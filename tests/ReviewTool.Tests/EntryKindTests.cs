using ReviewTool.Core;
using ReviewTool.Cpp;
using ReviewTool.CSharp;

namespace ReviewTool.Tests;

// 진입점 종류(화면 필터: 패킷 / 메시지 / 시작 / 테스트 / 함수)
public class EntryKindTests
{
    [Theory]
    [InlineData("멤버 함수 등록", EntryKind.Packet)]
    [InlineData("OnPacket 구현", EntryKind.Packet)]
    [InlineData("API 컨트롤러 액션", EntryKind.Packet)]
    [InlineData("레인 메시지", EntryKind.Message)]
    [InlineData("시작 함수(최상위 문)", EntryKind.Start)]
    [InlineData("gtest 테스트", EntryKind.Test)]
    [InlineData("xUnit 테스트", EntryKind.Test)]
    [InlineData("Core 핵심 함수", EntryKind.Function)]
    public void 규칙_이름으로_종류를_추정한다(string ruleName, string kind)
    {
        Assert.Equal(kind, EntryKind.Infer(ruleName));
    }

    [Fact]
    public void 적어_둔_종류가_이름_추정보다_먼저다()
    {
        // 이름은 "등록"(패킷)이지만 메시지라고 적었다. 한글 값도 받는다.
        Assert.Equal(EntryKind.Message, EntryKind.Of(new EntryPointRule { Name = "레인 핸들러 등록", Kind = "메시지" }));
        Assert.Equal(EntryKind.Function, EntryKind.Of(new EntryPointRule { Name = "핸들러 등록", Kind = "function" }));
        // 모르는 값이면 이름으로 추정한다
        Assert.Equal(EntryKind.Packet, EntryKind.Of(new EntryPointRule { Name = "핸들러 등록", Kind = "???" }));
        // 종류가 없던 때 저장한 결과는 Detector 로 추정한다
        Assert.Equal(EntryKind.Start, EntryKind.Of(new EntryPoint { Detector = "시작 함수" }));
        Assert.Equal(EntryKind.Test, EntryKind.Of(new EntryPoint { Detector = "시작 함수", Kind = EntryKind.Test }));
    }

    [Fact]
    public void 기본값인_macro_kind_는_설정_지문에_넣지_않는다()
    {
        // 이 값들이 생기기 전에 저장한 설정의 분석 결과가 "설정 바뀜"으로 보이지 않아야 한다
        var rule = new EntryPointRule { Name = "r", Pattern = "x" };
        var plain = new TargetConfig { Name = "t", Root = "C:/x", EntryPoints = [rule] };
        // 점검 규칙(checks)에도 kind 키가 있으므로 진입점 규칙만 본다
        var saved = System.Text.Json.JsonSerializer.SerializeToNode(plain, TargetConfig.JsonOptions)!["entryPoints"]![0]!.AsObject();

        Assert.False(saved.ContainsKey("macro"));
        Assert.False(saved.ContainsKey("kind"));

        var marked = new TargetConfig
        {
            Name = "t",
            Root = "C:/x",
            EntryPoints = [new EntryPointRule { Name = "r", Pattern = "x", Macro = true, Kind = EntryKind.Test }],
        };
        Assert.NotEqual(plain.ContentHash(), marked.ContentHash());
    }

    // 같은 함수를 일반 규칙(Handle*, 패킷)과 메시지 통로(crossProcess: false)가 함께 찾으면 메시지다(일반 규칙이 먼저 처리돼도).
    private static readonly MessageChannel Lane = new()
    {
        Name = "레인",
        CrossProcess = false,
        Send = @"\bPush\b",
        Key = @"\b(?<key>EMsg(?:\.|::)\w+)",
        Register = @"\bRegist\(\s*(?<key>EMsg(?:\.|::)\w+)\s*,\s*&?(?:(?<class>\w+)::)?(?<method>\w+)\s*\)",
    };

    [Fact]
    public async Task CSharp_메시지_통로_등록이_일반_규칙보다_우선한다()
    {
        var root = Directory.CreateTempSubdirectory("rt-kind-cs").FullName;
        var data = Directory.CreateTempSubdirectory("rt-kind-cs-data").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "App.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
            File.WriteAllText(Path.Combine(root, "Zone.cs"), """
                public enum EMsg { Tick }
                public class Lane { public void Regist(EMsg m, System.Action a) { } }
                public class Zone
                {
                    private readonly Lane _lane = new();
                    public void Init() { _lane.Regist(EMsg.Tick, HandleTick); }
                    public void HandleTick() { }
                }
                """);
            var handle = CSharpTargetDetector.Presets.Single(p => p.Name.StartsWith("Handle", StringComparison.Ordinal)).ToRule();
            var config = new TargetConfig { Name = "k", Root = root, Language = "csharp", EntryPoints = [handle], Channels = [Lane] };

            var graph = await new CSharpTraceAnalyzer().AnalyzeAsync(config, new WorkPaths(data, "k"), null, CancellationToken.None);

            var entry = Assert.Single(graph.EntryPoints);
            Assert.Equal(EntryKind.Message, EntryKind.Of(entry));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(data, recursive: true);
        }
    }

    // 실제 clangd 로 분석한다. clangd 가 없는 PC 에서는 아무것도 하지 않고 통과한다.
    [Fact]
    public async Task Cpp_메시지_통로_등록이_일반_규칙보다_우선한다()
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
        var root = Directory.CreateTempSubdirectory("rt-kind-cpp").FullName;
        var data = Directory.CreateTempSubdirectory("rt-kind-cpp-data").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Zone.cpp"), """
                enum class EMsg { Tick };
                struct Zone;
                struct Lane { void Regist(EMsg m, void (Zone::*h)()) {} };
                struct Zone
                {
                    Lane lane;
                    void Init();
                    void HandleTick();
                };
                void Zone::Init()
                {
                    lane.Regist(EMsg::Tick, &Zone::HandleTick);
                }
                void Zone::HandleTick()
                {
                }
                """);
            var handle = CppTargetDetector.Presets.Single(p => p.Name.StartsWith("Handle", StringComparison.Ordinal)).ToRule();
            var config = new TargetConfig
            {
                Name = "k",
                Root = root,
                EntryPoints = [handle],
                Channels = [Lane],
                Cpp = new CppOptions { Clangd = clangd, CompileFrom = "flags", Standard = "c++20", DetectMsvc = false, IndexTimeoutSeconds = 120 },
            };

            var graph = await new CppTraceAnalyzer().AnalyzeAsync(config, new WorkPaths(data, "k"), null, CancellationToken.None);

            var entry = Assert.Single(graph.EntryPoints);
            Assert.Equal(EntryKind.Message, EntryKind.Of(entry));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public void 자동_감지_규칙은_종류가_정해져_있다()
    {
        Assert.All(CppTargetDetector.Presets, p => Assert.NotNull(EntryKind.Normalize(p.Kind)));
        Assert.All(CSharpTargetDetector.Presets, p => Assert.NotNull(EntryKind.Normalize(p.Kind)));
        Assert.Equal(EntryKind.Test, CppTargetDetector.Presets.Single(p => p.Name == "gtest 테스트").Kind);
        Assert.Equal(EntryKind.Start, CppTargetDetector.Presets.Single(p => p.Name == "시작 함수").Kind);
    }
}
