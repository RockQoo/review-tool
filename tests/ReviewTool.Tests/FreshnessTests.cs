using ReviewTool.Core;

namespace ReviewTool.Tests;

public class FreshnessTests
{
    private static (string Root, TraceGraph Graph, string[] Files) Setup()
    {
        var root = Directory.CreateTempSubdirectory("rt-fresh").FullName;
        var a = Path.Combine(root, "a.cpp");
        var b = Path.Combine(root, "b.h");
        File.WriteAllText(a, "void a() {}");
        File.WriteAllText(b, "#pragma once");
        var graph = new TraceGraph
        {
            RootDirectory = root,
            Inputs = [InputFile.Read(root, a), InputFile.Read(root, b)],
            ConfigHash = "cfg1",
        };
        return (root, graph, [a, b]);
    }

    [Fact]
    public void 그대로면_최신()
    {
        var (root, graph, files) = Setup();
        try
        {
            Assert.Equal(FreshnessState.Fresh, Freshness.Evaluate(graph, files, "cfg1").State);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void 시각만_바뀌고_내용이_같으면_최신()
    {
        var (root, graph, files) = Setup();
        try
        {
            File.SetLastWriteTimeUtc(files[0], DateTime.UtcNow.AddMinutes(5));   // git checkout 같은 경우
            Assert.Equal(FreshnessState.Fresh, Freshness.Evaluate(graph, files, "cfg1").State);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void 내용_변경_새_파일_삭제_설정_변경을_각각_잡는다()
    {
        var (root, graph, files) = Setup();
        try
        {
            File.WriteAllText(files[0], "void a() { b(); }");
            File.Delete(files[1]);
            var c = Path.Combine(root, "c.cpp");
            File.WriteAllText(c, "void c() {}");

            var r = Freshness.Evaluate(graph, [files[0], c], "cfg2");

            Assert.Equal(FreshnessState.Stale, r.State);
            Assert.Equal(["a.cpp"], r.Changed);
            Assert.Equal(["c.cpp"], r.Added);
            Assert.Equal(["b.h"], r.Removed);
            Assert.True(r.ConfigChanged);
            Assert.Equal(4, r.Count);
            Assert.True(r.IsChanged("a.cpp"));
            Assert.True(r.IsChanged("b.h"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void 입력_기록이_없으면_최신이라고_하지_않는다()
    {
        var graph = new TraceGraph { RootDirectory = Path.GetTempPath() };
        Assert.Equal(FreshnessState.Unknown, Freshness.Evaluate(graph, [], null).State);
    }
}
