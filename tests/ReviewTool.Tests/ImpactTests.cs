using ReviewTool.Core;

namespace ReviewTool.Tests;

public class ImpactTests
{
    // 진입점 E1 → A → C, 진입점 E2 → B → A → C, E3 → D (C 와 무관), 재귀 A → A
    private static TraceGraph Sample()
    {
        var g = new TraceGraph();
        foreach (var id in new[] { "E1", "E2", "E3", "A", "B", "C", "D" })
        {
            g.Nodes[id] = new TraceNode { Id = id, Name = id };
        }
        foreach (var (f, t) in new[] { ("E1", "A"), ("A", "C"), ("E2", "B"), ("B", "A"), ("E3", "D"), ("A", "A") })
        {
            g.Edges.Add(new TraceEdge { From = f, To = t });
        }
        g.EntryPoints =
        [
            new EntryPoint { NodeId = "E1", Label = "P1", Group = "G" },
            new EntryPoint { NodeId = "E2", Label = "P2", Group = "G" },
            new EntryPoint { NodeId = "E3", Label = "P3", Group = "G" },
        ];
        return g;
    }

    [Fact]
    public void 닿는_진입점과_가장_짧은_경로()
    {
        var r = ImpactAnalyzer.Analyze(Sample(), "C");

        Assert.Equal(["P1", "P2"], r.Entries.Select(e => e.Entry.Label));
        Assert.Equal(["E1", "A", "C"], r.Entries[0].Path);
        Assert.Equal(["E2", "B", "A", "C"], r.Entries[1].Path);
        Assert.Equal(4, r.CallerCount);   // A, E1, B, E2
    }

    [Fact]
    public void 진입점_자신이면_경로는_하나()
    {
        var r = ImpactAnalyzer.Analyze(Sample(), "E3");
        var hit = Assert.Single(r.Entries);
        Assert.Equal(["E3"], hit.Path);
    }

    [Fact]
    public void 아무도_안_부르면_빈_목록()
    {
        var g = Sample();
        g.Nodes["X"] = new TraceNode { Id = "X", Name = "X" };
        Assert.Empty(ImpactAnalyzer.Analyze(g, "X").Entries);
    }
}
