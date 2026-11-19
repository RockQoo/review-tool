using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReviewTool.Core;

namespace ReviewTool.Cpp;

// 진입점에서 호출 트리를 따라 내려가며 그래프를 채운다. 같은 함수는 한 번만 펼친다(여러 진입점이 공유).
//
// clangd 사용 요령(실측):
// - outgoingCalls 는 item 의 data(심볼 ID)로 색인을 조회하므로, item 위치가 헤더 선언이어도 동작한다.
// - 대신 item 의 range 는 이름 토큰뿐이고, 위치가 선언일 때도 있다. 그래서 정의 위치는
//   workspace/symbol 로 다시 찾고, 본문 범위는 FunctionBody 로 소스에서 직접 찾는다.
internal sealed class CallTreeWalker(TargetConfig config, TraceGraph graph, ClangdSession session, Func<string, bool> filter)
{
    private readonly Dictionary<string, JsonObject> items_ = [];
    private readonly Dictionary<string, string> idBySymbol_ = [];
    // 펼친 노드와 그때의 깊이. 다른 진입점에서 더 얕게 닿으면 남은 깊이만큼 아래를 다시 따라간다
    // (진입점 처리 순서에 따라 결과가 달라지지 않게).
    private readonly Dictionary<string, int> expandedDepth_ = [];
    private readonly Dictionary<string, List<string>> children_ = [];
    private readonly Dictionary<(string, string), TraceEdge> edges_ = [];
    private readonly Regex[] hide_ = config.HideCallees.Select(p => new Regex(p)).ToArray();
    private readonly Dictionary<string, string[]> fileCache_ = new(StringComparer.OrdinalIgnoreCase);
    // 선언 위치로 받은 item 을 정의 위치로 옮겨야 하는 노드. outgoingCalls 의 fromRanges 는 item 이
    // 가리키는 파일 기준으로만 나오므로, 헤더 선언 item 으로 물으면 호출 위치가 비어 버린다.
    private readonly Dictionary<string, (string Path, int Line0, int Col)> relocate_ = [];

    public List<string> Failures { get; } = [];
    public bool NodeLimitHit { get; private set; }

