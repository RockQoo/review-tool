using System.Diagnostics;

namespace ReviewTool.Cpp;

// clang 이 새 Visual Studio(18 등)를 스스로 못 찾으면 <cstddef> 부터 깨져서 호출 해석이 전부 무너진다.
// 그래서 vswhere 로 MSVC 와 Windows SDK 의 include 경로를 찾아 직접 넘긴다.
public sealed record MsvcToolchain(string ToolsVersion, IReadOnlyList<string> SystemIncludes)
{
    // 14.51 -> 19.51 (_MSC_VER 흉내)
    public string CompatibilityVersion => ToolsVersion.Split('.') is [_, var minor, ..] ? $"19.{minor}" : "19.40";

    public static MsvcToolchain? Detect(List<string> diagnostics)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                   @"Microsoft Visual Studio\Installer\vswhere.exe");
        if (!File.Exists(vswhere))
        {
            diagnostics.Add("vswhere 가 없어 MSVC 헤더 경로를 찾지 못했습니다(표준 라이브러리 해석이 깨질 수 있음).");
            return null;
        }

        var install = Run(vswhere, "-latest", "-prerelease", "-products", "*",
                          "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath");
        if (string.IsNullOrWhiteSpace(install))
        {
            diagnostics.Add("C++ 도구가 설치된 Visual Studio 를 찾지 못했습니다.");
            return null;
        }

        var msvcRoot = Path.Combine(install, @"VC\Tools\MSVC");
        var defaultFile = Path.Combine(install, @"VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt");
        var version = File.Exists(defaultFile) ? File.ReadAllText(defaultFile).Trim() : null;
        if (version is null || !Directory.Exists(Path.Combine(msvcRoot, version)))
        {
            version = Directory.EnumerateDirectories(msvcRoot).Select(Path.GetFileName).OrderByDescending(v => Version.TryParse(v, out var x) ? x : new Version()).FirstOrDefault();
        }
        if (version is null)
        {
            diagnostics.Add($"MSVC 도구 폴더가 비어 있음: {msvcRoot}");
            return null;
        }

        var includes = new List<string> { Path.Combine(msvcRoot, version!, "include") };

        var kits = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Windows Kits\10\Include");
        if (Directory.Exists(kits))
        {
            var sdk = Directory.EnumerateDirectories(kits)
                .Where(d => Directory.Exists(Path.Combine(d, "ucrt")))
                .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version())
                .FirstOrDefault();
            if (sdk is not null)
            {
                includes.AddRange(new[] { "ucrt", "um", "shared", "winrt", "cppwinrt" }
                    .Select(s => Path.Combine(sdk, s)).Where(Directory.Exists));
            }
        }
        else
        {
            diagnostics.Add("Windows SDK 를 찾지 못했습니다.");
        }
        return new MsvcToolchain(version!, includes);
    }

    private static string Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
    }
}
