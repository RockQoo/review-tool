using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ReviewTool.Core;

namespace ReviewTool.Cpp;

// clangd 가 각 .cpp 를 어떤 옵션으로 해석할지 알려 주는 compile_commands.json 을 만든다.
// 원본 저장소에 쓰지 않으려고 이 도구의 작업 폴더에 만들고 clangd 에 --compile-commands-dir 로 넘긴다.
public sealed record CompileEntry(string Directory, string File, IReadOnlyList<string> Arguments);

public static partial class CompileDatabase
{
    public static List<CompileEntry> Build(TargetConfig config, Func<string, bool> sourceFilter, List<string> diagnostics)
    {
        var entries = config.Cpp.CompileFrom.Equals("vcxproj", StringComparison.OrdinalIgnoreCase)
            ? FromVcxproj(config, sourceFilter, diagnostics)
            : FromFlags(config, sourceFilter);

        var system = SystemArguments(config, diagnostics);
        if (system.Count == 0)
        {
            return entries;
        }
        // "clang++ -xc++ -std=..." 바로 뒤에 시스템 헤더 옵션을 끼운다
        return entries.Select(e => e with { Arguments = [.. e.Arguments.Take(3), .. system, .. e.Arguments.Skip(3)] }).ToList();
    }

    private static List<string> SystemArguments(TargetConfig config, List<string> diagnostics)
    {
        var args = new List<string>();
        if (config.Cpp.SystemIncludeDirs.Count > 0)
        {
            args.AddRange(config.Cpp.SystemIncludeDirs.Select(d => "-isystem" + d));
            return args;
        }
        if (!config.Cpp.DetectMsvc)
        {
            return args;
        }
        var msvc = MsvcToolchain.Detect(diagnostics);
        if (msvc is null)
        {
            return args;
        }
        // 자동 탐지를 끄고(-nostdinc 류 대신 MSVC 경로만 앞에 둔다) 버전을 맞춘다
        args.Add("--target=x86_64-pc-windows-msvc");
        args.Add($"-fms-compatibility-version={msvc.CompatibilityVersion}");
        args.AddRange(msvc.SystemIncludes.Select(d => "-isystem" + d));
        return args;
    }

    public static void Write(IEnumerable<CompileEntry> entries, string directory)
    {
        var array = new JsonArray();
        foreach (var e in entries)
        {
            var args = new JsonArray();
            foreach (var a in e.Arguments)
            {
                args.Add(a);
            }
            array.Add(new JsonObject
            {
                ["directory"] = e.Directory,
                ["file"] = e.File,
                ["arguments"] = args,
            });
        }
        File.WriteAllText(Path.Combine(directory, "compile_commands.json"),
                          array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static List<CompileEntry> FromFlags(TargetConfig config, Func<string, bool> sourceFilter)
    {
        var result = new List<CompileEntry>();
        foreach (var file in EnumerateSources(config.Root, sourceFilter, config.Exclude))
        {
            result.Add(new CompileEntry(config.Root, file,
                MakeArguments(config, config.Cpp.Standard, config.Cpp.ExtraIncludeDirs, config.Cpp.ExtraDefines, file)));
        }
        return result;
    }

    private static List<CompileEntry> FromVcxproj(TargetConfig config, Func<string, bool> sourceFilter, List<string> diagnostics)
    {
        var result = new List<CompileEntry>();
        var projects = SourceFiles.Enumerate(config.Root, IsVcxproj, config.Exclude).ToList();
        if (projects.Count == 0)
        {
            diagnostics.Add("vcxproj 를 찾지 못해 cpp.extraIncludeDirs/extraDefines 만으로 해석했습니다.");
            return FromFlags(config, sourceFilter);
        }

        foreach (var projectPath in projects)
        {
            var project = VcxprojInfo.Read(projectPath, config, diagnostics);
            var std = project.Standard ?? config.Cpp.Standard;
            var includes = project.IncludeDirs.Concat(config.Cpp.ExtraIncludeDirs.Select(d => Path.GetFullPath(d, config.Root))).ToList();
            var defines = project.Defines.Concat(config.Cpp.ExtraDefines).Distinct().ToList();
            foreach (var file in project.Sources)
            {
                var rel = Path.GetRelativePath(config.Root, file).Replace('\\', '/');
                if (!sourceFilter(rel))
                {
                    continue;
                }
                result.Add(new CompileEntry(Path.GetDirectoryName(projectPath)!, file,
                                            MakeArguments(config, std, includes, defines, file)));
            }
        }
        return result;
    }

    private static List<string> MakeArguments(TargetConfig config, string std, IEnumerable<string> includes,
                                              IEnumerable<string> defines, string file)
    {
        var args = new List<string> { "clang++", "-xc++", $"-std={std}" };
        foreach (var dir in includes)
        {
            args.Add("-I" + Path.GetFullPath(dir, config.Root));
        }
        foreach (var d in defines)
        {
            args.Add("-D" + d);
        }
        args.Add("-c");
        args.Add(file);
        return args;
    }

    public static IEnumerable<string> EnumerateSources(string root, Func<string, bool> sourceFilter,
                                                       IReadOnlyList<string>? exclude = null)
    {
        foreach (var file in SourceFiles.Enumerate(root, f => f.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase), exclude))
        {
            if (sourceFilter(Path.GetRelativePath(root, file).Replace('\\', '/')))
            {
                yield return file;
            }
        }
    }

    public static bool IsVcxproj(string path) => path.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase);

