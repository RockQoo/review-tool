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
    // 심볼 ID 를 다른 정의와 나눠 가진 노드(여러 실행 파일의 main 등). 색인이 호출을 합쳐 주므로 제 본문 안 호출만 쓴다.
    private readonly HashSet<string> sharedSymbol_ = [];
    // 매크로가 만든 진입점(gtest TEST 등). 색인에 이 함수의 호출이 일부만 남아(실측) outgoingCalls 대신 본문을 직접 훑는다.
    private readonly HashSet<string> macroBodies_ = [];
    // 정의 위치(uri|줄|이름) → 한정 이름. 테스트마다 같은 함수를 부르므로 색인 조회를 한 번만 한다. 못 찾은 것은 null.
    private readonly Dictionary<string, string?> qualified_ = new(StringComparer.Ordinal);
    // 정의 위치(파일|줄|열|호출 글자) → 그 함수의 item. 없으면 null(함수가 아닌 이름).
    private readonly Dictionary<string, JsonObject?> callee_ = new(StringComparer.OrdinalIgnoreCase);
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
        if (candidate.Rule.Macro)
        {
            return await AddMacroEntryAsync(candidate, ct);
        }
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

    // 매크로가 만드는 함수(gtest 의 TEST(Suite, Name) 등)는 clangd 에 물어도 답이 없다(실측: 매크로 이름·인자 위치 모두 빈 답,
    // 만들어진 TestBody 는 workspace/symbol 로도 0개). 그래서 노드를 소스에서 직접 만든다: 이름은 정규식이 잡은 Suite::Name,
    // 본문은 매크로 줄 뒤 중괄호. 호출은 WalkAsync 가 BodyCallsAsync 로 본문을 훑어 찾는다.
    private async Task<string?> AddMacroEntryAsync(CppTraceAnalyzer.EntryCandidate candidate, CancellationToken ct)
    {
        var position = new JsonObject { ["line"] = candidate.Line - 1, ["character"] = candidate.Column };
        var item = new JsonObject
        {
            ["name"] = candidate.Method,
            ["detail"] = candidate.ClassName is null ? candidate.Method : $"{candidate.ClassName}::{candidate.Method}",
            ["kind"] = 12, // LSP SymbolKind.Function
            ["uri"] = PathUri.ToUri(candidate.File),
            ["range"] = new JsonObject { ["start"] = position.DeepClone(), ["end"] = position.DeepClone() },
            ["selectionRange"] = new JsonObject { ["start"] = position.DeepClone(), ["end"] = position.DeepClone() },
        };
        var id = await AddNodeAsync(item, ct, macroDefined: true);
        if (graph.Nodes[id].Stop == StopReason.NoDefinition)
        {
            Failures.Add($"매크로 진입점의 본문을 찾지 못함: {candidate.RelativeFile}:{candidate.Line} {candidate.Method}");
        }
        return id;
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
                calls = macroBodies_.Contains(id) ? await BodyCallsAsync(node, ct)
                                                  : await session.OutgoingCallsAsync(items_[id], ct);
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
                if (sharedSymbol_.Contains(id) && !InBody(call, node))
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

    // 심볼을 나눠 가진 노드 중 먼저 펼친 쪽은 그때 아직 몰랐으므로, 다 끝난 뒤 제 본문 밖 호출을 걷어 낸다.
    public int PruneSharedSymbols()
    {
        var removed = 0;
        foreach (var id in sharedSymbol_)
        {
            var node = graph.Nodes[id];
            foreach (var edge in graph.Edges.Where(e => e.From == id).ToList())
            {
                edge.CallSites.RemoveAll(s => s.Line < node.StartLine || s.Line > node.EndLine);
                if (edge.CallSites.Count == 0)
                {
                    graph.Edges.Remove(edge);
                    edges_.Remove((edge.From, edge.To));
                    removed++;
                }
            }
        }
        return removed;
    }

    // 매크로가 만든 함수의 호출 목록. 매크로 함수는 색인에 호출이 일부만 남고 이름으로도 찾을 수 없다(실측: gtest TEST 본문에서
    // 33개 중 본문 안 호출 3개, workspace/symbol 로 TestBody 0개). 본문은 진짜 코드라 호출 이름 위치를 물으면 잘 풀리므로,
    // 호출처럼 보이는 위치마다 clangd 에 그 자리의 함수를 물어 outgoingCalls 와 같은 모양({to, fromRanges})으로 만든다.
    private async Task<JsonArray> BodyCallsAsync(TraceNode node, CancellationToken ct)
    {
        // 정의 위치(PathUri.ToPath)와 같은 모양으로 맞춘다. 구분자가 섞이면 같은 파일을 다른 파일로 보고 열린 문서를 닫아 버린다.
        var path = Path.GetFullPath(Path.Combine(config.Root, node.File));
        var lines = ReadLines(path);
        var result = new JsonArray();
        var byCallee = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        await session.OpenAsync(path, ct);
        try
        {
            foreach (var (line, code) in BodyLines(lines, node.StartLine, node.EndLine))
            {
                foreach (Match m in CallSite.Matches(code))
                {
                    var at = m.Groups["at"];
                    if (NotACall.Contains(at.Value) || MacroName.IsMatch(at.Value))
                    {
                        continue;
                    }
                    var item = await CalleeAtAsync(path, line - 1, at.Index, at.Value, ct);
                    if (item is null)
                    {
                        continue;
                    }
                    await QualifyAsync(item, ct);
                    // 심볼 ID 가 없는 item(범위 밖 합성)은 정의 위치까지 넣어 가른다. 같은 헤더의 left::Send / right::Send 가
                    // 이름만으로 합쳐지지 않게. 범위 밖 노드를 한정 이름으로 합치는 것은 AddNodeAsync 가 한다.
                    var start = (item["selectionRange"] ?? item["range"])?["start"];
                    var key = item["data"]?.GetValue<string>() ??
                              $"{item["uri"]}|{start?["line"]}|{start?["character"]}|{item["name"]}";
                    if (!byCallee.TryGetValue(key, out var call))
                    {
                        // 응답 배열에 붙어 있는 노드라 복사해서 넣는다(JsonNode 는 부모를 하나만 가진다)
                        call = new JsonObject { ["to"] = item.DeepClone(), ["fromRanges"] = new JsonArray() };
                        byCallee[key] = call;
                        result.Add(call);
                    }
                    call["fromRanges"]!.AsArray().Add(new JsonObject
                    {
                        ["start"] = new JsonObject { ["line"] = line - 1, ["character"] = at.Index },
                        ["end"] = new JsonObject { ["line"] = line - 1, ["character"] = at.Index + at.Length },
                    });
                }
            }
        }
        finally
        {
            await session.CloseAsync(path, ct);
        }
        return result;
    }

    // 본문의 한 위치가 부르는 함수의 item. 호출 위치에서 바로 prepare 하면 연산자(<<)는 빈 답이고, 멤버 함수는 헤더 선언을 줘서
    // 소속을 가리기 어렵다(실측). 그래서 정의로 이동한 뒤 정의 위치에서 묻는다(색인 심볼 위치와 같아져 소속을 정확히 붙인다).
    // 범위 밖(표준 라이브러리 등) 정의는 그 헤더를 열지 않고 위치만으로 item 을 만든다(큰 헤더를 열면 느리다).
    private async Task<JsonObject?> CalleeAtAsync(string path, int line0, int col, string token, CancellationToken ct)
    {
        var def = await session.DefinitionAsync(path, line0, col, ct);
        if (def is null)
        {
            return null;
        }
        // 테스트마다 같은 함수를 부른다. 정의 위치마다 한 번만 묻는다(정의 파일을 열 때마다 clangd 가 다시 해석해 느리다).
        var cacheKey = $"{def.Value.Path}|{def.Value.Line0}|{def.Value.Col}|{token}";
        if (callee_.TryGetValue(cacheKey, out var cached))
        {
            return cached?.DeepClone() as JsonObject;
        }
        var resolved = await ResolveCalleeAsync(path, def.Value, token, ct);
        callee_[cacheKey] = resolved?.DeepClone() as JsonObject;
        return resolved;
    }

    private async Task<JsonObject?> ResolveCalleeAsync(string path, (string Path, int Line0, int Col) def, string token,
                                                       CancellationToken ct)
    {
        if (!IsInside(def.Path, out _))
        {
            var at = new JsonObject { ["line"] = def.Line0, ["character"] = def.Col };
            var name = token is "<<" or ">>" ? $"operator{token}" : token;
            return new JsonObject
            {
                ["name"] = name,
                ["kind"] = 12,
                ["uri"] = PathUri.ToUri(def.Path),
                ["range"] = new JsonObject { ["start"] = at.DeepClone(), ["end"] = at.DeepClone() },
                ["selectionRange"] = new JsonObject { ["start"] = at.DeepClone(), ["end"] = at.DeepClone() },
            };
        }
        // 이미 열려 있는 본문 파일이면 닫지 않도록 바로 묻는다
        return string.Equals(def.Path, path, StringComparison.OrdinalIgnoreCase)
            ? await session.PrepareCallHierarchyAsync(path, def.Line0, def.Col, ct)
            : await PrepareAtAsync(def.Path, def.Line0, def.Col, ct);
    }

    // prepare 결과에는 한정 이름(detail)이 없다. 같은 이름의 색인 심볼 중 위치가 같은 것의 소속(containerName)을 붙인다.
    // 못 찾으면 그대로 둔다(이름만 보이고, 같은 심볼이 다른 경로로 먼저 들어왔으면 그 노드로 합쳐진다).
    private async Task QualifyAsync(JsonObject item, CancellationToken ct)
    {
        if (item["detail"] is not null)
        {
            return;
        }
        var name = item["name"]!.GetValue<string>();
        var uri = item["uri"]!.GetValue<string>();
        var line = (item["selectionRange"] ?? item["range"])!["start"]!["line"]!.GetValue<int>();
        var key = $"{uri}|{line}|{name}";
        if (qualified_.TryGetValue(key, out var cached))
        {
            if (cached is not null)
            {
                item["detail"] = cached;
            }
            return;
        }
        qualified_[key] = null;
        try
        {
            foreach (var s in (await session.WorkspaceSymbolAsync(name, ct)).OfType<JsonObject>())
            {
                if (s["name"]?.GetValue<string>() == name && s["location"]?["uri"]?.GetValue<string>() == uri &&
                    s["location"]?["range"]?["start"]?["line"]?.GetValue<int>() == line &&
                    s["containerName"]?.GetValue<string>() is { Length: > 0 } container)
                {
                    item["detail"] = qualified_[key] = $"{container}::{name}";
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failures.Add($"한정 이름 조회 실패 {name}: {ex.Message}");
        }
    }

    // 호출처럼 보이는 곳: 이름(템플릿 인자 허용, 중첩 Box<Box<int>> 포함) 뒤 '(' , 또는 스트림 연산자 << >>.
    // 템플릿 인자는 균형 그룹으로 < > 짝을 맞춘다. 그래서 Foo<A<B>>( 의 닫는 >> 는 이름 쪽에 먹혀 연산자로 잡히지 않는다.
    private static readonly Regex CallSite = new(
        @"(?<at>\b[A-Za-z_]\w*)\s*(?:<(?>[^<>()]+|<(?<d>)|>(?<-d>))*(?(d)(?!))>)?\s*\(|(?<at><<|>>)", RegexOptions.Compiled);

    // 본문 중괄호 { } 안쪽만 남긴 줄(1부터 세는 줄, 열 위치 그대로). 첫 줄은 매크로 부분을 지우고, 첫 '{' 앞과
    // 짝이 맞는 '}' 뒤(같은 줄에 이어지는 다른 함수 등)도 지운다. 문자열·주석 속 중괄호는 CodeOnly 가 먼저 지운다.
    internal static IEnumerable<(int Line, string Code)> BodyLines(string[] lines, int startLine, int endLine)
    {
        var depth = 0;
        var started = false;
        for (var line = startLine; line <= Math.Min(endLine, lines.Length); line++)
        {
            var text = CodeOnly(lines[line - 1]);
            var code = (line == startLine ? BlankMacroHead(text) : text).ToCharArray();
            for (var i = 0; i < code.Length; i++)
            {
                if (!started)
                {
                    if (code[i] == '{')
                    {
                        started = true;
                        depth = 1;
                    }
                    else
                    {
                        code[i] = ' ';
                    }
                    continue;
                }
                if (code[i] == '{')
                {
                    depth++;
                }
                else if (code[i] == '}' && --depth == 0)
                {
                    Array.Fill(code, ' ', i + 1, code.Length - i - 1);
                    yield return (line, new string(code));
                    yield break;
                }
            }
            yield return (line, new string(code));
        }
    }

    // 첫 줄의 매크로 부분: 첫 '(' 부터 짝이 맞는 ')' 까지와 그 앞을 지운다. 짝을 못 찾으면 줄 전체를 지운다.
    internal static string BlankMacroHead(string code)
    {
        var open = code.IndexOf('(');
        if (open < 0)
        {
            return new string(' ', code.Length);
        }
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            depth += code[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return new string(' ', i + 1) + code[(i + 1)..];
            }
        }
        return new string(' ', code.Length);
    }
    // 대문자와 _ 만으로 된 이름은 매크로(EXPECT_EQ 등)로 보고 묻지 않는다. 인자 안의 호출은 따로 잡힌다.
    private static readonly Regex MacroName = new(@"^[A-Z][A-Z0-9_]+$", RegexOptions.Compiled);
    private static readonly HashSet<string> NotACall =
        ["if", "for", "while", "switch", "return", "sizeof", "alignof", "decltype", "catch", "static_cast",
         "reinterpret_cast", "const_cast", "dynamic_cast", "noexcept", "requires", "co_await", "co_return"];

    // 문자열·문자 리터럴과 // 주석을 공백으로 바꾼다(열 위치는 그대로). 그 안의 "Foo(" 를 호출로 묻지 않게.
    internal static string CodeOnly(string line)
    {
        var chars = line.ToCharArray();
        var quote = '\0';
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < chars.Length)
                {
                    chars[i] = chars[i + 1] = ' ';
                    i++;
                    continue;
                }
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    chars[i] = ' ';
                }
            }
            else if (c == '\'' && IsDigitSeparator(chars, i))
            {
                // 1'000 같은 숫자 구분자. 문자 리터럴이 아니다.
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                Array.Fill(chars, ' ', i, chars.Length - i);
                break;
            }
        }
        return new string(chars);
    }

    // 작은따옴표 앞 토큰이 숫자 리터럴(숫자로 시작: 1'000, 0xFF'FF, 0b1010'0101)이면 숫자 구분자다.
    // u8'a' 처럼 글자로 시작하는 토큰 뒤라면 문자 리터럴이다.
    private static bool IsDigitSeparator(char[] chars, int quote)
    {
        if (quote + 1 >= chars.Length || !char.IsAsciiHexDigit(chars[quote + 1]))
        {
            return false;
        }
        var start = quote;
        while (start > 0 && (char.IsAsciiLetterOrDigit(chars[start - 1]) || chars[start - 1] is '\'' or '.'))
        {
            start--;
        }
        return start < quote && char.IsAsciiDigit(chars[start]);
    }

    private static bool InBody(JsonObject call, TraceNode node) =>
        call["fromRanges"]?.AsArray().OfType<JsonObject>().Any(r =>
            r["start"]?["line"]?.GetValue<int>() + 1 is { } l && l >= node.StartLine && l <= node.EndLine) == true;

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

    // macroDefined: 매크로가 만든 진입점. 심볼 ID 를 같은 매크로의 다른 함수와 나눠 가지므로 위치(파일:줄)로 노드를 가르고
    // 제 본문 안 호출만 쓴다(sharedSymbol_). 심볼 → 노드 표에는 넣지 않는다(다른 함수가 이 노드로 합쳐지지 않게).
    private async Task<string> AddNodeAsync(JsonObject item, CancellationToken ct, bool macroDefined = false)
    {
        var name = item["name"]!.GetValue<string>();
        var qualified = item["detail"]?.GetValue<string>() is { Length: > 0 } d ? d : name;
        var symbol = item["data"]?.GetValue<string>();
        var path = PathUri.ToPath(item["uri"]!.GetValue<string>());
        var sel = (item["selectionRange"] ?? item["range"])!["start"]!;
        var line0 = sel["line"]!.GetValue<int>();
        var col = sel["character"]!.GetValue<int>();
        var inside = IsInside(path, out var rel);

        var split = macroDefined;
        if (!macroDefined && symbol is not null && idBySymbol_.TryGetValue(symbol, out var known))
        {
            // 프로세스마다 있는 main 처럼 다른 파일에 정의가 또 있으면 심볼 ID 가 같아도 다른 함수다.
            var first = graph.Nodes[known];
            if (!inside || first.Kind == NodeKind.External || string.Equals(first.File, rel, StringComparison.OrdinalIgnoreCase) ||
                !FunctionBody.Find(ReadLines(path), line0, col).HasBody)
            {
                return known;
            }
            split = true;
            sharedSymbol_.Add(known);
        }

        // 범위 밖 함수는 템플릿 인스턴스마다 심볼이 달라 같은 이름이 줄줄이 생긴다. 이름 하나로 합친다.
        var id = !inside ? $"ext:{qualified}"
               : macroDefined ? $"{qualified}@{rel}:{line0 + 1}"
               : split ? $"{qualified}@{symbol}@{rel}"
               : symbol is not null ? $"{qualified}@{symbol}"
               : $"{qualified}@{rel}:{line0 + 1}";
        // 매크로 노드는 BodyCallsAsync 가 처음부터 제 본문 호출만 만들므로 공유 심볼 후처리(간선 전체 재검색)에 넣지 않는다
        if (split && !macroDefined)
        {
            sharedSymbol_.Add(id);
        }
        if (macroDefined)
        {
            macroBodies_.Add(id);
        }
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
            if (!body.HasBody && !macroDefined)
            {
                // 선언 위치였다 → 색인에서 정의를 찾는다(매크로 진입점은 색인에 없으므로 찾지 않는다)
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
        if (symbol is not null && !split)
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
