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

    [Fact]
    public void 문자열_안의_공백_변경은_본문_변경이다()
    {
        Assert.NotEqual(CodeFingerprint.Body("void F() { Send(\"a b\"); }"), CodeFingerprint.Body("void F() { Send(\"ab\"); }"));
        Assert.NotEqual(CodeFingerprint.Body("void F() { char c = ' '; }"), CodeFingerprint.Body("void F() { char c = '_'; }"));
        // 식별자 사이 공백은 토큰 경계
        Assert.NotEqual(CodeFingerprint.Body("void F() { return x; }"), CodeFingerprint.Body("void F() { returnx; }"));
        // 문자열 안의 // 는 주석이 아니다
        Assert.NotEqual(CodeFingerprint.Body("void F() { Log(\"http://a\"); }"), CodeFingerprint.Body("void F() { Log(\"http://b\"); }"));
    }

    [Fact]
    public void 파일이_다른_같은_이름_함수를_따로_비교한다()
    {
        TraceNode Static(string id, string file, string code) => new()
        {
            Id = id, Name = "Helper", File = file, StartLine = 1, Code = code,
            Signature = CodeFingerprint.Signature(code), BodyHash = CodeFingerprint.Body(code),
        };
        var before = Graph(Static("a", "a.cpp", "static void Helper() { x = 1; }"), Static("b", "b.cpp", "static void Helper() { y = 1; }"));
        var after = Graph(Static("a", "a.cpp", "static void Helper() { x = 1; }"), Static("b", "b.cpp", "static void Helper() { y = 2; }"));

        var r = SnapshotComparer.Compare(before, after);

        var changed = Assert.Single(r.BodyChanged);
        Assert.Equal("b.cpp", changed.File);
    }

    [Fact]
    public void 오버로드가_바뀐_호출을_잡는다()
    {
        var before = Graph(Fn("H", "void H() { Foo(1); }"), Fn("Foo", "void Foo(int v) { }"), FnId("Foo2", "Foo", "void Foo(const char* s) { }"));
        before.Edges.Add(new TraceEdge { From = "H@id", To = "Foo@id" });
        var after = Graph(Fn("H", "void H() { Foo(\"x\"); }"), Fn("Foo", "void Foo(int v) { }"), FnId("Foo2", "Foo", "void Foo(const char* s) { }"));
        after.Edges.Add(new TraceEdge { From = "H@id", To = "Foo2@id" });

        var r = SnapshotComparer.Compare(before, after);

        Assert.Single(r.CallsAdded);
        Assert.Single(r.CallsRemoved);
        Assert.Contains("const char* s", r.CallsAdded[0].To);   // 이름이 겹치면 시그니처를 붙여 보여 준다
    }

    [Fact]
    public void 기준_목록은_분석_시각을_읽는다()
    {
        var data = Directory.CreateTempSubdirectory("rt-snap").FullName;
        try
        {
            var paths = new WorkPaths(data, "t");
            var g = Graph(Fn("A", "void A() {}"));
            g.AnalyzedAt = new DateTimeOffset(2026, 11, 20, 1, 2, 3, TimeSpan.Zero);
            TraceStore.Save(g, paths.TraceFile);
            SnapshotStore.KeepPrevious(paths);
            var saved = SnapshotStore.SaveBaseline(paths, g);

            var list = SnapshotStore.List(paths);

            Assert.Equal([SnapshotStore.PreviousId, saved.Id], list.Select(s => s.Id));
            Assert.All(list, s => Assert.Equal(g.AnalyzedAt, s.AnalyzedAt));
            Assert.NotNull(SnapshotStore.Load(paths, saved.Id));
            Assert.Null(SnapshotStore.Load(paths, "../t/trace"));
        }
        finally
        {
            Directory.Delete(data, true);
        }
    }

    private static TraceNode FnId(string id, string name, string code)
    {
        var n = Fn(name, code);
        n.Id = id + "@id";
        return n;
    }

    private static TraceNode Fn(string name, string code) => new()
    {
        Id = name + "@id", Name = name, File = "a.cpp", StartLine = 1, Code = code,
        Signature = CodeFingerprint.Signature(code), BodyHash = CodeFingerprint.Body(code),
    };

    private static TraceGraph Graph(params TraceNode[] nodes)
    {
        var g = new TraceGraph { RootDirectory = "r", ConfigHash = "c", FingerprintVersion = CodeFingerprint.Version };
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

        // 지문 방식이 다른 옛 결과: 본문은 비교하지 않고 경고만
        var old = Graph(Fn("A", "void A() { x = 1; }"));
        old.FingerprintVersion = 1;
        var r = SnapshotComparer.Compare(old, Graph(Fn("A", "void A() { x = 2; }")));
        Assert.Empty(r.BodyChanged);
        Assert.Single(r.ConditionWarnings);
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