    // bin/obj/.vs 처럼 빌드 산출물 폴더는 설정과 무관하게 뺀다. 수 GB 가 될 수 있다.
    public static bool IsBuildOutput(string relativePath) => SourceFiles.IsBuildOutput(relativePath);

    internal sealed class VcxprojInfo
    {
        public List<string> Sources { get; } = [];
        public List<string> IncludeDirs { get; } = [];
        public List<string> Defines { get; } = [];
        public string? Standard { get; set; }

        public static VcxprojInfo Read(string projectPath, TargetConfig config, List<string> diagnostics)
        {
            var info = new VcxprojInfo();
            var doc = XDocument.Load(projectPath);
            XNamespace ns = doc.Root!.Name.Namespace;
            var projectDir = Path.GetDirectoryName(projectPath)! + Path.DirectorySeparatorChar;
            var macros = BuildMacros(projectPath, projectDir, config);

            foreach (var item in doc.Descendants(ns + "ClCompile").Where(e => e.Attribute("Include") is not null))
            {
                var include = item.Attribute("Include")!.Value;
                if (include.Contains('*'))
                {
                    continue;
                }
                var full = Path.GetFullPath(Expand(include, macros), projectDir);
                if (File.Exists(full))
                {
                    info.Sources.Add(full);
                }
            }

            // 구성이 여럿이면 Debug|x64 를 우선, 없으면 첫 번째.
            var groups = doc.Descendants(ns + "ItemDefinitionGroup").ToList();
            var group = groups.FirstOrDefault(g => (g.Attribute("Condition")?.Value ?? "").Contains("Debug|x64"))
                        ?? groups.FirstOrDefault();
            var cl = group?.Element(ns + "ClCompile");
            if (cl is not null)
            {
                foreach (var dir in Split(cl.Element(ns + "AdditionalIncludeDirectories")?.Value))
                {
                    var expanded = Expand(dir, macros);
                    if (expanded.Contains("$("))
                    {
                        diagnostics.Add($"{Path.GetFileName(projectPath)}: 풀지 못한 매크로라 include 에서 뺌: {dir}");
                        continue;
                    }
                    info.IncludeDirs.Add(Path.GetFullPath(expanded, projectDir));
                }
                info.Defines.AddRange(Split(cl.Element(ns + "PreprocessorDefinitions")?.Value));
                info.Standard = cl.Element(ns + "LanguageStandard")?.Value switch
                {
                    "stdcpp14" => "c++14",
                    "stdcpp17" => "c++17",
                    "stdcpp20" => "c++20",
                    "stdcpp23" => "c++23",
                    "stdcpplatest" => "c++2c",
                    _ => null,
                };
            }
            return info;
        }

        private static IEnumerable<string> Split(string? value) =>
            (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => !v.StartsWith("%(", StringComparison.Ordinal));

        private static Dictionary<string, string> BuildMacros(string projectPath, string projectDir, TargetConfig config)
        {
            var macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ProjectDir"] = projectDir,
                ["ProjectName"] = Path.GetFileNameWithoutExtension(projectPath),
                ["SolutionDir"] = config.Root + Path.DirectorySeparatorChar,
            };

            // Directory.Build.props 의 단순 속성(경로 계산)만 흉내 낸다. 위로 올라가며 가장 가까운 것 하나.
            for (var dir = new DirectoryInfo(projectDir); dir is not null; dir = dir.Parent)
            {
                var props = Path.Combine(dir.FullName, "Directory.Build.props");
                if (File.Exists(props))
                {
                    ReadPropsMacros(props, macros);
                    break;
                }
                if (string.Equals(dir.FullName.TrimEnd('\\', '/'), config.Root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            foreach (var (k, v) in config.Cpp.Macros)
            {
                macros[k] = Path.GetFullPath(v, config.Root);
            }
            return macros;
        }

        private static void ReadPropsMacros(string propsPath, Dictionary<string, string> macros)
        {
            var doc = XDocument.Load(propsPath);
            var propsDir = Path.GetDirectoryName(propsPath)! + Path.DirectorySeparatorChar;
            foreach (var group in doc.Root!.Elements().Where(e => e.Name.LocalName == "PropertyGroup"))
            {
                foreach (var prop in group.Elements())
                {
                    var value = prop.Value.Trim().Replace("$(MSBuildThisFileDirectory)", propsDir);
                    value = NormalizeDirectoryCall().Replace(value, m => m.Groups[1].Value);
                    if (!value.Contains("$(") && value.Length > 0)
                    {
                        macros.TryAdd(prop.Name.LocalName, value);
                    }
                }
            }
        }

        private static string Expand(string value, Dictionary<string, string> macros) =>
            MacroRef().Replace(value, m => macros.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    [GeneratedRegex(@"\$\(([A-Za-z_][A-Za-z0-9_]*)\)")]
    private static partial Regex MacroRef();

    [GeneratedRegex(@"\$\(\[MSBuild\]::NormalizeDirectory\('([^']*)'\)\)")]
    private static partial Regex NormalizeDirectoryCall();
}