    public async Task<string?> ResolveEntryAsync(CppTraceAnalyzer.EntryCandidate candidate, CancellationToken ct)
    {
        await session.OpenAsync(candidate.File, ct);
        try
        {
            var item = await session.PrepareCallHierarchyAsync(candidate.File, candidate.Line - 1, candidate.Column, ct);
            if (item is null)
            {
                return null;
            }
            // prepare 결과에는 detail(한정 이름)이 없어서 정규식으로 잡은 클래스 이름을 붙인다.
            // .cpp 의 "void C::M(...)" 정의 위치에서 받으면 name 이 이미 "C::M" 이다(그때는 붙이지 않는다).
            if (item["detail"] is null && candidate.ClassName is not null)
            {
                var name = item["name"]!.GetValue<string>();
                item["detail"] = name.Contains("::") ? name : $"{candidate.ClassName}::{name}";
            }
            return await AddNodeAsync(item, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failures.Add($"진입점 해석 실패 {candidate.RelativeFile}:{candidate.Line}: {ex.Message}");
            return null;
        }
        finally
        {
            await session.CloseAsync(candidate.File, ct);
        }
    }

    public async Task WalkAsync(string rootId, CancellationToken ct)
    {
        // 너비 우선: 깊이 제한에 걸려도 얕은 쪽이 먼저 다 채워진다.
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((rootId, 0));
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (id, depth) = queue.Dequeue();
            var node = graph.Nodes[id];
            if (node.Stop is StopReason.External or StopReason.Hidden or StopReason.NoDefinition or StopReason.Failed)
            {
                continue;
            }
            if (depth >= config.MaxDepth)
            {
                if (!expandedDepth_.ContainsKey(id))
                {
                    node.Stop = StopReason.DepthLimit;   // 다른 진입점에서 더 얕게 닿으면 그때 펼친다
                }
                continue;
            }
            if (expandedDepth_.TryGetValue(id, out var seenDepth))
            {
                // 이미 펼쳤다. 이번이 더 얕으면 아래쪽을 남은 깊이만큼 다시 따라간다(호출 목록은 이미 있음).
                if (depth < seenDepth)
                {
                    expandedDepth_[id] = depth;
                    foreach (var child in children_.GetValueOrDefault(id) ?? [])
                    {
                        queue.Enqueue((child, depth + 1));
                    }
                }
                continue;
            }
            if (graph.Nodes.Count >= config.MaxNodes)
            {
                node.Stop = StopReason.NodeLimit;
                NodeLimitHit = true;
                continue;
            }
            expandedDepth_[id] = depth;
            node.Stop = null;

            JsonArray calls;
            try
            {
                if (relocate_.Remove(id, out var def))
                {
                    await RelocateItemAsync(id, def, ct);
                }
                calls = await session.OutgoingCallsAsync(items_[id], ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                node.Stop = StopReason.Failed;
                Failures.Add($"호출 조회 실패 {node.Name}: {ex.Message}");
                continue;
            }

            // 소스 순서대로 보이도록 첫 호출 위치로 정렬
            var ordered = calls.OfType<JsonObject>()
                .Select(c => (Call: c, First: FirstRange(c)))
                .OrderBy(c => c.First.Line).ThenBy(c => c.First.Character)
                .ToList();
            var kids = children_[id] = [];
            var skipped = 0;
            foreach (var (call, _) in ordered)
            {
                if (call["to"] is not JsonObject to)
                {
                    continue;
                }
                // 노드 상한은 새 노드를 실제로 만들 때 검사한다(이미 있는 함수로 가는 선은 그대로 잇는다)
                if (!IsKnown(to) && graph.Nodes.Count >= config.MaxNodes)
                {
                    skipped++;
                    continue;
                }
                var childId = await AddNodeAsync(to, ct);
                if (!kids.Contains(childId))
                {
                    kids.Add(childId);
                }
                var edge = GetEdge(id, childId);
                foreach (var range in call["fromRanges"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    var start = range["start"]!;
                    var site = new SourceRef
                    {
                        // fromRanges 는 호출하는 쪽 본문(정의 파일) 기준이다
                        File = node.File,
                        Line = start["line"]!.GetValue<int>() + 1,
                        Column = start["character"]!.GetValue<int>(),
                    };
                    if (!edge.CallSites.Any(s => s.Line == site.Line && s.Column == site.Column))
                    {
                        edge.CallSites.Add(site);
                    }
                }
                queue.Enqueue((childId, depth + 1));
            }
            if (skipped > 0)
            {
                node.Stop = StopReason.NodeLimit;
                NodeLimitHit = true;
                Failures.Add($"노드 수 제한으로 {node.Name} 의 호출 {skipped}개를 생략함");
            }
        }
    }

    private bool IsKnown(JsonObject item)
    {
        var symbol = item["data"]?.GetValue<string>();
        if (symbol is not null && idBySymbol_.ContainsKey(symbol))
        {
            return true;
        }
        var path = PathUri.ToPath(item["uri"]!.GetValue<string>());
        var qualified = item["detail"]?.GetValue<string>() is { Length: > 0 } d ? d : item["name"]!.GetValue<string>();
        return !IsInside(path, out _) && graph.Nodes.ContainsKey($"ext:{qualified}");
    }

    private async Task RelocateItemAsync(string id, (string Path, int Line0, int Col) def, CancellationToken ct)
    {
        var moved = await PrepareAtAsync(def.Path, def.Line0, def.Col, ct);
        if (moved is null)
        {
            return;
        }
        // 정의 위치에서 다시 받은 심볼이 원래 것과 같아야 한다(다른 오버로드로 바뀌면 안 됨)
        var original = items_[id]["data"]?.GetValue<string>();
        var relocated = moved["data"]?.GetValue<string>();
        if (original is not null && relocated is not null && original != relocated)
        {
            Failures.Add($"정의 위치의 심볼이 달라 옮기지 않음: {graph.Nodes[id].Name} ({def.Path}:{def.Line0 + 1})");
            return;
        }
        moved["detail"] ??= items_[id]["detail"]?.DeepClone();
        items_[id] = moved;
    }

    private async Task<JsonObject?> PrepareAtAsync(string path, int line0, int col, CancellationToken ct)
    {
        await session.OpenAsync(path, ct);
        try
        {
            return await session.PrepareCallHierarchyAsync(path, line0, col, ct);
        }
        finally
        {
            await session.CloseAsync(path, ct);
        }
    }

    private static (int Line, int Character) FirstRange(JsonObject call)
    {
        var start = call["fromRanges"]?.AsArray().FirstOrDefault()?["start"];
        return start is null ? (int.MaxValue, 0) : (start["line"]!.GetValue<int>(), start["character"]!.GetValue<int>());
    }

    private TraceEdge GetEdge(string from, string to)
    {
        if (!edges_.TryGetValue((from, to), out var edge))
        {
            edge = new TraceEdge { From = from, To = to };
            edges_[(from, to)] = edge;
            graph.Edges.Add(edge);
        }
        return edge;
    }

    private async Task<string> AddNodeAsync(JsonObject item, CancellationToken ct)
    {
        var name = item["name"]!.GetValue<string>();
        var qualified = item["detail"]?.GetValue<string>() is { Length: > 0 } d ? d : name;
        var symbol = item["data"]?.GetValue<string>();
        if (symbol is not null && idBySymbol_.TryGetValue(symbol, out var known))
        {
            return known;
        }

        var path = PathUri.ToPath(item["uri"]!.GetValue<string>());
        var sel = (item["selectionRange"] ?? item["range"])!["start"]!;
        var line0 = sel["line"]!.GetValue<int>();
        var col = sel["character"]!.GetValue<int>();

        var inside = IsInside(path, out var rel);
        // 범위 밖 함수는 템플릿 인스턴스마다 심볼이 달라 같은 이름이 줄줄이 생긴다. 이름 하나로 합친다.
        var id = !inside ? $"ext:{qualified}"
               : symbol is not null ? $"{qualified}@{symbol}"
               : $"{qualified}@{rel}:{line0 + 1}";
        if (graph.Nodes.ContainsKey(id))
        {
            return id;
        }

        var node = new TraceNode
        {
            Id = id,
            Name = qualified,
            Kind = KindOf(item),
        };

        if (!inside)
        {
            node.Kind = NodeKind.External;
            node.File = path;
            node.StartLine = node.EndLine = line0 + 1;
            node.Stop = StopReason.External;
        }
        else
        {
            var body = FunctionBody.Find(ReadLines(path), line0, col);
            if (!body.HasBody)
            {
                // 선언 위치였다 → 색인에서 정의를 찾는다
                var def = await FindDefinitionAsync(name, qualified, (path, line0, col), ct);
                if (def is not null && IsInside(def.Value.Path, out var defRel))
                {
                    var defBody = FunctionBody.Find(ReadLines(def.Value.Path), def.Value.Line0, def.Value.Col);
                    if (defBody.HasBody)
                    {
                        path = def.Value.Path;
                        rel = defRel;
                        body = defBody;
                        relocate_[id] = def.Value;
                    }
                }
            }
            node.File = rel;
            node.StartLine = body.StartLine;
            node.EndLine = body.EndLine;
            var lines = ReadLines(path);
            var count = Math.Max(0, Math.Min(body.EndLine, lines.Length) - body.StartLine + 1);
            var take = Math.Min(count, config.MaxCodeLines);
            node.Code = string.Join('\n', lines.Skip(body.StartLine - 1).Take(take));
            node.CodeTruncated = take < count;
            var full = node.CodeTruncated ? string.Join('\n', lines.Skip(body.StartLine - 1).Take(count)) : node.Code;
            node.Signature = CodeFingerprint.Signature(full);
            node.BodyHash = CodeFingerprint.Body(full);
            if (!body.HasBody)
            {
                node.Stop = StopReason.NoDefinition;
            }
            if (hide_.Any(r => r.IsMatch(qualified)))
            {
                node.Stop = StopReason.Hidden;
            }
        }

        graph.Nodes[id] = node;
        items_[id] = item;
        if (symbol is not null)
        {
            idBySymbol_[symbol] = id;
        }
        return id;
    }

    // 이름으로 색인을 찾으면 같은 이름의 오버로드·다른 클래스가 섞여 나온다. 범위가 맞는 후보가 하나면 그것을,
    // 여럿이면 선언과 매개변수 타입이 같은 후보 하나만 쓴다. 못 가르면 쓰지 않는다(진단에 남김).
    private async Task<(string Path, int Line0, int Col)?> FindDefinitionAsync(string name, string qualified,
                                                                              (string Path, int Line0, int Col) declaration,
                                                                              CancellationToken ct)
    {
        JsonArray symbols;
        try
        {
            symbols = await session.WorkspaceSymbolAsync(qualified, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failures.Add($"정의 검색 실패 {qualified}: {ex.Message}");
            return null;
        }
        var container = qualified.Contains("::") ? qualified[..qualified.LastIndexOf("::", StringComparison.Ordinal)] : "";
        var candidates = new List<(string Path, int Line0, int Col)>();
        foreach (var s in symbols.OfType<JsonObject>())
        {
            if (s["name"]?.GetValue<string>() != name || !SameScope(container, s["containerName"]?.GetValue<string>() ?? ""))
            {
                continue;
            }
            var loc = s["location"]!;
            var start = loc["range"]!["start"]!;
            candidates.Add((PathUri.ToPath(loc["uri"]!.GetValue<string>()), start["line"]!.GetValue<int>(), start["character"]!.GetValue<int>()));
        }
        if (candidates.Count <= 1)
        {
            return candidates.Count == 1 ? candidates[0] : null;
        }
        // 오버로드는 매개변수 목록(소스 텍스트)으로 가른다. clangd 에 파일을 열어 심볼 ID 로 확인하는 방법은 쓰지 않는다:
        // 연 파일의 동적 색인이 헤더 인라인 함수의 호출 기록을 가려 outgoingCalls 가 비는 것을 실측했다.
        var wanted = FunctionBody.ParameterTypes(ReadLines(declaration.Path), declaration.Line0, declaration.Col);
        if (wanted is not null)
        {
            var matches = candidates
                .Where(c => IsInside(c.Path, out _) &&
                            FunctionBody.ParameterTypes(ReadLines(c.Path), c.Line0, c.Col) is { } p && p.SequenceEqual(wanted))
                .ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }
        }
        Failures.Add($"정의 후보가 {candidates.Count}개라 고르지 못함(오버로드?): {qualified}");
        return null;
    }

    // 범위 이름 비교. "A::B" 와 "B" 는 같은 것으로 보지만, 빈 범위는 빈 범위끼리만 같다.
    internal static bool SameScope(string expected, string actual)
    {
        if (expected.Length == 0 || actual.Length == 0)
        {
            return expected.Length == actual.Length;
        }
        return expected == actual ||
               expected.EndsWith("::" + actual, StringComparison.Ordinal) ||
               actual.EndsWith("::" + expected, StringComparison.Ordinal);
    }

    private bool IsInside(string path, out string rel)
    {
        rel = Path.GetRelativePath(config.Root, path).Replace('\\', '/');
        return !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel) && filter(rel) &&
               !CompileDatabase.IsBuildOutput(rel);
    }

    private string[] ReadLines(string path)
    {
        if (!fileCache_.TryGetValue(path, out var lines))
        {
            lines = File.Exists(path) ? File.ReadAllLines(path) : [];
            fileCache_[path] = lines;
        }
        return lines;
    }

    private static NodeKind KindOf(JsonObject item) => item["kind"]?.GetValue<int>() switch
    {
        6 => NodeKind.Method,
        9 => NodeKind.Constructor,
        _ => NodeKind.Function,
    };
}
