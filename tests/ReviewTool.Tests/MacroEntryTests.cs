using ReviewTool.Core;
using ReviewTool.Cpp;

namespace ReviewTool.Tests;

// gtest 의 TEST(Suite, Name) 처럼 매크로가 만드는 진입점, 진입점 트리 이름 나누기.
public class MacroEntryTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "gtest");

    [Theory]
    [InlineData("Core / ByteWriter", "Core", "ByteWriter")]
    [InlineData("Tests / A / B", "Tests", "A / B")]
    [InlineData("main", "main", "")]
    public void 묶음_이름을_폴더와_클래스로_나눈다(string group, string folder, string cls)
    {
        Assert.Equal(folder, EntryGroup.Folder(group));
        Assert.Equal(cls, EntryGroup.Class(group));
    }

    [Fact]
    public void 문자열과_주석_속_글자는_지운다()
    {
        const string line = "    Foo(\"Bar(1)\", 'x'); // Baz(2)";
        var code = CallTreeWalker.CodeOnly(line);

        Assert.Equal(line.Length, code.Length); // 열 위치는 그대로
        Assert.Contains("Foo(", code);
        Assert.DoesNotContain("Bar", code);
        Assert.DoesNotContain("Baz", code);
    }

    [Theory]
    [InlineData("const auto n = 1'000; Foo(n);", "Foo(")]
    [InlineData("auto x = 0xFF'FF; Bar();", "Bar(")]
    public void 숫자_구분자는_문자_리터럴로_보지_않는다(string line, string kept)
    {
        Assert.Contains(kept, CallTreeWalker.CodeOnly(line));
    }

    [Fact]
    public void 매크로_줄은_괄호_짝까지만_지운다()
    {
        var code = CallTreeWalker.BlankMacroHead("TEST(S, N) { Foo(); }");

        Assert.DoesNotContain("TEST", code);
        Assert.Contains("{ Foo(); }", code);
        Assert.Equal(21, code.Length);
    }

    [Fact]
    public void 본문_중괄호_밖은_지운다()
    {
        string[] lines = ["TEST(S, A) { Foo(); } void Helper() { Bar(); }", "int x = Baz();"];
        var body = CallTreeWalker.BodyLines(lines, 1, 2).ToList();

        var (line, code) = Assert.Single(body); // 본문이 첫 줄에서 끝나 둘째 줄은 보지 않는다
        Assert.Equal(1, line);
        Assert.Contains("Foo(", code);
        Assert.DoesNotContain("Helper", code);
        Assert.DoesNotContain("Bar", code);
    }

    [Fact]
    public void 자동_감지가_gtest_규칙을_매크로로_켠다()
    {
        var s = CppTargetDetector.Suggest(Fixture);

        var gtest = s.Presets.Single(p => p.Preset.Name == "gtest 테스트");
        Assert.Equal(6, gtest.Matches);
        Assert.True(gtest.Enabled);
        var rule = Assert.Single(s.Config.EntryPoints, r => r.Name == "gtest 테스트");
        Assert.True(rule.Macro);
        Assert.Contains("^testing::", s.Config.HideCallees);
    }

    [Fact]
    public void CMake_가_만든_vcxproj_는_범위로_잡지_않는다()
    {
        var root = Directory.CreateTempSubdirectory("rt-detect-cmake").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Core"));
            File.WriteAllText(Path.Combine(root, "CMakeLists.txt"), "project(x)\n");
            File.WriteAllText(Path.Combine(root, "Core", "a.cpp"), "int main()\n{\n    return 0;\n}\n");
            var vs = Directory.CreateDirectory(Path.Combine(root, "build", "vs")).FullName;
            File.WriteAllText(Path.Combine(vs, "Core.vcxproj"),
                """<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><ItemGroup><ClCompile Include="..\..\Core\a.cpp" /></ItemGroup></Project>""");
            File.WriteAllText(Path.Combine(vs, "CMakeCXXCompilerId.cpp"), "int main() { return 0; }\n");

            var s = CppTargetDetector.Suggest(root);

            Assert.Equal("flags", s.Config.Cpp.CompileFrom);
            Assert.DoesNotContain("build/**", s.Config.Include);
            Assert.Contains("build/**", s.Config.Exclude);
            Assert.Equal(1, s.SourceCount); // build 안의 컴파일러 확인용 .cpp 는 세지 않는다
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // 실제 clangd 로 분석한다. clangd 가 없는 PC 에서는 아무것도 하지 않고 통과한다.
    [Fact]
    public async Task 매크로_진입점은_제_본문의_호출만_잇는다()
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

        var data = Directory.CreateTempSubdirectory("rt-e2e-macro").FullName;
        try
        {
            var config = new TargetConfig
            {
                Name = "gtest",
                Root = Fixture,
                EntryPoints =
                [
                    new EntryPointRule
                    {
                        Name = "gtest 테스트",
                        Pattern = @"^TEST(?:_F|_P)?\(\s*(?<class>\w+)\s*,\s*(?<method>\w+)\s*\)",
                        Files = ["**/*.cpp"],
                        Macro = true,
                    },
                ],
                Cpp = new CppOptions
                {
                    Clangd = clangd,
                    CompileFrom = "flags",
                    Standard = "c++20",
                    ExtraIncludeDirs = ["Src", "Ext"],
                    DetectMsvc = false,
                    IndexTimeoutSeconds = 120,
                },
                Exclude = ["Ext/**"],
            };

            var graph = await new CppTraceAnalyzer().AnalyzeAsync(config, new WorkPaths(data, "gtest"), null, CancellationToken.None);

            Assert.Equal(["Adds", "Externals", "OneLine", "Sibling", "Streams", "Templates"], graph.EntryPoints.Select(e => e.Label).Order());
            // 진입점 파일이 한 폴더뿐이라 묶음 이름은 클래스(테스트 묶음)만 남는다
            Assert.All(graph.EntryPoints, e => Assert.Equal("Calc", e.Group));

            List<string> CalleesOf(string label)
            {
                var id = graph.EntryPoints.Single(e => e.Label == label).NodeId;
                return graph.Edges.Where(e => e.From == id).Select(e => graph.Nodes[e.To].Name).ToList();
            }

            // 같은 매크로로 만든 두 테스트의 호출이 섞이지 않는다. 매크로 인자 안의 호출(EXPECT_EQ(calc.Add(..)))도 잡고,
            // 주석·문자열 속 이름은 잡지 않는다.
            var adds = CalleesOf("Adds");
            Assert.Equal(["Calc::Add"], adds);

            // 연산자 호출(calc << 5)도 소속까지 풀린다
            var streams = CalleesOf("Streams");
            Assert.Contains("Calc::operator<<", streams);
            Assert.Contains("Twice", streams);
            Assert.DoesNotContain("Calc::Add", streams);

            // 한 줄로 쓴 본문(TEST(S, N) { Foo(); })도 호출을 잡는다
            Assert.Equal(["Twice"], CalleesOf("OneLine"));
            // 같은 줄에 이어지는 다른 함수(Helper)의 호출은 이 테스트 것이 아니다
            Assert.Equal(["Twice"], CalleesOf("Sibling"));

            // 중첩 템플릿 인자 호출(Count<Box<Box<int>>>(n))과 숫자 구분자(1'000) 뒤의 호출
            Assert.Equal(["Count"], CalleesOf("Templates"));

            // 범위 밖 같은 헤더의 동명 함수는 소속으로 갈린다
            var externals = CalleesOf("Externals");
            Assert.Contains("left::Send", externals);
            Assert.Contains("right::Send", externals);

            // 노드는 테스트 이름과 본문을 가리킨다
            var addsNode = graph.Nodes[graph.EntryPoints.Single(e => e.Label == "Adds").NodeId];
            Assert.Equal("Calc::Adds", addsNode.Name);
            Assert.Equal("Tests/CalcTest.cpp", addsNode.File);
            Assert.Contains("calc.Add(1, 2)", addsNode.Code);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }
}
