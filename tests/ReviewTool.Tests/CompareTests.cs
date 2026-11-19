using ReviewTool.Core;
using ReviewTool.Cpp;

namespace ReviewTool.Tests;

public class CompareTests
{
    [Fact]
    public void 공백과_주석만_바뀐_본문은_같다()
    {
        var a = "void A::Run(int x)\n{\n    Go(x); // 이동\n}";
        var b = "void A::Run(int x) {\n  /* 바뀐 주석 */ Go( x );\n}";
        Assert.Equal(CodeFingerprint.Body(a), CodeFingerprint.Body(b));
        Assert.NotEqual(CodeFingerprint.Body(a), CodeFingerprint.Body("void A::Run(int x)\n{\n    Stop(x);\n}"));
        Assert.Equal("void A::Run(int x)", CodeFingerprint.Signature(a));
    }

    private static TraceNode Fn(string name, string code) => new()
    {
        Id = name + "@id", Name = name, File = "a.cpp", StartLine = 1, Code = code,
        Signature = CodeFingerprint.Signature(code), BodyHash = CodeFingerprint.Body(code),
    };

    private static TraceGraph Graph(params TraceNode[] nodes)
    {
        var g = new TraceGraph { RootDirectory = "r", ConfigHash = "c" };
        foreach (var n in nodes)
        {
            g.Nodes[n.Id] = n;
        }
        return g;
    }

    [Fact]
    public void 본문_시그니처_추가_삭제_호출_변화를_가른다()
    {
        var before = Graph(
            Fn("H", "void H() { A(); }"),
            Fn("A", "void A() { x = 1; }"),
            Fn("B", "void B(int v) { y = v; }"),
            Fn("Old", "void Old() { z = 3; }"));
        before.Edges.Add(new TraceEdge { From = "H@id", To = "A@id" });
        before.EntryPoints.Add(new EntryPoint { NodeId = "H@id", Label = "P", Group = "G" });

        var after = Graph(
            Fn("H", "void H() { A(); C(); }"),
            Fn("A", "void A() { x = 1; }"),
            Fn("B", "void B(int v, int w) { y = v; }"),
            Fn("C", "void C() { w = 2; }"),
            Fn("New", "void New() { z = 3; }"));
        after.Edges.Add(new TraceEdge { From = "H@id", To = "A@id" });
        after.Edges.Add(new TraceEdge { From = "H@id", To = "C@id" });
        after.EntryPoints.Add(new EntryPoint { NodeId = "H@id", Label = "P", Group = "G" });

        var r = SnapshotComparer.Compare(before, after);

        Assert.Empty(r.ConditionWarnings);
        Assert.Equal(["H"], r.BodyChanged.Select(f => f.Name));
        Assert.Equal(["B"], r.SignatureChanged.Select(p => p.After.Name));
        Assert.Equal(["C", "New"], r.FunctionsAdded.Select(f => f.Name));
        Assert.Equal(["Old"], r.FunctionsRemoved.Select(f => f.Name));
        Assert.Equal([("Old", "New")], r.RenameCandidates.Select(p => (p.Before.Name, p.After.Name)));
        Assert.Equal([("H", "C")], r.CallsAdded);
        Assert.Empty(r.CallsRemoved);
        Assert.Empty(r.EntriesAdded);
        Assert.True(r.HasChanges);
    }

    [Fact]
    public void 같은_결과면_차이_없음_설정이_다르면_경고()
    {
        var g = Graph(Fn("A", "void A() {}"));
        Assert.False(SnapshotComparer.Compare(g, g).HasChanges);

        var other = Graph(Fn("A", "void A() {}"));
        other.ConfigHash = "c2";
        Assert.Single(SnapshotComparer.Compare(g, other).ConditionWarnings);
    }

    // 실제 clangd 로: 예제를 분석 → 코드 수정 → 재분석 → 비교
    [Fact]
    public async Task 실제_수정_전후를_비교한다()
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
        var work = Directory.CreateTempSubdirectory("rt-cmp").FullName;
        try
        {
            var root = Path.Combine(work, "mini");
            CopyDir(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mini"), root);
            var config = new TargetConfig
            {
                Name = "mini", Root = root,
                EntryPoints = [new EntryPointRule { Name = "Register", Pattern = @"Register\(\s*&(?<class>\w+)::(?<method>\w+)\s*\)" }],
                Cpp = new CppOptions
                {
                    Clangd = clangd, CompileFrom = "flags", Standard = "c++20", ExtraIncludeDirs = ["Src"],
                    DetectMsvc = false, IndexTimeoutSeconds = 120,
                },
            };
            var paths = new WorkPaths(Path.Combine(work, "data"), "mini");
            var before = await new CppTraceAnalyzer().AnalyzeAsync(config, paths, null, CancellationToken.None);

            // World::Move 가 Broadcast 를 안 부르게 바꾼다(AI 가 흐름을 바꾼 상황)
            var world = Path.Combine(root, "Src", "World.cpp");
            File.WriteAllText(world, File.ReadAllText(world).Replace("    Broadcast(session);\n", "    (void)session;\n"));
            Assert.Equal(FreshnessState.Stale,
                Freshness.Evaluate(before, new CppTraceAnalyzer().EnumerateInputs(config), config.ContentHash()).State);

            var after = await new CppTraceAnalyzer().AnalyzeAsync(config, paths, null, CancellationToken.None);
            var r = SnapshotComparer.Compare(before, after);

            Assert.Contains(r.BodyChanged, f => f.Name == "World::Move");
            Assert.Contains(("World::Move", "World::Broadcast"), r.CallsRemoved);
            Assert.Contains(r.FunctionsRemoved, f => f.Name == "World::Broadcast");   // 이제 진입점에서 닿지 않음
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static void CopyDir(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }
    }
}
