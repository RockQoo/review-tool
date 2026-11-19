using ReviewTool.Core;
using ReviewTool.CSharp;

namespace ReviewTool.Tests;

// 5단계: C# 어댑터(Roslyn). 외부 도구 없이 돌아 늘 실행된다. 예제는 Fixtures/csharp(두 프로젝트: Common, GameServer).
public class CSharpAdapterTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "csharp");

    [Fact]
    public void 폴더를_보고_CSharp_설정을_제안한다()
    {
        Assert.True(CSharpTargetDetector.LooksLikeCSharp(Fixture));
        Assert.False(CSharpTargetDetector.LooksLikeCSharp(Path.Combine(AppContext.BaseDirectory, "Fixtures", "multi")));

        var s = CSharpTargetDetector.Suggest(Fixture, "cs-sample");

        Assert.Equal("csharp", s.Config.Language);
        Assert.Equal(["메서드 등록", "시작 함수"], s.Presets.Where(p => p.Enabled).Select(p => p.Preset.Name));
        Assert.Contains("handler.HandleLogin", s.Presets.First().Samples.Select(x => "handler." + x));
        Assert.Equal(3, s.SourceCount);   // Net.cs, Program.cs, LoginHandler.cs (bin/obj 제외)
    }

    private static async Task<(TraceGraph Graph, TraceIndex Index, TargetConfig Config)> Analyze()
    {
        var config = CSharpTargetDetector.Suggest(Fixture, "cs-sample").Config;
        var data = Directory.CreateTempSubdirectory("rt-cs").FullName;
        try
        {
            var graph = await new CSharpTraceAnalyzer().AnalyzeAsync(config, new WorkPaths(data, "cs-sample"), null, CancellationToken.None);
            return (graph, new TraceIndex(graph), config);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public async Task 진입점과_호출_트리를_만든다()
    {
        var (g, index, _) = await Analyze();

        Assert.Equal(["GameServer / LoginHandler: C2SLogin", "GameServer / Program: Main"],
                     g.EntryPoints.Select(e => $"{e.Group}: {e.Label}"));
        var login = g.Nodes[g.EntryPoints[0].NodeId];
        Assert.Equal("Game::Server::LoginHandler::HandleLogin", login.Name);
        Assert.Equal("GameServer/LoginHandler.cs", login.File);
        Assert.Contains("lock (gate_)", login.Code);

        var calls = index.ChildrenOf(login.Id).Select(e => g.Nodes[e.To].Name).ToList();
        Assert.Equal(["Game::Common::IRepository::Load", "Game::Common::Session::Send", "System::Collections::Generic::List::ForEach",
                      "Game::Common::Audit::Write"], calls);                       // 소스 순서, 람다 안 호출 포함
        var send = index.ChildrenOf(login.Id).Single(e => g.Nodes[e.To].Name.EndsWith("Session::Send"));
        Assert.Equal((16, 20), (send.CallSites[0].Line, send.CallSites[0].Column));   // 메서드 이름 위치(열은 0부터)

        // 인터페이스 메서드: 구현 후보(Roslyn 상속 정보), 바깥 라이브러리: 외부
        var load = g.Nodes.Values.Single(n => n.Name == "Game::Common::IRepository::Load");
        Assert.Equal(StopReason.NoDefinition, load.Stop);
        Assert.Equal(["Game::Server::SqlRepository::Load"], load.Implementations.Select(i => i.Name));
        Assert.Equal(StopReason.External, g.Nodes.Values.Single(n => n.Name.EndsWith("List::ForEach")).Stop);

        // main: 설정 → 생성자 → 등록(주소만 넘긴 HandleLogin 은 호출이 아니다)
        var main = g.Nodes[g.EntryPoints[1].NodeId];
        Assert.Equal(new[] { "Game::Server::Config::Load", "Game::Common::Dispatcher::Register", "Game::Server::LoginHandler::LoginHandler" }.Order(),   // SqlRepository 의 기본 생성자는 컴파일러가 만든 것이라 뺀다
                     index.ChildrenOf(main.Id).Select(e => g.Nodes[e.To].Name).Where(n => !n.StartsWith("ext", StringComparison.Ordinal)).Order());
    }

    [Fact]
    public async Task 메시지_표시_점검_클래스가_언어와_무관하게_붙는다()
    {
        var (g, index, config) = await Analyze();
        var login = g.EntryPoints[0].NodeId;

        Assert.Contains(g.Messages, m => m.Role == MessageRole.Send && m.NodeId == login && m.Key == "S2CLoginResult" && m.How == "직접");

        var report = Checks.Run(config.Checks, g, index, Checks.ReadFrom(config.Root));
        var locked = Assert.Single(report.Findings, f => f.Kind == Checks.ForbidInScope);   // C# lock (x) { } 안의 Send
        Assert.Equal((login, Certainty.High), (locked.NodeId, locked.Certainty));
        Assert.Equal([16], locked.Evidence.Select(e => e.Site.Line));

        var handler = g.Types.Single(t => t.Name == "Game::Server::LoginHandler");
        Assert.Contains(handler.Members, m => m.Name == "gate_" && m.Kind == "field");
        Assert.Contains(handler.Members, m => m.Name == "HandleLogin" && m.Kind == "method");
        Assert.Equal(["IRepository"], g.Types.Single(t => t.Name == "Game::Server::SqlRepository").Bases);
        Assert.Contains(g.Inputs, i => i.File == "GameServer/GameServer.csproj");     // 신선도: 프로젝트 파일도
    }
}
