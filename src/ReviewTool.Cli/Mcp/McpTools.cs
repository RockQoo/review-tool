using System.Text;
using System.Text.Json.Nodes;
using ReviewTool.Core;
using ReviewTool.Cpp;

namespace ReviewTool.Cli.Mcp;

// MCP 로 내보내는 조회 도구. 저장된 분석 결과(data/<대상>/trace.json)만 읽는다. 분석을 돌리거나 파일을 쓰지 않는다.
public sealed class McpTools(string targetsDirectory, string dataRoot)
{
    private readonly Dictionary<string, (DateTime Stamp, TraceGraph Graph, TraceIndex Index)> cache_ = [];

    private sealed record Tool(string Name, string Description, JsonObject Schema, Func<JsonObject, string> Run);

    private IReadOnlyList<Tool> Tools => tools_ ??=
    [
        new("list_targets", "분석 대상 목록과 각 대상의 분석 시각·규모·신선도(분석 뒤 코드가 바뀌었는지).",
            Schema(), _ => ListTargets()),
        new("get_entrypoints", "대상의 진입점(패킷 핸들러) 목록. filter 로 이름·그룹 일부를 걸러 낸다.",
            Schema(("target", "string", "대상 이름", true), ("filter", "string", "이름·그룹에 들어간 글자(선택)", false)),
            a => Entrypoints(Target(a), Opt(a, "filter"))),
        new("find_functions", "함수 이름(일부 가능)으로 함수 id 를 찾는다. 다른 도구의 function 인자에 id 나 이름을 넣는다.",
            Schema(("target", "string", "대상 이름", true), ("query", "string", "함수 이름 또는 일부", true)),
            a => Find(Target(a), Req(a, "query"))),
        new("get_flow", "함수(보통 진입점)에서 시작하는 호출 트리. 추정 연결·멈춘 이유·구현 후보를 표시한다.",
            Schema(("target", "string", "대상 이름", true), ("function", "string", "함수 id 또는 이름", true),
                   ("depth", "integer", "펼칠 깊이(기본 4, 최대 12)", false)),
            a => Flow(Target(a), Req(a, "function"), Math.Clamp(Int(a, "depth") ?? 4, 1, 12))),
        new("get_source", "함수 하나의 코드(줄 번호, 줄마다 부르는 함수 표시)와 이 함수를 부르는 곳.",
            Schema(("target", "string", "대상 이름", true), ("function", "string", "함수 id 또는 이름", true)),
            a => Source(Target(a), Req(a, "function"))),
        new("get_flow_context", "진입점 → … → 함수 경로 전체를 Markdown 으로(각 함수 코드, 호출 근거 파일:줄, 못 본 것). " +
                                "entry 를 주지 않으면 이 함수에 닿는 가장 짧은 진입점 경로를 쓴다.",
            Schema(("target", "string", "대상 이름", true), ("function", "string", "끝 함수 id 또는 이름", true),
                   ("entry", "string", "시작 진입점 함수 id 또는 이름(선택)", false),
                   ("include_code", "boolean", "코드 본문 포함(기본 true)", false)),
            a => FlowContextText(Target(a), Req(a, "function"), Opt(a, "entry"), Bool(a, "include_code") ?? true)),
        new("get_impact", "이 함수를 바꾸면 영향받는 진입점(패킷)과 각 대표 경로. 호출자를 거슬러 올라간다.",
            Schema(("target", "string", "대상 이름", true), ("function", "string", "함수 id 또는 이름", true)),
            a => Impact(Target(a), Req(a, "function"))),
        new("compare", "기준 분석(수정 전)과 지금 분석의 구조 차이: 진입점·함수(본문/시그니처/추가/삭제)·호출 변화.",
            Schema(("target", "string", "대상 이름", true),
                   ("baseline", "string", "기준 id(기본 previous = 직전 분석). list_targets 에 기준 목록이 나온다", false)),
            a => Compare(Target(a), Opt(a, "baseline") ?? SnapshotStore.PreviousId)),
    ];

    private IReadOnlyList<Tool>? tools_;

