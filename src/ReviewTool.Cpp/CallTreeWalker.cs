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
    private readonly HashSet<string> expanded_ = [];
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
            if (item["detail"] is null && candidate.ClassName is not null)
            {
                item["detail"] = $"{candidate.ClassName}::{item["name"]!.GetValue<string>()}";
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
            if (node.Stop is StopReason.External or StopReason.Hidden or StopReason.NoDefinition)
            {
                continue;
            }
            if (expanded_.Contains(id))
            {
                continue;
            }
            if (depth >= config.MaxDepth)
            {
                node.Stop = StopReason.DepthLimit;   // 다른 진입점에서 더 얕게 닿으면 그때 펼친다
                continue;
            }
            if (graph.Nodes.Count >= config.MaxNodes)
            {
                node.Stop = StopReason.NodeLimit;
                NodeLimitHit = true;
                continue;
            }
            expanded_.Add(id);
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
            foreach (var (call, _) in ordered)
            {
                if (call["to"] is not JsonObject to)
                {
                    continue;
                }
                var childId = await AddNodeAsync(to, ct);
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
                if (!expanded_.Contains(childId))
                {
                    queue.Enqueue((childId, depth + 1));
                }
            }
        }
    }

    private async Task RelocateItemAsync(string id, (string Path, int Line0, int Col) def, CancellationToken ct)
    {
        await session.OpenAsync(def.Path, ct);
        try
        {
            var moved = await session.PrepareCallHierarchyAsync(def.Path, def.Line0, def.Col, ct);
            if (moved is not null)
            {
                moved["detail"] ??= items_[id]["detail"]?.DeepClone();
                items_[id] = moved;
            }
        }
        finally
        {
            await session.CloseAsync(def.Path, ct);
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
                var def = await FindDefinitionAsync(name, qualified, ct);
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

    private async Task<(string Path, int Line0, int Col)?> FindDefinitionAsync(string name, string qualified, CancellationToken ct)
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
        foreach (var s in symbols.OfType<JsonObject>())
        {
            if (s["name"]?.GetValue<string>() != name)
            {
                continue;
            }
            var sc = s["containerName"]?.GetValue<string>() ?? "";
            if (container.Length > 0 && !container.EndsWith(sc, StringComparison.Ordinal) && !sc.EndsWith(container, StringComparison.Ordinal))
            {
                continue;
            }
            var loc = s["location"]!;
            var start = loc["range"]!["start"]!;
            return (PathUri.ToPath(loc["uri"]!.GetValue<string>()), start["line"]!.GetValue<int>(), start["character"]!.GetValue<int>());
        }
        return null;
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
