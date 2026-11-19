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
}
