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

    [Fact]
    public void 자동_감지_규칙은_종류가_정해져_있다()
    {
        Assert.All(CppTargetDetector.Presets, p => Assert.NotNull(EntryKind.Normalize(p.Kind)));
        Assert.All(CSharpTargetDetector.Presets, p => Assert.NotNull(EntryKind.Normalize(p.Kind)));
        Assert.Equal(EntryKind.Test, CppTargetDetector.Presets.Single(p => p.Name == "gtest 테스트").Kind);
        Assert.Equal(EntryKind.Start, CppTargetDetector.Presets.Single(p => p.Name == "시작 함수").Kind);
    }
}
