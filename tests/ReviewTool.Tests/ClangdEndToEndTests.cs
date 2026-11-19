using ReviewTool.Core;
using ReviewTool.Cpp;

namespace ReviewTool.Tests;

// 실제 clangd 로 작은 C++ 예제를 분석한다. clangd 가 없는 PC 에서는 아무것도 하지 않고 통과한다.
public class ClangdEndToEndTests
{
    [Fact]
    public async Task 진입점부터_정의를_따라_호출을_잇는다()
    {
        string clangd;
        try
        {
            clangd = ClangdSession.FindClangd("");
        }
        catch (FileNotFoundException)
        {
            return;
        }

        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mini");
        var data = Directory.CreateTempSubdirectory("rt-e2e").FullName;
        try
        {
            var config = new TargetConfig
            {
                Name = "mini",
                Root = fixture,
                EntryPoints =
                [
                    new EntryPointRule { Name = "Register", Pattern = @"Register\(\s*&(?<class>\w+)::(?<method>\w+)\s*\)" },
                ],
                HideCallees = [@"::Log$"],
                Cpp = new CppOptions
                {
                    Clangd = clangd,
                    CompileFrom = "flags",
                    Standard = "c++20",
                    ExtraIncludeDirs = ["Src"],
                    DetectMsvc = false,
                    IndexTimeoutSeconds = 120,
                },
            };

            var graph = await new CppTraceAnalyzer().AnalyzeAsync(config, new WorkPaths(data, "mini"), null, CancellationToken.None);

            // 주석 속 Register(&Handler::Init) 은 잡히지 않고, 진짜 등록 하나만
            var entry = Assert.Single(graph.EntryPoints);
            Assert.Equal("C2SMove", entry.Label);

            // prepare 결과는 헤더 선언이지만 노드는 .cpp 정의를 가리켜야 한다
            var root = graph.Nodes[entry.NodeId];
            Assert.Equal("Src/Handler.cpp", root.File);
            Assert.Contains("world_.Move", root.Code);

            var move = graph.Nodes.Values.Single(n => n.Name == "World::Move");
            Assert.Equal("Src/World.cpp", move.File);
            var broadcast = graph.Nodes.Values.Single(n => n.Name == "World::Broadcast");
            var log = graph.Nodes.Values.Single(n => n.Name == "World::Log");
            Assert.Equal(StopReason.Hidden, log.Stop);

            // 호출 위치는 호출하는 쪽 본문 안의 줄이어야 한다
            var edge = graph.Edges.Single(e => e.From == root.Id && e.To == move.Id);
            var site = Assert.Single(edge.CallSites);
            Assert.Equal("Src/Handler.cpp", site.File);
            Assert.Equal(18, site.Line);
            Assert.Contains(graph.Edges, e => e.From == move.Id && e.To == broadcast.Id);

            // 소스 순서: Log 가 Move 보다 먼저
            var order = graph.Edges.Where(e => e.From == root.Id).Select(e => graph.Nodes[e.To].Name).ToList();
            Assert.True(order.IndexOf("World::Log") < order.IndexOf("World::Move"));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    // 깊이 제한에 걸린 함수도, 나중 진입점에서 더 얕게 닿으면 아래를 마저 따라가야 한다(처리 순서와 무관한 결과).
    [Fact]
    public async Task 공유_함수는_더_얕게_닿으면_다시_펼친다()
    {
        string clangd;
        try
        {
            clangd = ClangdSession.FindClangd("");
        }
        catch (FileNotFoundException)
        {
            return;
        }

        var data = Directory.CreateTempSubdirectory("rt-e2e").FullName;
        try
        {
            var config = new TargetConfig
            {
                Name = "mini",
                Root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mini"),
                // HandleMove(0) → World::Move(1) → Broadcast(2, 제한). 그 다음 World::Move 자체가 진입점(0).
                EntryPoints =
                [
                    new EntryPointRule { Name = "Register", Pattern = @"Register\(\s*&(?<class>\w+)::(?<method>\w+)\s*\)" },
                    new EntryPointRule { Name = "World", Pattern = @"^void (?<class>World)::(?<method>Move)\(", Files = ["**/*.cpp"] },
                ],
                MaxDepth = 2,
                Cpp = new CppOptions
                {
                    Clangd = clangd, CompileFrom = "flags", Standard = "c++20", ExtraIncludeDirs = ["Src"],
                    DetectMsvc = false, IndexTimeoutSeconds = 120,
                },
            };

            var graph = await new CppTraceAnalyzer().AnalyzeAsync(config, new WorkPaths(data, "mini"), null, CancellationToken.None);

            Assert.Equal(2, graph.EntryPoints.Count);
            var broadcast = graph.Nodes.Values.Single(n => n.Name == "World::Broadcast");
            Assert.NotEqual(StopReason.DepthLimit, broadcast.Stop);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    // 3단계: 두 프로세스(LoginServer, GameServer) 예제. 패킷 여정·레인 메시지 연결·이름 표시·클래스·main 둘.
    [Fact]
    public async Task 두_프로세스_예제에서_여정_메시지_표시_클래스를_만든다()
    {
        string clangd;
        try
        {
            clangd = ClangdSession.FindClangd("");
        }
        catch (FileNotFoundException)
        {
            return;
        }

        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "multi");
        var data = Directory.CreateTempSubdirectory("rt-e2e-multi").FullName;
        try
        {
            var lane = new MessageChannel
            {
                Name = "레인 메시지", CrossProcess = false, Send = @"\bPushMsg\b", Key = @"\b(?<key>E\w+::\w+)",
                Register = @"\bRegist\(\s*(?<key>E\w+::\w+)\s*,\s*&(?<class>\w+)::(?<method>\w+)",
            };
            var config = new TargetConfig
            {
                Name = "multi",
                Root = fixture,
                EntryPoints =
                [
                    new EntryPointRule { Name = "등록", Pattern = @"Register\(\s*&(?<class>\w+)::(?<method>\w+)\s*\)" },
                    new EntryPointRule { Name = "시작 함수", Pattern = @"^int[ \t]+(?<method>main)[ \t]*\(", Files = ["**/*.cpp"] },
                ],
                Channels = [MessageChannel.DefaultPacket(), lane],
                Cpp = new CppOptions
                {
                    Clangd = clangd, CompileFrom = "flags", Standard = "c++20",
                    ExtraIncludeDirs = ["Common", "LoginServer/Src", "GameServer/Src"],
                    DetectMsvc = false, IndexTimeoutSeconds = 120,
                },
            };

            var graph = await new CppTraceAnalyzer().AnalyzeAsync(config, new WorkPaths(data, "multi"), null, CancellationToken.None);

            // 묶음 = 빌드 프로젝트 폴더(CMakeLists.txt), main 은 프로세스마다 따로
            var groups = graph.EntryPoints.Select(e => $"{e.Group}: {e.Label}").Order(StringComparer.Ordinal).ToList();
            Assert.Equal(["GameServer / GameHandler: EGameMsg::Tick", "GameServer / GameHandler: L2GEnter", "GameServer / main: main",
                          "LoginServer / LoginHandler: C2LLogin", "LoginServer / main: main"], groups);
            var mains = graph.EntryPoints.Where(e => e.Label == "main").Select(e => graph.Nodes[e.NodeId]).ToList();
            Assert.Equal(2, mains.Select(m => m.Id).Distinct().Count());
            var gameMain = mains.Single(m => m.File.StartsWith("GameServer/", StringComparison.Ordinal));
            var mainCalls = graph.Edges.Where(e => e.From == gameMain.Id).Select(e => graph.Nodes[e.To].Name).ToList();
            Assert.Contains("Config::Load", mainCalls);
            Assert.DoesNotContain("LoginHandler::Init", mainCalls);      // 다른 main 의 호출이 섞이지 않는다

            // 보내는 곳: 변수 타입으로 찾은 L2GEnter, 직접 쓴 G2CEnterResult, 레인 메시지
            var login = graph.Nodes.Values.Single(n => n.Name == "LoginHandler::HandleLogin");
            var enter = graph.Nodes.Values.Single(n => n.Name == "GameHandler::HandleEnter");
            var tick = graph.Nodes.Values.Single(n => n.Name == "GameHandler::OnTick");
            Assert.Contains(graph.Messages, m => m.Role == MessageRole.Send && m.NodeId == login.Id && m.Key == "L2GEnter" && m.How == "변수 타입");
            Assert.Contains(graph.Messages, m => m.Role == MessageRole.Send && m.NodeId == enter.Id && m.Key == "G2CEnterResult" && m.How == "직접");
            var laneEdge = Assert.Single(graph.Edges, e => e.From == enter.Id && e.To == tick.Id);
            Assert.Equal("Message", laneEdge.Resolution);

            // 여정: HandleEnter 앞은 LoginServer 의 HandleLogin, 뒤는 받는 곳 없는 G2CEnterResult
            var index = new TraceIndex(graph);
            var journey = new Journey(graph, index).Build(enter.Id);
            var from = Assert.Single(journey.Upstream.Children);
            Assert.Equal((login.Id, "C2LLogin", "LoginServer", "L2GEnter"), (from.NodeId, from.Key, from.Process, from.LinkKey));
            Assert.Equal(JourneyKind.Origin, Assert.Single(from.Children).Kind);   // Init 의 Register(&...) 는 호출이 아니다
            var exit = Assert.Single(journey.Downstream.Children);
            Assert.Equal((JourneyKind.Exit, "G2CEnterResult"), (exit.Kind, exit.LinkKey));

            // 표시: DB·캐시·작업 단위(레인 너머)·설정·네트워크
            var tags = FlowSummary.Build(graph, index, enter.Id).Items.Select(i => $"{string.Join(",", i.Tags)}:{graph.Nodes[i.CalleeId].Name}").ToList();
            Assert.Equal(["캐시:PlayerCache::Update", "작업 단위:UnitOfWork::Commit"], tags);
            var loginDb = FlowSummary.Build(graph, index, login.Id).Items.Where(i => i.Tags.Contains("DB")).Select(i => (i.CalleeId.Length > 0 ? graph.Nodes[i.CalleeId].Name : "본문", i.Line)).ToList();
            Assert.Equal([("Db::Query", 10), ("본문", 11)], loginDb);   // 호출로 잡히지 않는 DbCommand{"usp_..."} 줄도
            var start = FlowSummary.Build(graph, index, gameMain.Id).Items.Select(i => i.Tags[0]).Distinct().ToList();
            Assert.Equal(["설정", "등록", "네트워크"], start);

            // 클래스: 부모와 멤버
            var handler = graph.Types.Single(t => t.Name == "GameHandler");
            Assert.Equal(["HandlerBase"], handler.Bases);
            Assert.Contains(handler.Members, m => m.Name == "cache_" && m.Kind == "field");
            Assert.Contains(handler.Members, m => m.Name == "HandleEnter" && m.Kind == "method");
            Assert.Contains(graph.Types, t => t.Name == "HandlerBase");

            // 점검(4단계): 기본 락 규칙 + 순서 규칙(작업 단위는 레인 너머라 되돌리기로 치지 않게 unless 를 좁힌다)
            var order = CheckRule.Defaults().First(r => r.Kind == Checks.Order);
            order.Enabled = true;
            order.Unless = "(?i)refund|rollback";
            var report = Checks.Run([.. config.Checks, order], graph, index, Checks.ReadFrom(fixture));
            var grant = graph.Nodes.Values.Single(n => n.Name == "GameHandler::GrantReward");
            var locked = Assert.Single(report.Findings, f => f.Kind == Checks.ForbidInScope);
            Assert.Equal((grant.Id, Certainty.High), (locked.NodeId, locked.Certainty));
            Assert.Equal("Session::Flush 호출", Assert.Single(locked.Evidence).Note);
            Assert.Equal([enter.Id, grant.Id], locked.Path.Take(2));
            var reward = Assert.Single(report.Findings, f => f.Kind == Checks.Order);
            Assert.Equal("L2GEnter: Wallet::Spend 뒤 Inventory::AddItem", reward.Title);
            Assert.Equal(["먼저: Wallet::Spend", "나중: Inventory::AddItem"], reward.Evidence.Select(e => e.Note));
            Assert.Equal([RuleState.Ran, RuleState.Off, RuleState.Off, RuleState.Off, RuleState.Off, RuleState.Ran],
                         report.Runs.Select(r => r.State));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }
}
