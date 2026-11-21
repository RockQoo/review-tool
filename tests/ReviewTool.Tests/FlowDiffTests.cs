using ReviewTool.Core;
using static ReviewTool.Core.LineDiff;

namespace ReviewTool.Tests;

// 7단계 ①: 흐름 안 전후 비교(줄 단위 비교, 기준 쪽 같은 함수 찾기)
public class FlowDiffTests
{
    private static string Show(IEnumerable<Line> lines) =>
        string.Join(" ", lines.Select(l => (l.Op switch { Op.Added => "+", Op.Removed => "-", _ => "=" }) + l.Text.Trim()));

    [Fact]
    public void 바뀐_줄은_지운_줄_다음_더한_줄로()
    {
        var before = new[] { "void F()", "{", "    if (!ok)", "        return;", "    Send();", "}" };
        var after = new[] { "void F()", "{", "    if (!ok || empty)", "        return;", "    Log();", "    Send();", "}" };

        var diff = Compare(before, 10, after, 12);

        Assert.Equal("=void F() ={ -if (!ok) +if (!ok || empty) =return; +Log(); =Send(); =}", Show(diff));
        var changed = diff.Single(l => l.Op == Op.Added && l.Text.Contains("empty"));
        Assert.Equal((null, 14), (changed.Before, changed.After));                              // 지금 파일 줄 번호
        Assert.Equal(12, diff.Single(l => l.Op == Op.Removed).Before);                         // 기준 파일 줄 번호
        Assert.Equal((2, 1), Count(diff));
    }

    [Fact]
    public void 들여쓰기만_바뀐_줄은_같은_줄()
    {
        var diff = Compare(["  a();", "b( x );"], 1, ["\ta();", "b( x );"], 1);
        Assert.All(diff, l => Assert.Equal(Op.Same, l.Op));
        Assert.Equal("+x", Show(Compare([], 1, ["x"], 1)));
        Assert.Equal("-x", Show(Compare(["x"], 1, [], 1)));
    }

    private static TraceNode Fn(string id, string name, string file, string signature = "", string code = "{}") =>
        new() { Id = id, Name = name, File = file, Signature = signature, Code = code, StartLine = 1 };

    [Fact]
    public void 기준에서_같은_함수_찾기()
    {
        var baseline = new TraceGraph();
        foreach (var n in new[]
                 {
                     Fn("b1", "Zone::Fanout", "Zone.cpp", "(int)"), Fn("b2", "Zone::Fanout", "Zone.cpp", "(int, int)"),
                     Fn("b3", "Helper", "A.cpp"), Fn("b4", "Helper", "B.cpp"), Fn("b5", "Only", "X.cpp"),
                 })
        {
            baseline.Nodes[n.Id] = n;
        }

        Assert.Equal("b2", SnapshotComparer.Counterpart(baseline, Fn("a", "Zone::Fanout", "Zone.cpp", "(int, int)"))?.Id);   // 오버로드는 시그니처로
        Assert.Equal("b4", SnapshotComparer.Counterpart(baseline, Fn("a", "Helper", "B.cpp"))?.Id);                          // 같은 이름 여럿이면 같은 파일
        Assert.Null(SnapshotComparer.Counterpart(baseline, Fn("a", "Helper", "C.cpp")));                                     // 확실하지 않으면 없음
        Assert.Equal("b5", SnapshotComparer.Counterpart(baseline, Fn("a", "Only", "Moved.cpp"))?.Id);                        // 하나뿐이면 파일이 달라도
        Assert.Null(SnapshotComparer.Counterpart(baseline, Fn("a", "New", "X.cpp")));
    }

    [Fact]
    public void 본문_변경은_공백_주석만이면_아니다()
    {
        var g1 = new TraceGraph();
        var g2 = new TraceGraph();
        var before = Fn("b", "F", "f.cpp", code: "{ a(); }");
        Assert.False(SnapshotComparer.BodyChanged(g1, before, g2, Fn("a", "F", "f.cpp", code: "{\n  a(); // 설명\n}")));
        Assert.True(SnapshotComparer.BodyChanged(g1, before, g2, Fn("a", "F", "f.cpp", code: "{ a(); b(); }")));
    }
}
