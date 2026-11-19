using ReviewTool.Core;
using ReviewTool.Cpp;

namespace ReviewTool.Tests;

// 3단계: 메시지 찾기(MessagePass), 패킷 여정(Journey), 흐름 요약, 전체 흐름도, 묶음(프로세스) 이름.
public class GameServerViewTests
{
    private static List<(int, string)> Body(int start, string code) =>
        code.Split('\n').Select((t, i) => (start + i, t)).ToList();

    [Fact]
    public void 보내는_곳은_직접_이름과_변수_타입으로_찾고_받은_인자와_주석은_뺀다()
    {
        var node = new TraceNode { Id = "H@1", Name = "H::Handle", File = "a.cpp", StartLine = 10 };
        var body = Body(10, """
            void H::Handle(const Session& s, const C2SLogin& packet)
            {
                // SendPacket(PacketId::X2YFake)
                Common::W2ZLeave leave{};
                SendPacket(s, leave);
                session->SendPacket(
                    PacketId::W2GRelay, Wrap(packet));
                SendPacket(s, packet);
                switch (id) { case PacketId::C2ZMove: break; }
            }
            """);
        var sites = MessagePass.Scan(MessageChannel.DefaultPacket(), node, body);

        var sends = sites.Where(s => s.Role == MessageRole.Send).Select(s => (s.Key, s.Site.Line, s.How)).ToList();
        Assert.Equal([("W2ZLeave", 14, "변수 타입"), ("W2GRelay", 15, "직접")], sends);   // 받은 인자(C2SLogin)·주석은 아님
        var receive = Assert.Single(sites, s => s.Role == MessageRole.Receive);
        Assert.Equal(("C2ZMove", 18, "case"), (receive.Key, receive.Site.Line, receive.How));
    }

    [Fact]
    public void 변수_타입은_return_같은_앞말이_아니라_이름_모양인_것()
    {
        Assert.Equal(["W2ZLeave", "return"], MessagePass.DeclaredTypes("W2ZLeave leave{};\nreturn leave;", "leave"));
    }

    // Gate(네트워크 수신) ─G2WRelay→ World.HandleRelay ─W2ZA→ Zone.Stream(case W2ZA / case W2ZB) → Player.HandleMove(C2ZMove)
    //                                               ─W2CResult→ (받는 곳 없음)
    private static TraceGraph TwoHops()
    {
        var g = new TraceGraph { Channels = [new ChannelInfo { Name = "패킷", CrossProcess = true }] };
        void Node(string id, string name, string file, int start, int lines) =>
            g.Nodes[id] = new TraceNode
            {
                Id = id, Name = name, File = file, StartLine = start, EndLine = start + lines - 1, Kind = NodeKind.Method,
                Code = string.Join('\n', Enumerable.Range(0, lines).Select(i => i == 0 ? $"void {name}()" : "    x();")),
            };
        Node("G", "Gate::OnPacket", "Gate/Src/Gate.cpp", 10, 5);
        Node("W", "World::HandleRelay", "World/Src/World.cpp", 20, 15);
        Node("S", "Zone::Stream", "Zone/Src/Zone.cpp", 40, 10);
        Node("A", "Player::HandleMove", "Zone/Src/Player.cpp", 60, 5);
        Node("B", "Player::HandleChat", "Zone/Src/Player.cpp", 70, 5);
        void Entry(string id, string label, string group, int line) =>
            g.EntryPoints.Add(new EntryPoint { NodeId = id, Label = label, Group = group, RegisteredAt = new SourceRef { File = g.Nodes[id].File, Line = line } });
        Entry("G", "OnPacket", "Gate / Gate", 10);
        Entry("W", "G2WRelay", "World / World", 5);
        Entry("A", "C2ZMove", "Zone / Player", 3);
        Entry("B", "C2ZChat", "Zone / Player", 4);
        void Msg(string id, string key, MessageRole role, int line, string how) =>
            g.Messages.Add(new MessageSite { Channel = "패킷", Key = key, Role = role, NodeId = id, How = how,
                                             Site = new SourceRef { File = g.Nodes[id].File, Line = line } });
        Msg("G", "G2WRelay", MessageRole.Send, 12, "직접");
        Msg("W", "G2WRelay", MessageRole.Receive, 5, "등록");
        Msg("W", "W2ZA", MessageRole.Send, 25, "직접");
        Msg("W", "W2CResult", MessageRole.Send, 30, "변수 타입");
        Msg("S", "W2ZA", MessageRole.Receive, 41, "case");
        Msg("S", "W2ZB", MessageRole.Receive, 45, "case");
        Msg("A", "C2ZMove", MessageRole.Receive, 3, "등록");
        Msg("B", "C2ZChat", MessageRole.Receive, 4, "등록");
        g.Edges.Add(new TraceEdge { From = "S", To = "A", CallSites = [new SourceRef { File = "Zone/Src/Zone.cpp", Line = 43 }] });
        g.Edges.Add(new TraceEdge { From = "S", To = "B", CallSites = [new SourceRef { File = "Zone/Src/Zone.cpp", Line = 47 }] });
        return g;
    }

