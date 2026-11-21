using ReviewTool.Core;
using static ReviewTool.Core.ReviewLog;

namespace ReviewTool.Tests;

// 7단계 ②: 검토 기록(확인·보류·메모, 본문이 바뀌면 다시 봐야 함, 진입점별 진행률)
public class ReviewLogTests
{
    private static TraceNode Fn(string id, string name, string code, NodeKind kind = NodeKind.Function) =>
        new() { Id = id, Name = name, File = "f.cpp", Code = code, StartLine = 1, Kind = kind };

    private static (TraceGraph, TraceIndex) Graph()
    {
        // E → A → B, E → ext
        var g = new TraceGraph();
        foreach (var n in new[] { Fn("E", "Handler::E", "{ A(); }"), Fn("A", "A", "{ B(); }"), Fn("B", "B", "{ }"), Fn("X", "std::x", "", NodeKind.External) })
        {
            g.Nodes[n.Id] = n;
        }
        foreach (var (f, t) in new[] { ("E", "A"), ("A", "B"), ("E", "X") })
        {
            g.Edges.Add(new TraceEdge { From = f, To = t });
        }
        return (g, new TraceIndex(g));
    }

    [Fact]
    public void 확인_보류_다시_봐야_함과_진행률()
    {
        var (g, index) = Graph();
        var log = new ReviewLog();
        Assert.Equal((0, 3), log.Progress(g, index, "E"));          // 외부 함수는 세지 않는다

        log.Set(g.Nodes["A"], Mark.Checked, "락 확인함");
        log.Set(g.Nodes["B"], Mark.Hold);
        Assert.Equal((State.Checked, State.Hold, State.None), (log.StateOf(g.Nodes["A"]), log.StateOf(g.Nodes["B"]), log.StateOf(g.Nodes["E"])));
        Assert.Equal((1, 3), log.Progress(g, index, "E"));

        // AI 가 A 본문을 고쳤다(다시 분석) → 다시 봐야 함, 진행률에서 빠짐. 메모는 남는다
        g.Nodes["A"].Code = "{ B(); C(); }";
        Assert.Equal(State.Stale, log.StateOf(g.Nodes["A"]));
        Assert.Equal((0, 3), log.Progress(g, index, "E"));
        Assert.Equal("락 확인함", log.Find(g.Nodes["A"])!.Note);

        log.Set(g.Nodes["B"], null);                                   // 표시 지우기
        Assert.Equal(State.None, log.StateOf(g.Nodes["B"]));
        log.SetNote(g.Nodes["E"], "  나중에 볼 것 ");                  // 메모만 쓰면 보류
        Assert.Equal((State.Hold, "나중에 볼 것"), (log.StateOf(g.Nodes["E"]), log.Find(g.Nodes["E"])!.Note));
    }

    [Fact]
    public void 저장과_불러오기_열쇠는_이름과_시그니처()
    {
        var (g, _) = Graph();
        var log = new ReviewLog();
        log.Set(g.Nodes["A"], Mark.Checked, "메모 한글");
        var dir = Directory.CreateTempSubdirectory("rt-review").FullName;
        try
        {
            var path = Path.Combine(dir, "review.json");
            log.Save(path);
            var back = ReviewLog.Load(path);
            Assert.Equal(State.Checked, back.StateOf(g.Nodes["A"]));
            Assert.Contains("메모 한글", File.ReadAllText(path));              // 한글을 \uXXXX 로 바꾸지 않는다
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

            // 다시 분석해 id 가 바뀌어도 이름·시그니처가 같으면 같은 함수
            var renamedId = Fn("A2", "A", "{ B(); }");
            Assert.Equal(State.Checked, back.StateOf(renamedId));
            renamedId.Signature = "(int)";
            Assert.Equal(State.None, back.StateOf(renamedId));                  // 시그니처가 생기면 다른 열쇠

            File.WriteAllText(path, "{ 깨진");
            Assert.Empty(ReviewLog.Load(path).Items);                            // 깨진 파일은 빈 기록으로
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
