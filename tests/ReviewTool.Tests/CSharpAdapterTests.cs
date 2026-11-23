using ReviewTool.Core;
using ReviewTool.CSharp;

namespace ReviewTool.Tests;

// 5단계: C# 어댑터(Roslyn). 외부 도구 없이 돌아 늘 실행된다. 예제는 Fixtures/csharp(두 프로젝트: Common, GameServer).
public class CSharpAdapterTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "csharp");

    // main 이 부르는 함수
    private static readonly string[] MainCalls = ["Game::Server::Config::Load", "Game::Common::Dispatcher::Register", "Game::Server::LoginHandler::LoginHandler"];

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
        var send = index.ChildrenOf(login.Id).Single(e => g.Nodes[e.To].Name.EndsWith("Session::Send", StringComparison.Ordinal));
        Assert.Equal((16, 20), (send.CallSites[0].Line, send.CallSites[0].Column));   // 메서드 이름 위치(열은 0부터)

        // 인터페이스 메서드: 구현 후보(Roslyn 상속 정보), 바깥 라이브러리: 외부
        var load = g.Nodes.Values.Single(n => n.Name == "Game::Common::IRepository::Load");
        Assert.Equal(StopReason.NoDefinition, load.Stop);
        Assert.Equal(["Game::Server::SqlRepository::Load"], load.Implementations.Select(i => i.Name));
        Assert.Equal(StopReason.External, g.Nodes.Values.Single(n => n.Name.EndsWith("List::ForEach", StringComparison.Ordinal)).Stop);

        // main: 설정 → 생성자 → 등록(주소만 넘긴 HandleLogin 은 호출이 아니다)
        var main = g.Nodes[g.EntryPoints[1].NodeId];
        Assert.Equal(MainCalls.Order(),   // SqlRepository 의 기본 생성자는 컴파일러가 만든 것이라 뺀다
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

    [Fact]
    public async Task 최상위_문만_있는_프로젝트_한_폴더의_여러_csproj_노드_상한()
    {
        var root = Directory.CreateTempSubdirectory("rt-cs-top").FullName;
        var data = Directory.CreateTempSubdirectory("rt-cs-data").FullName;
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root, "App")).FullName;
            File.WriteAllText(Path.Combine(app, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(app, "App.Other.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");   // 같은 폴더에 하나 더
            File.WriteAllText(Path.Combine(app, "Program.cs"), "Work.A();\nWork.B();\n");
            File.WriteAllText(Path.Combine(app, "Work.cs"),
                "static class Work\n{\n    public static void A() { C(); D(); }\n    public static void B() { }\n    static void C() { }\n    static void D() { }\n}\n");

            var s = CSharpTargetDetector.Suggest(root, "top");
            var start = Assert.Single(s.Presets, p => p.Enabled);
            Assert.Equal("시작 함수", start.Preset.Name);                                      // 최상위 문도 시작 지점
            Assert.Contains("최상위 문(App/Program.cs)", start.Samples);

            var g = await new CSharpTraceAnalyzer().AnalyzeAsync(s.Config, new WorkPaths(data, "top"), null, CancellationToken.None);
            var main = g.Nodes[Assert.Single(g.EntryPoints).NodeId];
            Assert.Equal(("Program::Main", "App / Program"), (main.Name, g.EntryPoints[0].Group));
            Assert.Contains(g.Diagnostics, d => d.Contains(".csproj 가 2개", StringComparison.Ordinal));
            Assert.Equal(5, g.Nodes.Count);                                                      // Main, A, B, C, D

            s.Config.MaxNodes = 3;                                                               // 상한은 노드를 만들기 전에
            var cut = await new CSharpTraceAnalyzer().AnalyzeAsync(s.Config, new WorkPaths(data, "top"), null, CancellationToken.None);
            Assert.Equal(3, cut.Nodes.Count);
            Assert.Contains(cut.Diagnostics, d => d.Contains("노드 수 제한(3)", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(data, recursive: true);
        }
    }
}