    [Fact]
    public void 여정은_봉투를_연_곳과_보낸_곳을_거슬러_시작까지_간다()
    {
        var g = TwoHops();
        var journey = new Journey(g, new TraceIndex(g));
        var up = journey.Build("A").Upstream;

        Assert.Equal(("A", "C2ZMove"), (up.NodeId, up.Key));
        var stream = Assert.Single(up.Children);              // C2ZMove 는 보낸 곳이 없다 → 부르는 쪽의 받는 곳
        Assert.Equal(("S", "W2ZA", "Zone"), (stream.NodeId, stream.Key, stream.Process));   // case W2ZA 다음 줄(43)에서 부름
        Assert.Null(stream.SenderId);
        Assert.Equal(["S", "A"], stream.Path);

        var world = Assert.Single(stream.Children);
        Assert.Equal(("W", "G2WRelay", "W2ZA", "W"), (world.NodeId, world.Key, world.LinkKey, world.SenderId));
        Assert.Equal(25, world.SentAt!.Line);

        var gate = Assert.Single(world.Children);
        Assert.Equal(("G", "", "G2WRelay"), (gate.NodeId, gate.Key, gate.LinkKey));
        Assert.Equal(JourneyKind.Origin, Assert.Single(gate.Children).Kind);   // 네트워크 수신 함수: 더 위는 범위 밖
    }

    [Fact]
    public void 여정은_보낸_패킷의_받는_곳으로_내려가고_case_범위만_따른다()
    {
        var g = TwoHops();
        var journey = new Journey(g, new TraceIndex(g));
        var down = journey.Build("G").Downstream;

        var world = Assert.Single(down.Children);
        Assert.Equal(("W", "G2WRelay"), (world.NodeId, world.Key));
        Assert.Equal(["G"], world.Path);                       // 보낸 함수까지의 경로

        var exit = Assert.Single(world.Children, c => c.Kind == JourneyKind.Exit);
        Assert.Equal("W2CResult", exit.LinkKey);
        var stream = Assert.Single(world.Children, c => c.Kind == JourneyKind.Station);
        Assert.Equal(("S", "W2ZA"), (stream.NodeId, stream.Key));

        var text = journey.ToText(journey.Build("G"), "OnPacket");
        Assert.Contains("[Gate] Gate::OnPacket", text);
        Assert.Contains("(끝) [World] World::HandleRelay 가 W2CResult 를 보냄 (World/Src/World.cpp:30) → 받는 곳을 찾지 못함", text);
        Assert.Contains("[Zone] Zone::Stream — W2ZA 받음 (Zone/Src/Zone.cpp:41, case)", text);
    }

