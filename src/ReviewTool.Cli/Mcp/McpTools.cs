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
        new("get_journey", "패킷 여정: 이 진입점에 패킷이 오기까지(보낸 프로세스·함수·줄)와 여기서 보내는 패킷이 어느 프로세스의 어느 핸들러로 가는지. " +
                           "보낸 곳(SendPacket(X))과 X 를 받는 곳을 이름으로 잇는다(추정).",
            Schema(("target", "string", "대상 이름", true), ("entry", "string", "진입점 표시 이름(C2ZMove 등)·함수 id·함수 이름", true)),
            a => JourneyText(Target(a), Req(a, "entry"))),
        new("get_flow_summary", "흐름 요약: 진입점에서 소스 순서대로 내려가며 표시(설정·스레드·등록·네트워크·DB·캐시·작업 단위)가 붙은 호출만. " +
                                "main 이면 시작 순서, 패킷 핸들러면 DB·캐시 접근 순서.",
            Schema(("target", "string", "대상 이름", true), ("entry", "string", "진입점 표시 이름·함수 id·함수 이름", true),
                   ("tag", "string", "이 표시만(선택)", false)),
            a => SummaryText(Target(a), Req(a, "entry"), Opt(a, "tag"))),
        new("get_system_map", "전체 흐름도: 프로세스별 클래스와 클래스 사이 호출 수, 프로세스를 넘는 패킷.",
            Schema(("target", "string", "대상 이름", true)),
            a => MapText(Target(a))),
        new("get_class", "클래스 선언: 파일:줄, 부모, 멤버(필드·메서드), 이 클래스의 분석된 함수.",
            Schema(("target", "string", "대상 이름", true), ("name", "string", "클래스 이름(일부 가능)", true)),
            a => ClassText(Target(a), Req(a, "name"))),
        new("get_checks", "점검 규칙(대상 설정 checks): 규칙마다 상태(돌았음·꺼짐·오류)와 지적. 지적마다 근거 파일:줄, 확실도(높음·보통·낮음)와 그 이유, " +
                          "진입점에서의 경로. 락 안 I/O, 작업 단위·레인 밖 접근, 차감 뒤 지급, 금지된 의존 같은 규칙이다(이름·소스 텍스트 추정).",
            Schema(("target", "string", "대상 이름", true), ("rule", "string", "이 규칙 이름만(선택)", false),
                   ("entry", "string", "이 진입점 경로에 걸친 지적만(선택). 표시 이름·함수 id·함수 이름", false)),
            a => ChecksText(Req(a, "target"), Target(a), Opt(a, "rule"), Opt(a, "entry"))),
        new("get_review", "사람의 검토 기록: 함수마다 확인·보류·메모와, 표시 뒤 본문이 바뀌어 다시 봐야 하는 함수. " +
                          "entry 를 주면 그 진입점에서 닿는 함수 중 아직 확인하지 않은 것(검토 남은 곳)을 보여 준다.",
            Schema(("target", "string", "대상 이름", true), ("entry", "string", "진입점 표시 이름·함수 id·함수 이름(선택)", false)),
            a => ReviewText(Req(a, "target"), Target(a), Opt(a, "entry"))),
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
            (TraceGraph Graph, TraceIndex Index)? loaded;
            try
            {
                loaded = TryGraph(name);
            }
            catch (ToolError ex)
            {
                sb.Append("- ").Append(ex.Message).Append("\n\n");
                continue;
            }
            if (loaded is null)
            {
                sb.Append("- 분석 결과 없음(review-tool analyze 또는 화면의 다시 분석)\n\n");
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
            ITraceAnalyzer analyzer = config.Language switch
            {
                "cpp" => new CppTraceAnalyzer(),
                "csharp" => new ReviewTool.CSharp.CSharpTraceAnalyzer(),
                _ => throw new NotSupportedException(config.Language),
            };
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

    // 진입점 표시 이름(패킷 이름)이 정확히 맞으면 그 진입점, 아니면 함수로 찾는다
    private static TraceNode ResolveEntry(TraceGraph g, string query)
    {
        var byLabel = g.EntryPoints.Where(e => e.Label == query.Trim()).ToList();
        if (byLabel.Count == 1)
        {
            return g.Nodes[byLabel[0].NodeId];
        }
        if (byLabel.Count > 1)
        {
            throw new ToolError($"이 이름의 진입점이 {byLabel.Count}개입니다. id 로 고르세요:\n" +
                                string.Join('\n', byLabel.Select(e => $"- {e.Label} · {e.Group}  id={e.NodeId}")));
        }
        return Resolve(g, query);
    }

    private static string EntryTitle(TraceGraph g, TraceNode n) =>
        g.EntryPoints.FirstOrDefault(e => e.NodeId == n.Id) is { } e ? $"{e.Label} ({e.Group})" : n.Name;

    private static string JourneyText((TraceGraph G, TraceIndex I) t, string entry)
    {
        var n = ResolveEntry(t.G, entry);
        var journey = new Journey(t.G, t.I);
        return journey.ToText(journey.Build(n.Id), EntryTitle(t.G, n));
    }

    private static string SummaryText((TraceGraph G, TraceIndex I) t, string entry, string? tag)
    {
        var n = ResolveEntry(t.G, entry);
        var result = FlowSummary.Build(t.G, t.I, n.Id);
        if (tag is not null)
        {
            result = result with { Items = result.Items.Where(i => i.Tags.Contains(tag)).ToList() };
        }
        return FlowSummary.ToText(t.G, result, EntryTitle(t.G, n) + (tag is null ? "" : $" [{tag}]"));
    }

    private static string MapText((TraceGraph G, TraceIndex I) t)
    {
        var map = SystemMap.Build(t.G, t.I);
        var sb = new StringBuilder("# 전체 흐름도\n\n");
        foreach (var p in map.Processes)
        {
            sb.Append("## ").Append(ProcessMap.Display(p)).Append('\n');
            foreach (var c in map.Classes.Where(c => c.Process == p))
            {
                sb.Append("- ").Append(c.Id).Append("  함수 ").Append(c.Functions).Append(c.Entries > 0 ? $", 진입점 {c.Entries}" : "").Append('\n');
            }
            sb.Append('\n');
        }
        var packets = map.Links.Where(l => l.Kind == "packet").ToList();
        sb.Append("## 프로세스를 넘는 패킷 ").Append(packets.Count).Append("개\n");
        foreach (var l in packets)
        {
            sb.Append("- ").Append(l.From).Append(" → ").Append(l.To).Append(": ").Append(string.Join(", ", l.Keys)).Append('\n');
        }
        var calls = map.Links.Where(l => l.Kind != "packet").OrderByDescending(l => l.Count).Take(100).ToList();
        sb.Append("\n## 클래스 사이 호출 (많은 순, 최대 100)\n");
        foreach (var l in calls)
        {
            sb.Append("- ").Append(l.From).Append(" → ").Append(l.To).Append("  ").Append(l.Count).Append("곳")
              .Append(l.Kind == "guess" ? " (추정 연결)" : "").Append('\n');
        }
        return sb.ToString();
    }

    private static string ClassText((TraceGraph G, TraceIndex I) t, string name)
    {
        if (t.G.Types.Count == 0)
        {
            throw new ToolError("이 분석 결과에는 클래스 정보가 없습니다. 재분석하면 생깁니다.");
        }
        var q = name.Trim();
        var found = t.G.Types.Where(x => x.Name == q).ToList();
        if (found.Count == 0)
        {
            found = t.G.Types.Where(x => x.Name.EndsWith("::" + q, StringComparison.Ordinal) ||
                                         x.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(20).ToList();
        }
        if (found.Count != 1)
        {
            return found.Count == 0 ? "찾지 못했습니다. (흐름에 나온 클래스만 있습니다)"
                : "여러 개입니다:\n" + string.Join('\n', found.Select(x => $"- {x.Name}  {x.File}:{x.Line}"));
        }
        var type = found[0];
        var sb = new StringBuilder();
        sb.Append("# ").Append(type.Kind).Append(' ').Append(type.Name).Append("  (").Append(type.File).Append(':').Append(type.Line).Append(")\n");
        if (type.Bases.Count > 0)
        {
            sb.Append("부모: ").Append(string.Join(", ", type.Bases)).Append('\n');
        }
        var children = t.G.Types.Where(x => x.Bases.Contains(type.Name.Split("::")[^1])).Select(x => x.Name).ToList();
        if (children.Count > 0)
        {
            sb.Append("자식: ").Append(string.Join(", ", children)).Append('\n');
        }
        sb.Append("\n## 멤버\n");
        foreach (var m in type.Members)
        {
            sb.Append("- ").Append(m.Kind == "field" ? "필드 " : m.Kind == "ctor" ? "생성자 " : "메서드 ")
              .Append(m.Name).Append(m.Detail.Length > 0 ? "  : " + m.Detail : "").Append('\n');
        }
        var functions = t.G.Nodes.Values.Where(n => SystemMap.ClassOf(n) == type.Name).OrderBy(n => n.StartLine).ToList();
        sb.Append("\n## 분석된 함수 ").Append(functions.Count).Append("개\n");
        foreach (var f in functions)
        {
            sb.Append("- ").Append(f.Name).Append("  ").Append(f.File).Append(':').Append(f.StartLine).Append("  id=").Append(f.Id).Append('\n');
        }
        return sb.ToString();
    }

    private string ChecksText(string name, (TraceGraph G, TraceIndex I) t, string? rule, string? entry)
    {
        var config = LoadConfig(name);
        var rules = config.Checks.Where(r => rule is null || r.Name == rule).ToList();
        if (rules.Count == 0)
        {
            throw new ToolError(rule is null ? "대상 설정에 점검 규칙(checks)이 없습니다."
                : $"규칙 {rule} 이 없습니다. 있는 규칙: {string.Join(", ", config.Checks.Select(r => r.Name))}");
        }
        var report = Checks.Run(rules, t.G, t.I, Checks.ReadFrom(config.Root));
        var title = config.Name;
        if (entry is not null)
        {
            var n = ResolveEntry(t.G, entry);
            report = report with { Findings = report.Findings.Where(f => f.Entries.Contains(n.Id)).ToList() };
            title += " · " + EntryTitle(t.G, n);
        }
        return Checks.ToText(t.G, report, title);
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

    // 읽기만 하므로 폴더를 만들지 않는다
    private string ReviewText(string name, (TraceGraph G, TraceIndex I) t, string? entry)
    {
        var log = ReviewLog.Load(ReviewLog.PathOf(Paths(LoadConfig(name))));
        var sb = new StringBuilder("# 검토 기록: ").Append(name).Append("\n\n");
        var marked = t.G.Nodes.Values.Where(n => log.StateOf(n) != ReviewLog.State.None).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        if (marked.Count == 0)
        {
            sb.Append("- 표시한 함수가 없습니다(화면의 코드 칸에서 \"확인\"·\"보류\").\n");
        }
        foreach (var n in marked)
        {
            var state = log.StateOf(n) switch
            {
                ReviewLog.State.Checked => "확인",
                ReviewLog.State.Hold => "보류",
                _ => "다시 봐야 함(표시 뒤 본문이 바뀜)",
            };
            var note = log.Find(n)?.Note ?? "";
            sb.Append("- [").Append(state).Append("] `").Append(n.Name).Append("` ").Append(n.File).Append(':').Append(n.StartLine)
              .Append(note.Length > 0 ? " — " + note : "").Append('\n');
        }
        if (entry is not null)
        {
            var e = ResolveEntry(t.G, entry);
            var (done, total) = log.Progress(t.G, t.I, e.Id);
            sb.Append("\n## ").Append(EntryTitle(t.G, e)).Append(": 확인 ").Append(done).Append('/').Append(total).Append("\n\n");
            var seen = new HashSet<string> { e.Id };
            var queue = new Queue<string>(seen);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (t.G.Nodes.TryGetValue(id, out var n) && n.Kind != NodeKind.External && log.StateOf(n) != ReviewLog.State.Checked)
                {
                    sb.Append("- 남음: `").Append(n.Name).Append("` ").Append(n.File).Append(':').Append(n.StartLine).Append('\n');
                }
                foreach (var c in t.I.ChildrenOf(id).Where(c => seen.Add(c.To)))
                {
                    queue.Enqueue(c.To);
                }
            }
        }
        sb.Append("\n사람이 남긴 표시다. \"확인\"은 그 사람이 읽었다는 뜻이지 문제없다는 증명이 아니다.\n");
        return sb.ToString();
    }

    private WorkPaths Paths(TargetConfig config) => new(dataRoot, config.Name, create: false);

    private (TraceGraph Graph, TraceIndex Index)? TryGraph(string name)
    {
        var config = LoadConfig(name);
        var file = Paths(config).TraceFile;
        if (!File.Exists(file))
        {
            return null;
        }
        var stamp = File.GetLastWriteTimeUtc(file);
        TraceGraph? graph = null;
        TraceIndex? index = null;
        lock (cache_)
        {
            if (cache_.TryGetValue(name, out var c) && c.Stamp == stamp)
            {
                (graph, index) = (c.Graph, c.Index);
            }
        }
        if (graph is null)
        {
            graph = TraceStore.TryLoad(file);
            if (graph is null)
            {
                return null;
            }
            index = new TraceIndex(graph);
            lock (cache_)
            {
                cache_[name] = (stamp, graph, index);
            }
        }
        // 설정의 root 를 다른 저장소로 바꾼 뒤 재분석 전이면, 옛 저장소의 결과를 이 대상의 것으로 내주지 않는다
        if (!SameRoot(graph.RootDirectory, config.Root))
        {
            throw new ToolError($"대상 {name} 의 설정 폴더({config.Root})와 분석 결과의 폴더({graph.RootDirectory})가 다릅니다. 재분석이 필요합니다.");
        }
        return (graph, index!);
    }

    private static bool SameRoot(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                                 OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
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
