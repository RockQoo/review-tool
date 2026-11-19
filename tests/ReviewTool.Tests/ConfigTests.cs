using ReviewTool.Core;
using ReviewTool.Cpp;

namespace ReviewTool.Tests;

public class ConfigTests
{
    [Theory]
    [InlineData("Server/**", "Server/Zone/A.cpp", true)]
    [InlineData("Server/**", "3rd/asio/a.hpp", false)]
    [InlineData("**/*.cpp", "a.cpp", true)]
    [InlineData("**/*.cpp", "x/y/a.cpp", true)]
    [InlineData("**/*.cpp", "x/y/a.h", false)]
    [InlineData("Src/*.h", "Src/a/b.h", false)]
    public void Glob(string glob, string path, bool expected) =>
        Assert.Equal(expected, GlobMatcher.ToRegex(glob).IsMatch(path));

    [Fact]
    public void 제외가_포함보다_우선() =>
        Assert.False(GlobMatcher.Build(["**"], ["**/Test/**"])("Server/Test/a.cpp"));

    [Fact]
    public void 주석_속_매치는_진입점이_아님()
    {
        const string text = "void f()\n{\n    // Register(&A::B)\n    Register(&A::C);\n}";
        Assert.True(CppTraceAnalyzer.IsInComment(text, text.IndexOf("Register(&A::B", StringComparison.Ordinal)));
        Assert.False(CppTraceAnalyzer.IsInComment(text, text.IndexOf("Register(&A::C", StringComparison.Ordinal)));
    }

    // 정의를 이름으로 찾을 때 범위 비교. 빈 범위가 아무것과나 맞으면 다른 클래스의 같은 이름 함수로 간다.
    [Theory]
    [InlineData("Zone", "Zone", true)]
    [InlineData("Game::Zone", "Zone", true)]
    [InlineData("Zone", "Game::Zone", true)]
    [InlineData("Zone", "", false)]
    [InlineData("", "Zone", false)]
    [InlineData("", "", true)]
    [InlineData("MyZone", "Zone", false)]
    public void 범위_비교(string expected, string actual, bool same) =>
        Assert.Equal(same, CallTreeWalker.SameScope(expected, actual));

    [Fact]
    public void 줄과_열은_LSP_기준()
    {
        Assert.Equal((2, 3), CppTraceAnalyzer.ToLineColumn("ab\ncdef", 6));
    }

    [Fact]
    public void Vcxproj에서_include와_매크로를_읽음()
    {
        var root = Directory.CreateTempSubdirectory("rt-vcx").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Directory.Build.props"), """
                <Project>
                  <PropertyGroup>
                    <RepoRoot>$([MSBuild]::NormalizeDirectory('$(MSBuildThisFileDirectory)'))</RepoRoot>
                  </PropertyGroup>
                </Project>
                """);
            var proj = Directory.CreateDirectory(Path.Combine(root, "Server", "Game")).FullName;
            Directory.CreateDirectory(Path.Combine(proj, "Src"));
            File.WriteAllText(Path.Combine(proj, "Src", "main.cpp"), "int main() {}");
            File.WriteAllText(Path.Combine(proj, "Game.vcxproj"), """
                <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <ItemDefinitionGroup Condition="'$(Configuration)|$(Platform)'=='Release|x64'">
                    <ClCompile><PreprocessorDefinitions>NDEBUG;%(PreprocessorDefinitions)</PreprocessorDefinitions></ClCompile>
                  </ItemDefinitionGroup>
                  <ItemDefinitionGroup Condition="'$(Configuration)|$(Platform)'=='Debug|x64'">
                    <ClCompile>
                      <AdditionalIncludeDirectories>$(ProjectDir)Src;$(RepoRoot)3rd\inc;%(AdditionalIncludeDirectories)</AdditionalIncludeDirectories>
                      <PreprocessorDefinitions>_DEBUG;GAME;%(PreprocessorDefinitions)</PreprocessorDefinitions>
                      <LanguageStandard>stdcpp20</LanguageStandard>
                    </ClCompile>
                  </ItemDefinitionGroup>
                  <ItemGroup><ClCompile Include="Src\main.cpp" /></ItemGroup>
                </Project>
                """);

            var config = new TargetConfig { Name = "t", Root = root, Cpp = new CppOptions { DetectMsvc = false } };
            var diagnostics = new List<string>();
            var entries = CompileDatabase.Build(config, _ => true, diagnostics);

            var entry = Assert.Single(entries);
            Assert.Empty(diagnostics);
            Assert.Contains("-std=c++20", entry.Arguments);
            Assert.Contains("-D_DEBUG", entry.Arguments);
            Assert.DoesNotContain("-DNDEBUG", entry.Arguments);
            Assert.Contains("-I" + Path.Combine(proj, "Src"), entry.Arguments);
            Assert.Contains("-I" + Path.Combine(root, "3rd", "inc"), entry.Arguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 결과_저장과_불러오기()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rt-{Guid.NewGuid():N}.json");
        try
        {
            var graph = new TraceGraph { TargetName = "t" };
            graph.Nodes["a"] = new TraceNode { Id = "a", Name = "A::Run", Stop = StopReason.Hidden };
            TraceStore.Save(graph, path);
            var loaded = TraceStore.TryLoad(path);
            Assert.NotNull(loaded);
            Assert.Equal(StopReason.Hidden, loaded.Nodes["a"].Stop);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