    [Fact]
    public void 여정은_case_범위_밖_핸들러로_새지_않는다()
    {
        var g = TwoHops();
        // Zone.Stream 이 B(C2ZChat) 쪽에서 무언가 보낸다면, W2ZA 로 들어온 여정에는 나오면 안 된다
        g.Messages.Add(new MessageSite { Channel = "패킷", Key = "Z2WChatOut", Role = MessageRole.Send, NodeId = "B",
                                         Site = new SourceRef { File = "Zone/Src/Player.cpp", Line = 72 }, How = "직접" });
        var journey = new Journey(g, new TraceIndex(g));
        var text = journey.ToText(journey.Build("G"), "OnPacket");
        Assert.DoesNotContain("Z2WChatOut", text);
        var up = journey.Build("B").Upstream;
        Assert.Equal("W2ZB", Assert.Single(up.Children).Key);  // B 는 case W2ZB 다음에서 불렸다
    }

    [Fact]
    public void 메시지_정보가_없는_옛_결과는_재분석을_안내한다()
    {
        var g = TwoHops();
        g.Channels.Clear();
        var journey = new Journey(g, new TraceIndex(g));
        Assert.Contains("재분석하면", journey.ToText(journey.Build("A"), "x"));
    }

    [Fact]
    public void 함수_주소만_넘긴_줄은_호출이_아니다()
    {
        var g = new TraceGraph();
        g.Nodes["I"] = new TraceNode { Id = "I", Name = "C::Init", StartLine = 1, Code = "void C::Init()\n{\n    Register(this, &C::Handle);\n    Handle(1);\n}" };
        g.Nodes["H"] = new TraceNode { Id = "H", Name = "C::Handle" };
        var reg = new TraceEdge { From = "I", To = "H", CallSites = [new SourceRef { Line = 3 }] };
        Assert.True(TraceQueries.IsAddressOnly(g, reg));
        reg.CallSites.Add(new SourceRef { Line = 4 });         // 실제 호출도 있으면 호출
        Assert.False(TraceQueries.IsAddressOnly(g, reg));
    }

    [Fact]
    public void 흐름_요약은_표시가_붙은_호출만_소스_순서와_깊이로()
    {
        var g = new TraceGraph();
        void Node(string id, string name, params string[] tags) =>
            g.Nodes[id] = new TraceNode { Id = id, Name = name, File = "a.cpp", StartLine = 1, EndLine = 5, Code = "void f()\n{\n    Db::Query(\"usp_x\");\n    Helper();\n}", Tags = [.. tags] };
        Node("E", "H::Handle");
        Node("D", "Db::Query", "DB");
        Node("Y", "H::Helper");
        Node("C", "Cache::Set", "캐시");
        g.Edges.Add(new TraceEdge { From = "E", To = "D", CallSites = [new SourceRef { Line = 3 }] });
        g.Edges.Add(new TraceEdge { From = "E", To = "Y", CallSites = [new SourceRef { Line = 4 }] });
        g.Edges.Add(new TraceEdge { From = "Y", To = "C", CallSites = [new SourceRef { Line = 3 }] });

        var r = FlowSummary.Build(g, new TraceIndex(g), "E");
        Assert.Equal([("D", 0), ("C", 1)], r.Items.Select(i => (i.CalleeId, i.Depth)).ToList());
        Assert.Equal("Db::Query(\"usp_x\");", r.Items[0].LineText);
        Assert.Equal(["E", "Y", "C"], r.Items[1].Path);
        Assert.Equal(["DB", "캐시"], r.AllTags);
        Assert.Contains("- [DB] Db::Query  (a.cpp:3)", FlowSummary.ToText(g, r, "H"));
    }