    public JsonArray List()
    {
        var array = new JsonArray();
        foreach (var t in Tools)
        {
            array.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.Schema.DeepClone(),
                ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
            });
        }
        return array;
    }

    public string Call(string name, JsonObject args)
    {
        var tool = Tools.FirstOrDefault(t => t.Name == name) ?? throw new ToolError($"없는 도구: {name}");
        return tool.Run(args);
    }

    // ---- 도구 ----

    private string ListTargets()
    {
        var sb = new StringBuilder();
        var names = TargetNames();
        if (names.Count == 0)
        {
            return $"대상이 없습니다. review-tool init <폴더> 로 만드세요. (찾은 곳: {targetsDirectory})";
        }
        foreach (var name in names)
        {
            sb.Append("## ").Append(name).Append('\n');
            TargetConfig config;
            try
            {
                config = LoadConfig(name);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                sb.Append("- 설정을 읽지 못함: ").Append(ex.Message).Append("\n\n");
                continue;
            }
            var loaded = TryGraph(name);
            if (loaded is null)
            {
                sb.Append("- 분석 결과 없음(review-tool analyze 또는 화면의 재분석)\n\n");
                continue;
            }
            var g = loaded.Value.Graph;
            sb.Append("- 언어 ").Append(g.Language).Append(" · 분석 ").Append(g.AnalyzedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
              .Append(" · 진입점 ").Append(g.EntryPoints.Count).Append(" · 함수 ").Append(g.Nodes.Count).Append(" · 호출 ").Append(g.Edges.Count).Append('\n');
            sb.Append("- 신선도: ").Append(FreshnessText(config, g)).Append('\n');
            var snaps = SnapshotStore.List(Paths(config));
            if (snaps.Count > 0)
            {
                sb.Append("- 비교 기준: ").Append(string.Join(", ", snaps.Select(s => $"{s.Id}({s.AnalyzedAt.ToLocalTime():MM-dd HH:mm})"))).Append('\n');
            }
            if (g.Diagnostics.Count > 0)
            {
                sb.Append("- 못 본 것 ").Append(g.Diagnostics.Count).Append("개: ").Append(string.Join(" / ", g.Diagnostics.Take(3))).Append('\n');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string FreshnessText(TargetConfig config, TraceGraph g)
    {
        try
        {
            ITraceAnalyzer analyzer = config.Language == "cpp" ? new CppTraceAnalyzer() : throw new NotSupportedException(config.Language);
            var r = Freshness.Evaluate(g, analyzer.EnumerateInputs(config), config.ContentHash());
            return r.State switch
            {
                FreshnessState.Fresh => "최신",
                FreshnessState.Stale => $"바뀜 {r.Count}개 — 결과의 코드·줄 번호가 지금과 다를 수 있음 ({string.Join(", ", r.Changed.Concat(r.Added).Take(5))})",
                _ => "확인 불가: " + r.Reason,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "확인 불가: " + ex.Message;
        }
    }

    private static string Entrypoints((TraceGraph G, TraceIndex I) t, string? filter)
    {
        var sb = new StringBuilder();
        var list = t.G.EntryPoints.Where(e => filter is null ||
                                              e.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                              e.Group.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var group in list.GroupBy(e => e.Group))
        {
            sb.Append("## ").Append(group.Key).Append('\n');
            foreach (var e in group)
            {
                sb.Append("- ").Append(e.Label).Append("  id=").Append(e.NodeId).Append("  등록 ").Append(e.RegisteredAt).Append('\n');
            }
        }
        return list.Count == 0 ? "맞는 진입점이 없습니다." : $"진입점 {list.Count}개\n\n{sb}";
    }

    private static string Find((TraceGraph G, TraceIndex I) t, string query)
    {
        var found = TraceQueries.FindFunctions(t.G, query, 50);
        if (found.Count == 0)
        {
            return "찾지 못했습니다. (분석에서 진입점으로부터 닿은 함수만 있습니다)";
        }
        var entries = t.G.EntryPoints.Select(e => e.NodeId).ToHashSet();
        return string.Join('\n', found.Select(n =>
            $"- {n.Name}{(n.Kind == NodeKind.External ? "  [범위 밖]" : $"  {n.File}:{n.StartLine}")}{(entries.Contains(n.Id) ? "  [진입점]" : "")}  id={n.Id}"));
    }

    private static string Flow((TraceGraph G, TraceIndex I) t, string function, int depth)
    {
        var node = Resolve(t.G, function);
        return TraceQueries.CallTreeText(t.G, t.I, node.Id, depth, maxLines: 400) +
               "\n표시: [추정 연결] 정적 호출이 아닌 등록 관계 · [멈춤] 더 펼치지 않은 이유 · (위에서 펼침) 이미 나온 함수\n";
    }

    private static string Source((TraceGraph G, TraceIndex I) t, string function)
    {
        var n = Resolve(t.G, function);
        var sb = new StringBuilder();
        sb.Append("# ").Append(n.Name).Append('\n');
        if (n.Kind == NodeKind.External)
        {
            return sb.Append("분석 범위 밖 함수라 코드가 없습니다.\n").ToString();
        }
        sb.Append(n.File).Append(':').Append(n.StartLine).Append('-').Append(n.EndLine).Append("  id=").Append(n.Id).Append("\n\n```\n")
          .Append(TraceQueries.NumberedCode(t.G, t.I, n)).Append("```\n");
        if (n.Stop is { } stop && stop != StopReason.External)
        {
            sb.Append("멈춤: ").Append(TraceQueries.StopText(stop)).Append('\n');
        }
        foreach (var im in n.Implementations)
        {
            sb.Append("구현 후보(추정): ").Append(im.Name).Append(' ').Append(im.File).Append(':').Append(im.Line).Append('\n');
        }
        var callers = t.I.Callers.GetValueOrDefault(n.Id) ?? [];
        sb.Append("\n부르는 곳 ").Append(callers.Count).Append("개\n");
        foreach (var c in callers)
        {
            var cn = t.G.Nodes[c];
            sb.Append("- ").Append(cn.Name).Append("  ").Append(cn.File).Append(':').Append(string.Join(",", t.I.CallLines(c, n.Id)))
              .Append("  id=").Append(c).Append('\n');
        }
        return sb.ToString();
    }

    private static string FlowContextText((TraceGraph G, TraceIndex I) t, string function, string? entry, bool includeCode)
    {
        var target = Resolve(t.G, function);
        List<string>? path;
        if (entry is not null)
        {
            var start = Resolve(t.G, entry);
            path = TraceQueries.PathFrom(t.G, t.I, start.Id, target.Id)
                   ?? throw new ToolError($"{start.Name} 에서 {target.Name} 까지 가는 호출 경로가 분석 결과에 없습니다.");
        }
        else
        {
            var hit = ImpactAnalyzer.Analyze(t.G, target.Id).Entries.OrderBy(e => e.Path.Count).FirstOrDefault()
                      ?? throw new ToolError($"{target.Name} 에 닿는 진입점을 찾지 못했습니다.");
            path = [.. hit.Path];
        }
        return FlowContext.ToMarkdown(t.G, path, new FlowContext.Options { IncludeCode = includeCode });
    }

    private static string Impact((TraceGraph G, TraceIndex I) t, string function)
    {
        var n = Resolve(t.G, function);
        var r = ImpactAnalyzer.Analyze(t.G, n.Id);
        var sb = new StringBuilder();
        sb.Append(n.Name).Append(" 을(를) 바꾸면 영향받는 진입점 ").Append(r.Entries.Count).Append("개 (거슬러 올라간 함수 ").Append(r.CallerCount).Append("개)\n\n");
        foreach (var e in r.Entries)
        {
            sb.Append("- ").Append(e.Entry.Label).Append(" · ").Append(e.Entry.Group).Append(": ")
              .Append(string.Join(" → ", e.Path.Select(p => TraceQueries.ShortName(t.G.Nodes[p].Name)))).Append('\n');
        }
        sb.Append("\n증명하지 않는 것:\n");
        foreach (var l in ImpactAnalyzer.Limits)
        {
            sb.Append("- ").Append(l).Append('\n');
        }
        return sb.ToString();
    }

    private string Compare((TraceGraph G, TraceIndex I) t, string baseline)
    {
        var config = LoadConfig(t.G.TargetName);
        var before = SnapshotStore.Load(Paths(config), baseline)
                     ?? throw new ToolError($"기준 {baseline} 이 없습니다. list_targets 의 비교 기준을 보세요.");
        var r = SnapshotComparer.Compare(before, t.G);
        var sb = new StringBuilder();
        sb.Append("기준 ").Append(baseline).Append(" (").Append(before.AnalyzedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")).Append(") → 지금 (")
          .Append(t.G.AnalyzedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")).Append(")\n");
        foreach (var w in r.ConditionWarnings)
        {
            sb.Append("! ").Append(w).Append('\n');
        }
        if (!r.HasChanges)
        {
            sb.Append("\n구조 차이 없음\n");
        }
        void Section(string title, IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count == 0)
            {
                return;
            }
            sb.Append("\n## ").Append(title).Append(' ').Append(list.Count).Append('\n');
            foreach (var i in list.Take(100))
            {
                sb.Append("- ").Append(i).Append('\n');
            }
            if (list.Count > 100)
            {
                sb.Append("- … ").Append(list.Count - 100).Append("개 더\n");
            }
        }
        static string F(SnapshotComparer.FunctionRef f) => $"{f.Name} ({f.File}:{f.Line})";
        Section("본문 바뀜", r.BodyChanged.Select(F));
        Section("시그니처 바뀜", r.SignatureChanged.Select(p => $"{F(p.Before)} → {F(p.After)}"));
        Section("함수 추가", r.FunctionsAdded.Select(F));
        Section("함수 삭제", r.FunctionsRemoved.Select(F));
        Section("이름 변경 후보(본문 같음)", r.RenameCandidates.Select(p => $"{p.Before.Name} → {p.After.Name}"));
        Section("호출 추가", r.CallsAdded.Select(c => $"{c.From} → {c.To}"));
        Section("호출 삭제", r.CallsRemoved.Select(c => $"{c.From} → {c.To}"));
        Section("진입점 추가", r.EntriesAdded);
        Section("진입점 삭제", r.EntriesRemoved);
        sb.Append("\n증명하지 않는 것:\n");
        foreach (var l in SnapshotComparer.Limits)
        {
            sb.Append("- ").Append(l).Append('\n');
        }
        return sb.ToString();
    }

    // ---- 대상·결과 읽기 ----

    private List<string> TargetNames() =>
        Directory.Exists(targetsDirectory)
            ? Directory.EnumerateFiles(targetsDirectory, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).Order().ToList()
            : [];

    private TargetConfig LoadConfig(string name)
    {
        if (!TargetNames().Contains(name))
        {
            throw new ToolError($"대상 {name} 이 없습니다. 있는 대상: {string.Join(", ", TargetNames())}");
        }
        return TargetConfig.Load(Path.Combine(targetsDirectory, name + ".json"));
    }

    private WorkPaths Paths(TargetConfig config) => new(dataRoot, config.Name);

    private (TraceGraph Graph, TraceIndex Index)? TryGraph(string name)
    {
        var config = LoadConfig(name);
        var file = Paths(config).TraceFile;
        if (!File.Exists(file))
        {
            return null;
        }
        var stamp = File.GetLastWriteTimeUtc(file);
        lock (cache_)
        {
            if (cache_.TryGetValue(name, out var c) && c.Stamp == stamp)
            {
                return (c.Graph, c.Index);
            }
        }
        var graph = TraceStore.TryLoad(file);
        if (graph is null)
        {
            return null;
        }
        var index = new TraceIndex(graph);
        lock (cache_)
        {
            cache_[name] = (stamp, graph, index);
        }
        return (graph, index);
    }

    private (TraceGraph G, TraceIndex I) Target(JsonObject a)
    {
        var name = Req(a, "target");
        return TryGraph(name) ?? throw new ToolError($"대상 {name} 의 분석 결과가 없습니다. 먼저 분석하세요.");
    }

    private static TraceNode Resolve(TraceGraph g, string function)
    {
        var found = TraceQueries.FindFunctions(g, function, 20);
        return found.Count switch
        {
            0 => throw new ToolError($"함수 {function} 을(를) 찾지 못했습니다. find_functions 로 찾아 보세요."),
            1 => found[0],
            // FindFunctions 는 이름이 정확히 같은 것이 있으면 그것만 준다: 여럿이면 오버로드나 파일별 static 함수
            _ => throw new ToolError($"{function} 에 맞는 함수가 여러 개입니다. id 로 지정하세요:\n" +
                                      string.Join('\n', found.Select(n => $"- {n.Name}  {n.File}:{n.StartLine}  id={n.Id}"))),
        };
    }

    // ---- 인자 ----

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in props)
        {
            properties[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Required)
            {
                required.Add(p.Name);
            }
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }
        return schema;
    }

    private static string? Opt(JsonObject a, string key) =>
        a[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static string Req(JsonObject a, string key) => Opt(a, key) ?? throw new ToolError($"{key} 인자가 필요합니다.");

    private static int? Int(JsonObject a, string key) =>
        a[key] is JsonValue v ? v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (int)d
            : v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : null : null;

    private static bool? Bool(JsonObject a, string key) =>
        a[key] is JsonValue v ? v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p) ? p : null : null;
}
