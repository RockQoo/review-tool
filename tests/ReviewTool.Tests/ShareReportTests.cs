using ReviewTool.Core;

namespace ReviewTool.Tests;

// 5단계: 공유 보고서. 기본은 소스 본문과 절대 경로를 뺀다.
public class ShareReportTests
{
    private static (TraceGraph, TraceIndex) Sample()
    {
        var g = new TraceGraph
        {
            TargetName = "t", Language = "cpp", RootDirectory = @"C:\Work\Secret\repo",
            Diagnostics = [@"C:\Work\Secret\repo\a.cpp 를 열지 못함"],
        };
        g.Nodes["H"] = new TraceNode
        {
            Id = "H", Name = "Login::Handle", Kind = NodeKind.Method, File = "src/Login.cpp", StartLine = 10, EndLine = 14,
            Code = "void Login::Handle()\n{\n    Db::Query(\"usp_secret\");\n    std::lock_guard l(m);\n    Net::Send();\n}",
            TagLines = [new TaggedLine { Line = 12, Tags = ["DB"] }],
        };
        g.Nodes["D"] = new TraceNode { Id = "D", Name = "Db::Query", Kind = NodeKind.Function, File = "src/Db.cpp", StartLine = 1, Code = "void Query() {}", Tags = ["DB"] };
        g.Nodes["S"] = new TraceNode { Id = "S", Name = "Net::Send", Kind = NodeKind.Function, File = "src/Net.cpp", StartLine = 1, Code = "void Send() {}" };
        g.Nodes["O"] = new TraceNode { Id = "O", Name = "Other::Run", Kind = NodeKind.Function, File = "src/Other.cpp", StartLine = 1, Code = "void Run() {}" };
        g.Edges.Add(new TraceEdge { From = "H", To = "D", CallSites = [new SourceRef { File = "src/Login.cpp", Line = 12, Column = 4 }] });
        g.Edges.Add(new TraceEdge { From = "H", To = "S", CallSites = [new SourceRef { File = "src/Login.cpp", Line = 14, Column = 9 }] });
        g.EntryPoints.Add(new EntryPoint { NodeId = "H", Label = "C2SLogin", Group = "Server / Login", Detector = "등록", RegisteredAt = new SourceRef { File = "src/Login.cpp", Line = 3 } });
        g.EntryPoints.Add(new EntryPoint { NodeId = "O", Label = "Run", Group = "Server / Other", Detector = "등록" });
        return (g, new TraceIndex(g));
    }

    private static Checks.Report RunChecks(TraceGraph g, TraceIndex i) => Checks.Run(CheckRule.Defaults(), g, i, _ => null);

    [Fact]
    public void 기본은_소스_본문과_절대_경로를_뺀다()
    {
        var (g, i) = Sample();
        var text = ShareReport.Build(g, i, new ShareReport.Options { EntryNodeIds = ["H"] }, RunChecks(g, i));

        Assert.Contains("## C2SLogin  (Server / Login)", text);
        Assert.Contains("Db::Query", text);                       // 구조(이름·파일:줄)는 남는다
        Assert.Contains("src/Login.cpp:14", text);                 // 점검 근거 위치
        Assert.Contains("락 안에서 I/O", text);
        Assert.DoesNotContain("usp_secret", text);                 // 코드·줄 텍스트 없음
        Assert.DoesNotContain("lock_guard", text);
        Assert.DoesNotContain("Secret", text);
        Assert.DoesNotContain("## Run", text);                     // 고르지 않은 진입점
        Assert.Contains("증명하지 않는 것", text);
    }

    [Fact]
    public void 옵션을_켜면_코드와_줄_텍스트가_들어간다()
    {
        var (g, i) = Sample();
        var text = ShareReport.Build(g, i, new ShareReport.Options { IncludeSource = true, IncludeAbsolutePaths = true }, RunChecks(g, i));

        Assert.Contains("```\nvoid Login::Handle()", text);
        Assert.Contains("`Net::Send();`", text);                  // 점검 근거 줄
        Assert.Contains("## Run  (Server / Other)", text);         // 비우면 전체
    }

    [Theory]
    [InlineData(@"열지 못함: C:\Users\me\x.cpp 끝", "열지 못함: <경로> 끝")]
    [InlineData(@"루트 C:\Work\Secret\repo\src\a.cpp", "루트 <분석 폴더>\\src\\a.cpp")]
    [InlineData("루트 C:/Work/Secret/repo/src", "루트 <분석 폴더>/src")]
    [InlineData(@"공유 \\server\share\a.h 와 /home/me/a.h", "공유 <경로> 와 <경로>")]
    [InlineData("상대 src/a.cpp:3 와 Ns::A", "상대 src/a.cpp:3 와 Ns::A")]
    public void 절대_경로_가리기(string input, string expected)
    {
        Assert.Equal(expected, ShareReport.HidePaths(input, @"C:\Work\Secret\repo"));
    }
}