    [Fact]
    public void 흐름_요약은_본문_규칙에_걸린_줄을_호출_사이_제자리에_끼운다()
    {
        var g = new TraceGraph();
        g.Nodes["E"] = new TraceNode
        {
            Id = "E", Name = "H::Handle", File = "a.cpp", StartLine = 1, EndLine = 6,
            Code = "void f()\n{\n    A();\n    Add(DbCommand{\"dbo.usp_x\"});\n    B();\n}",
            TagLines = [new TaggedLine { Line = 4, Tags = ["DB"] }],
        };
        g.Nodes["A"] = new TraceNode { Id = "A", Name = "Cache::Get", Tags = ["캐시"] };
        g.Nodes["B"] = new TraceNode { Id = "B", Name = "Cache::Set", Tags = ["캐시"] };
        g.Edges.Add(new TraceEdge { From = "E", To = "A", CallSites = [new SourceRef { Line = 3 }] });
        g.Edges.Add(new TraceEdge { From = "E", To = "B", CallSites = [new SourceRef { Line = 5 }] });

        var r = FlowSummary.Build(g, new TraceIndex(g), "E");
        Assert.Equal([("A", 3), ("", 4), ("B", 5)], r.Items.Select(i => (i.CalleeId, i.Line)).ToList());
        Assert.Contains("- [DB] H::Handle 본문  (a.cpp:4)", FlowSummary.ToText(g, r, "H"));

        var rules = new List<(string, System.Text.RegularExpressions.Regex)> { ("DB", new(TagRule.Defaults().Single(t => t.Name == "DB").Code)) };
        var lines = MessagePass.TagLines(rules, Body(1, "void f()\n{\n    // \"usp_comment\"\n    Add(DbCommand{\"dbo.usp_x\"});\n}"));
        Assert.Equal([4], lines.Select(l => l.Line));          // 주석은 빼고
    }

    [Fact]
    public void 전체_흐름도는_프로세스별_클래스와_패킷_선()
    {
        var g = TwoHops();
        var map = SystemMap.Build(g, new TraceIndex(g));
        Assert.Equal(["Gate", "World", "Zone"], map.Processes);
        Assert.Contains(map.Classes, c => c.Id == "Player" && c.Process == "Zone" && c.Functions == 2 && c.Entries == 2);
        var packet = Assert.Single(map.Links, l => l.Kind == "packet" && l.From == "Gate" && l.To == "World");
        Assert.Equal(["G2WRelay"], packet.Keys);
        Assert.Contains(map.Links, l => l.Kind == "call" && l.From == "Zone" && l.To == "Player" && l.Count == 2);

        // Gate 에서 시작한 흐름이 패킷을 타고 World·Zone 까지 간다
        Assert.Equal(["Gate", "Player", "World", "Zone"], SystemMap.ClassesOnFlow(g, new TraceIndex(g), "G").Order(StringComparer.Ordinal).ToList());
        Assert.Equal("[main.cpp]", SystemMap.ClassOf(new TraceNode { Name = "main", File = "x/main.cpp", Kind = NodeKind.Function }));
        Assert.Equal("Ns::Box", SystemMap.ClassOf(new TraceNode { Name = "Ns::Box<T>::Get", Kind = NodeKind.Method }));
    }

    [Fact]
    public void 프로세스는_진입점_묶음_이름이고_공용_코드는_빈_이름()
    {
        var g = TwoHops();
        var p = new ProcessMap(g);
        Assert.Equal("Zone", p.OfFile("Server/Zone/Src/x.h"));
        Assert.Equal("", p.OfFile("Server/Common/Src/x.h"));
        Assert.Equal("공용", ProcessMap.Display(""));
    }

    [Fact]
    public void 묶음은_가장_가까운_빌드_프로젝트_폴더_이름()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Server/ZoneServer", "Tool/StressClient" };
        Assert.Equal("ZoneServer / Player", CppTraceAnalyzer.MakeGroup("Server/ZoneServer/Src/Player/Player.cpp", "Player", "", dirs));
        Assert.Equal("StressClient / main", CppTraceAnalyzer.MakeGroup("Tool/StressClient/Src/main.cpp", null, "", dirs));
        // 프로젝트 폴더가 없으면 진입점 파일들의 공통 폴더 아래 첫 폴더
        Assert.Equal("Zone / Player", CppTraceAnalyzer.MakeGroup("Server/Zone/Player.cpp", "Player", "Server/", new HashSet<string>()));
        Assert.Equal("Player", CppTraceAnalyzer.MakeGroup("Src/Player.cpp", "Player", "Src/", new HashSet<string>()));
    }
}
