using ReviewTool.Core;
using ReviewTool.Cpp;

namespace ReviewTool.Tests;

public class TargetDetectorTests
{
    private static string Mini => Path.Combine(AppContext.BaseDirectory, "Fixtures", "mini");

    [Fact]
    public void 프로젝트_파일이_없으면_include_를_추측하고_등록_규칙을_고른다()
    {
        var s = CppTargetDetector.Suggest(Mini);

        Assert.Equal("mini", s.Config.Name);
        Assert.Equal("flags", s.Config.Cpp.CompileFrom);
        Assert.Contains("Src", s.Config.Cpp.ExtraIncludeDirs);
        Assert.Equal(2, s.SourceCount);

        var register = s.Presets.Single(p => p.Preset.Name == "멤버 함수 등록");
        Assert.Equal(1, register.Matches);   // 주석 속 Register(&Handler::Init) 은 세지 않음
        Assert.True(register.Enabled);
        Assert.Contains("Handler::HandleMove", register.Samples);

        // 구체적인 규칙이 맞으면 넓은 Handle* 규칙은 켜지 않는다
        Assert.False(s.Presets.Single(p => p.Preset.Name.StartsWith("Handle", StringComparison.Ordinal)).Enabled);
        Assert.Single(s.Config.EntryPoints);
    }

    [Fact]
    public void Vcxproj_가_있는_최상위_폴더를_범위로_잡고_서드파티는_뺀다()
    {
        var root = Directory.CreateTempSubdirectory("rt-detect").FullName;
        try
        {
            var src = Directory.CreateDirectory(Path.Combine(root, "Server", "Game", "Src")).FullName;
            File.WriteAllText(Path.Combine(root, "Server", "Game", "Game.vcxproj"),
                """<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><ItemGroup><ClCompile Include="Src\a.cpp" /></ItemGroup></Project>""");
            File.WriteAllText(Path.Combine(src, "a.cpp"), "void A::Init()\n{\n    table[Id::X] = &A::HandleX;\n}\n");
            Directory.CreateDirectory(Path.Combine(root, "3rd", "lib"));
            File.WriteAllText(Path.Combine(root, "3rd", "lib", "x.vcxproj"), "<Project />");

            var s = CppTargetDetector.Suggest(root, "My Server!");

            Assert.Equal("my-server", s.Config.Name);
            Assert.Equal("vcxproj", s.Config.Cpp.CompileFrom);
            Assert.Equal(["Server/**"], s.Config.Include);
            Assert.Contains("3rd/**", s.Config.Exclude);
            var table = s.Presets.Single(p => p.Preset.Name == "핸들러 표 대입");
            Assert.True(table.Enabled);
            Assert.Equal(["A::HandleX"], table.Samples);

            // 저장 → 같은 이름은 덮어쓰기 거부
            var targets = Path.Combine(root, "targets-out");
            var path = s.Config.Save(targets);
            Assert.Equal("my-server", TargetConfig.Load(path).Name);
            Assert.Throws<IOException>(() => s.Config.Save(targets));
            // 사람이 고치는 파일이라 한글이 \uXXXX 로 바뀌면 안 된다
            var json = File.ReadAllText(path);
            Assert.Contains("핸들러 표 대입", json);
            Assert.DoesNotContain("\\u", json);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
