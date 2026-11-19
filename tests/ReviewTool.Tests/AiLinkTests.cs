using System.Text.Json.Nodes;
using ReviewTool.Cli.Mcp;
using ReviewTool.Core;

namespace ReviewTool.Tests;

// 2단계: 문맥 복사(FlowContext), 조회(TraceQueries), AI CLI 출력 해석(AiStream), MCP 서버.
public class AiLinkTests
{
    // E(진입점) → A → B(등록 연결, 추정) , A → ext:std::x(범위 밖), A → A(재귀)
    private static TraceGraph Sample()
    {
        var g = new TraceGraph
        {
            TargetName = "t", Language = "cpp", RootDirectory = @"C:\secret\repo",
            AnalyzedAt = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero),
        };
        void Add(string id, string name, string file, int line, string code, NodeKind kind = NodeKind.Method) =>
            g.Nodes[id] = new TraceNode { Id = id, Name = name, File = file, StartLine = line, EndLine = line + code.Split('\n').Length - 1, Code = code, Kind = kind };
        Add("E@1", "Handler::OnLogin", "src/Handler.cpp", 10, "void Handler::OnLogin()\n{\n    world_.Enter();\n}");
        Add("A@1", "World::Enter", "src/World.cpp", 20, "void World::Enter()\n{\n    std::x();\n    Dispatch();\n    Enter();\n}");
        Add("B@1", "World::OnEnter", "src/World.cpp", 40, "void World::OnEnter()\n{\n}");
        Add("ext:std::x", "std::x", "", 0, "", NodeKind.External);
        g.Nodes["B@1"].Stop = StopReason.NoDefinition;
        g.Edges.Add(Edge("E@1", "A@1", "src/Handler.cpp", 12));
        g.Edges.Add(Edge("A@1", "ext:std::x", "src/World.cpp", 22));
        g.Edges.Add(Edge("A@1", "B@1", "src/World.cpp", 23, "Registration"));
        g.Edges.Add(Edge("A@1", "A@1", "src/World.cpp", 24));
        g.EntryPoints.Add(new EntryPoint
        {
            NodeId = "E@1", Label = "C2SLogin", Group = "Server / Handler",
            RegisteredAt = new SourceRef { File = "src/Handler.cpp", Line = 3 },
        });
        g.Diagnostics.Add("진단 하나");
        return g;
    }

    private static TraceEdge Edge(string from, string to, string file, int line, string resolution = "Static") => new()
    {
        From = from, To = to, Resolution = resolution, CallSites = [new SourceRef { File = file, Line = line }],
    };

    [Fact]
    public void 문맥은_경로_코드_근거_추정_못본것을_담고_절대경로는_뺀다()
    {
        var g = Sample();
        var md = FlowContext.ToMarkdown(g, ["E@1", "A@1", "B@1"], new FlowContext.Options { ChangedFiles = ["src/World.cpp"] });

        Assert.Contains("# 흐름: C2SLogin → World::OnEnter", md);
        Assert.Contains("- 경로: Handler::OnLogin → World::Enter → World::OnEnter", md);
        Assert.Contains("호출 근거: src/Handler.cpp:12", md);
        Assert.Contains("호출 근거: src/World.cpp:23 — **추정 연결**(Registration)", md);
        Assert.Contains(">12 | ", md);                              // 다음 함수를 부르는 줄 표시
        Assert.Contains("// → World::Enter", md);
        Assert.Contains("// → World::OnEnter(추정)", md);
        Assert.Contains("```cpp", md);
        Assert.Contains("이 파일은 분석 뒤 바뀌었다", md);
        Assert.Contains("정의(본문)를 찾지 못했습니다", md);           // B 의 멈춤 이유
        Assert.Contains("분석 진단: 진단 하나", md);
        Assert.DoesNotContain(@"C:\secret", md);
    }

    [Fact]
    public void 코드를_빼면_구조만_남는다()
    {
        var md = FlowContext.ToMarkdown(Sample(), ["E@1", "A@1"], new FlowContext.Options { IncludeCode = false });

        Assert.DoesNotContain("```", md);
        Assert.DoesNotContain("world_.Enter", md);
        Assert.Contains("호출 근거: src/Handler.cpp:12", md);
    }

    [Fact]
    public void 질문_프롬프트는_문맥과_질문을_감싼다()
    {
        var p = FlowContext.Prompt("CTX", "왜?");
        Assert.Contains("CTX", p);
        Assert.EndsWith("왜?", p.TrimEnd());
        Assert.Contains("파일:줄", p);
    }

    [Fact]
    public void 함수_찾기는_id_정확한이름_끝이름_일부_순()
    {
        var g = Sample();
        Assert.Equal(["A@1"], TraceQueries.FindFunctions(g, "A@1").Select(n => n.Id));
        Assert.Equal(["A@1"], TraceQueries.FindFunctions(g, "World::Enter").Select(n => n.Id));
        Assert.Equal(["A@1"], TraceQueries.FindFunctions(g, "Enter").Select(n => n.Id));          // ::Enter 로 끝남(OnEnter 는 아님)
        Assert.Equal(["E@1"], TraceQueries.FindFunctions(g, "onlogin").Select(n => n.Id));        // 일부, 대소문자 무시
        Assert.Empty(TraceQueries.FindFunctions(g, "없음"));
        Assert.Empty(TraceQueries.FindFunctions(g, "  "));
    }

    [Fact]
    public void 경로와_트리()
    {
        var g = Sample();
        var index = new TraceIndex(g);
        Assert.Equal(["E@1", "A@1", "B@1"], TraceQueries.PathFrom(g, index, "E@1", "B@1"));
        Assert.Null(TraceQueries.PathFrom(g, index, "B@1", "E@1"));

        var tree = TraceQueries.CallTreeText(g, index, "E@1");
        Assert.Contains("- Handler::OnLogin  (src/Handler.cpp:10)", tree);
        Assert.Contains("[추정 연결: Registration]", tree);
        Assert.Contains("[범위 밖]", tree);
        Assert.Contains("[멈춤: NoDefinition]", tree);
        Assert.Contains("(위에서 펼침)", tree);                       // 재귀는 다시 펼치지 않음

        var shallow = TraceQueries.CallTreeText(g, index, "E@1", maxDepth: 1);
        Assert.Contains("깊이 1 에서 자름", shallow);
        Assert.DoesNotContain("std::x", shallow);
    }

    [Fact]
    public void 깊이_제한으로_못_펼친_노드는_더_얕은_경로에서_펼친다()
    {
        // R → A → B → C, R → B  (maxDepth 2: 첫 경로의 B 는 깊이 2 라 C 를 못 펼침, 두 번째 R → B 는 깊이 1 이라 펼침)
        var g = new TraceGraph();
        foreach (var id in new[] { "R", "A", "B", "C" })
        {
            g.Nodes[id] = new TraceNode { Id = id, Name = id, File = "f.cpp", StartLine = 1 };
        }
        foreach (var (f, t) in new[] { ("R", "A"), ("A", "B"), ("B", "C"), ("R", "B") })
        {
            g.Edges.Add(new TraceEdge { From = f, To = t });
        }

        var tree = TraceQueries.CallTreeText(g, new TraceIndex(g), "R", maxDepth: 2);

        Assert.Contains("깊이 2 에서 자름", tree);
        Assert.Contains("    - C  (f.cpp:1)", tree);      // R → B → C (깊이 2)
        Assert.DoesNotContain("(위에서 펼침)", tree);
    }

    [Fact]
    public void Claude_스트림_해석()
    {
        var lines = new[]
        {
            """{"type":"system","subtype":"init"}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"안녕"}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{"}}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"안녕"},{"type":"tool_use","name":"Read","input":{"file_path":"src/a.cpp"}}]}}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"안녕"}""",
            "깨진 줄",
            "{깨진 json",
        };
        var events = lines.SelectMany(AiStream.ParseClaude).ToList();

        Assert.Equal(
            [new AiEvent(AiEventKind.Text, "안녕"), new AiEvent(AiEventKind.Tool, "Read: src/a.cpp"), new AiEvent(AiEventKind.Done, "")],
            events);
        Assert.Equal(AiEventKind.Error,
            AiStream.ParseClaude("""{"type":"result","is_error":true,"result":"한도 초과"}""").Single().Kind);
    }

    [Fact]
    public void Codex_스트림_해석()
    {
        var lines = new[]
        {
            """{"type":"thread.started","thread_id":"x"}""",
            """{"type":"item.started","item":{"id":"i1","type":"command_execution","command":"rg Foo"}}""",
            """{"type":"item.completed","item":{"id":"i1","type":"command_execution","command":"rg Foo"}}""",
            """{"type":"item.completed","item":{"id":"i2","type":"agent_message","text":"답"}}""",
            """{"type":"turn.completed","usage":{}}""",
        };
        var events = lines.SelectMany(AiStream.ParseCodex).ToList();

        Assert.Equal(
            [new AiEvent(AiEventKind.Tool, "명령: rg Foo"), new AiEvent(AiEventKind.Text, "답\n\n"), new AiEvent(AiEventKind.Done, "")],
            events);
        Assert.Equal("막힘", AiStream.ParseCodex("""{"type":"turn.failed","error":{"message":"막힘"}}""").Single().Text);
    }

    [Fact]
    public void 답_Markdown은_이스케이프한_뒤_일부만_태그로()
    {
        var html = MiniMarkdown.ToHtml("""
            # 요약
            **굵게** 와 `a<b>` 그리고 <script>alert(1)</script>
            - 하나 `x`
              이어지는 줄
            1. 첫째



            ```
            if (a < b) { **x** }
            ```
            짝 없는 ` 백틱
            """);

        Assert.Contains("<h3>요약</h3>", html);
        Assert.Contains("<b>굵게</b>", html);
        Assert.Contains("<code>a&lt;b&gt;</code>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("<ul><li>하나 <code>x</code></li><div class=\"cont\">이어지는 줄</div></ul><ol><li>첫째</li></ol>", html);
        Assert.Contains("<pre><code>if (a &lt; b) { **x** }\n</code></pre>", html);   // 코드 블록 안은 굵게 처리 안 함
        Assert.DoesNotContain("<br>", html);                                            // 빈 줄은 문단 구분(CSS)
        Assert.Contains("<p>짝 없는 ` 백틱</p>", html);

        Assert.Equal("<p><b>굵게 <code>A</code> 호출</b> <code>**아님**</code></p><hr>",
                     MiniMarkdown.ToHtml("**굵게 `A` 호출** `**아님**`\n---"));
        Assert.Equal("<p>&lt;b&gt;\u0001</p>".Replace("\u0001", " "), MiniMarkdown.ToHtml("<b>\u0001"));  // 자리표시 글자를 흉내 내도 무해
    }

    // ---- MCP ----

    private sealed class McpFixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("rt-mcp").FullName;
        public McpServer Server { get; }

        public McpFixture(bool withResult = true)
        {
            var targets = Directory.CreateDirectory(Path.Combine(Root, "targets")).FullName;
            var data = Path.Combine(Root, "data");
            var repo = Directory.CreateDirectory(Path.Combine(Root, "repo")).FullName;
            new TargetConfig { Name = "t", Root = repo }.Save(targets, overwrite: true);
            if (withResult)
            {
                var g = Sample();
                g.RootDirectory = repo;
                TraceStore.Save(g, new WorkPaths(data, "t").TraceFile);
            }
            Server = new McpServer(new McpTools(targets, data), TextReader.Null, TextWriter.Null, TextWriter.Null);
        }

        public JsonObject Rpc(string method, JsonObject? @params = null, int id = 1) =>
            Server.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params }.ToJsonString())!;

        public (string Text, bool IsError) CallTool(string name, JsonObject args)
        {
            var result = Rpc("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args })["result"]!;
            return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [Fact]
    public void MCP_초기화와_도구_목록()
    {
        using var f = new McpFixture();
        var init = f.Rpc("initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" })["result"]!;
        Assert.Equal("2025-03-26", init["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(init["capabilities"]!["tools"]);

        Assert.Null(f.Server.Handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));   // 알림엔 답하지 않음

        var tools = f.Rpc("tools/list")["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["list_targets", "get_entrypoints", "find_functions", "get_flow", "get_source", "get_flow_context", "get_impact", "compare",
                      "get_journey", "get_flow_summary", "get_system_map", "get_class"], tools);

        Assert.Equal(-32601, f.Rpc("nope")["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32700, f.Server.Handle("not json")!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void MCP_요청_알림_응답_구분()
    {
        using var f = new McpFixture();
        // method 가 문자열이 아닌 요청 → -32600 (응답으로 착각해 무시하지 않음)
        Assert.Equal(-32600, f.Server.Handle("""{"jsonrpc":"2.0","id":7,"method":42}""")!["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32600, f.Server.Handle("""{"jsonrpc":"2.0","id":8}""")!["error"]!["code"]!.GetValue<int>());
        // 알림은 실패해도 답하지 않는다
        Assert.Null(f.Server.Handle("""{"jsonrpc":"2.0","method":"tools/call","params":{}}"""));
        Assert.Null(f.Server.Handle("""{"jsonrpc":"2.0","method":42}"""));
        // 클라이언트가 보낸 응답은 무시
        Assert.Null(f.Server.Handle("""{"jsonrpc":"2.0","id":9,"result":{}}"""));
    }

    [Fact]
    public void MCP_는_폴더를_만들지_않고_루트가_바뀌면_거부()
    {
        using var f = new McpFixture();
        var targetData = Path.Combine(f.Root, "data", "t");
        f.CallTool("list_targets", []);
        Assert.True(f.CallTool("compare", new JsonObject { ["target"] = "t" }).IsError);
        Assert.False(Directory.Exists(Path.Combine(targetData, "snapshots")));

        // 설정의 root 를 다른 폴더로 바꿈(재분석 전)
        var other = Directory.CreateDirectory(Path.Combine(f.Root, "other")).FullName;
        new TargetConfig { Name = "t", Root = other }.Save(Path.Combine(f.Root, "targets"), overwrite: true);
        var (text, isError) = f.CallTool("get_entrypoints", new JsonObject { ["target"] = "t" });
        Assert.True(isError);
        Assert.Contains("재분석이 필요", text);
        Assert.Contains("재분석이 필요", f.CallTool("list_targets", []).Text);

        // 결과가 없는 대상: data/<대상> 폴더도 만들지 않음
        new TargetConfig { Name = "u", Root = other }.Save(Path.Combine(f.Root, "targets"), overwrite: true);
        f.CallTool("list_targets", []);
        Assert.False(Directory.Exists(Path.Combine(f.Root, "data", "u")));
    }

    [Fact]
    public void MCP_도구_호출()
    {
        using var f = new McpFixture();
        var (targets, _) = f.CallTool("list_targets", []);
        Assert.Contains("## t", targets);
        Assert.Contains("진입점 1 · 함수 4 · 호출 4", targets);

        Assert.Contains("C2SLogin  id=E@1", f.CallTool("get_entrypoints", new JsonObject { ["target"] = "t" }).Text);
        Assert.Contains("id=A@1", f.CallTool("find_functions", new JsonObject { ["target"] = "t", ["query"] = "Enter" }).Text);
        Assert.Contains("[추정 연결: Registration]", f.CallTool("get_flow", new JsonObject { ["target"] = "t", ["function"] = "E@1", ["depth"] = 3 }).Text);

        var source = f.CallTool("get_source", new JsonObject { ["target"] = "t", ["function"] = "World::Enter" }).Text;
        Assert.Contains("22 |     std::x();", source);
        Assert.Contains("부르는 곳 2개", source);                    // Handler::OnLogin, 자기 자신(재귀)

        var ctx = f.CallTool("get_flow_context", new JsonObject { ["target"] = "t", ["function"] = "World::OnEnter" }).Text;
        Assert.Contains("- 경로: Handler::OnLogin → World::Enter → World::OnEnter", ctx);

        Assert.Contains("C2SLogin", f.CallTool("get_impact", new JsonObject { ["target"] = "t", ["function"] = "B@1" }).Text);
    }

    [Fact]
    public void MCP_잘못된_인자는_도구_오류로()
    {
        using var f = new McpFixture();
        var (text, isError) = f.CallTool("get_flow", new JsonObject { ["target"] = "없는대상", ["function"] = "x" });
        Assert.True(isError);
        Assert.Contains("있는 대상: t", text);

        Assert.True(f.CallTool("get_flow", new JsonObject { ["target"] = "t" }).IsError);                       // function 없음
        Assert.True(f.CallTool("get_source", new JsonObject { ["target"] = "t", ["function"] = "없음" }).IsError);
        Assert.True(f.CallTool("compare", new JsonObject { ["target"] = "t" }).IsError);                          // 기준 없음
        Assert.True(f.CallTool("get_flow_context", new JsonObject { ["target"] = "t", ["function"] = "E@1", ["entry"] = "B@1" }).IsError);
        Assert.True(f.CallTool("없는도구", []).IsError);
    }

    [Fact]
    public void MCP_결과가_없으면_안내()
    {
        using var f = new McpFixture(withResult: false);
        Assert.Contains("분석 결과 없음", f.CallTool("list_targets", []).Text);
        Assert.True(f.CallTool("get_entrypoints", new JsonObject { ["target"] = "t" }).IsError);
    }

    [Fact]
    public async Task MCP_stdio_한줄에_한메시지()
    {
        using var f = new McpFixture();
        var input = new StringReader(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""" + "\n\n" +
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""" + "\n" +
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_source","arguments":{"target":"t","function":"A@1"}}}""" + "\n");
        var output = new StringWriter();
        var targets = Path.Combine(f.Root, "targets");
        await new McpServer(new McpTools(targets, Path.Combine(f.Root, "data")), input, output, TextWriter.Null).RunAsync(CancellationToken.None);

        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, replies.Length);
        Assert.Contains("World::Enter", replies[1]);                 // 한글·이름이 이스케이프 없이
        Assert.Equal(2, JsonNode.Parse(replies[1])!["id"]!.GetValue<int>());
    }

    [Fact]
    public void MCP_3단계_도구는_진입점_이름으로_찾고_옛_결과엔_재분석을_안내()
    {
        using var f = new McpFixture();
        // Sample 은 메시지·클래스 정보가 없는 결과(3단계 이전과 같음)
        Assert.Contains("재분석하면 여정이 보입니다", f.CallTool("get_journey", new JsonObject { ["target"] = "t", ["entry"] = "C2SLogin" }).Text);
        Assert.Contains("# 흐름 요약: C2SLogin (Server / Handler)", f.CallTool("get_flow_summary", new JsonObject { ["target"] = "t", ["entry"] = "C2SLogin" }).Text);
        Assert.Contains("## 공용", f.CallTool("get_system_map", new JsonObject { ["target"] = "t" }).Text);
        var (text, isError) = f.CallTool("get_class", new JsonObject { ["target"] = "t", ["name"] = "World" });
        Assert.True(isError);
        Assert.Contains("재분석", text);
    }
}
