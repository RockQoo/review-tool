using System.Text.RegularExpressions;
using ReviewTool.Core;

namespace ReviewTool.Cpp;

// 폴더 하나를 보고 분석 설정(TargetConfig)을 제안한다. 사람이 json 을 손으로 쓰지 않아도 되게 하는 것이 목적이고,
// 결과는 화면에서 고친 뒤 저장한다.
public static partial class CppTargetDetector
{
    // 게임 서버에서 흔한 패킷 핸들러 등록 모양. 앞의 것이 더 구체적이다.
    public static readonly EntryPreset[] Presets =
    [
        new("멤버 함수 등록", @"Register\(\s*(?:this\s*,\s*)?&(?<class>\w+)::(?<method>\w+)\s*\)", [],
            "dispatcher.Register(this, &Class::HandleX) / Register(&Class::HandleX)"),
        new("핸들러 표 대입", @"\[\s*[\w:]+\s*\]\s*=\s*&(?<class>\w+)::(?<method>\w+)", [],
            "handlers[PacketId::X] = &Class::HandleX"),
        new("OnPacket 구현", @"^void[ \t]+(?<class>\w+)::(?<method>OnPacket\w*)[ \t]*\(", ["**/*.cpp"],
            "void XxxHandler::OnPacket(...) 처럼 수신 함수를 직접 구현한 곳"),
        // 들여쓰기 없는 줄(= 함수 정의)만. 본문 안의 Foo::HandleX( 호출은 들여쓰기가 있어 걸리지 않는다.
        new("Handle 로 시작하는 함수", @"^(?:[\w:<>,\*&]+[ \t]+)*(?<class>\w+)::(?<method>Handle\w+)[ \t]*\(", ["**/*.cpp"],
            "등록 코드를 못 찾을 때 쓰는 넓은 규칙: .cpp 의 Class::HandleXxx 정의 전부"),
        // 시작 흐름(main → Init) 용. 다른 규칙과 상관없이 찾으면 켠다.
        new("시작 함수", @"^(?:int|auto)[ \t]+(?<method>w?main)[ \t]*\(", ["**/*.cpp"],
            "int main(...) — 설정 로딩·스레드·핸들러 등록·리스너 순서를 흐름 요약으로 본다"),
    ];

    private static readonly string[] ThirdPartyNames =
        ["3rd", "3rdparty", "third_party", "thirdparty", "external", "externals", "vendor", "deps", "extern"];

    public static TargetSuggestion Suggest(string folder, string? name = null)
    {
        var root = Path.GetFullPath(folder).TrimEnd('\\', '/');
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"폴더가 없음: {root}");
        }
        var notes = new List<string>();
        var config = new TargetConfig
        {
            Name = SanitizeName(name ?? Path.GetFileName(root)),
            Root = root,
            Language = "cpp",
            HideCallees = [@"^Log::", @"^spdlog::", @"^fmt::"],
        };

        // 1) 컴파일 옵션: vcxproj > compile_flags.txt > 추측
        var projects = SourceFiles.Enumerate(root, CompileDatabase.IsVcxproj)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Where(p => !IsThirdParty(p))
            .ToList();
        if (projects.Count > 0)
        {
            config.Cpp.CompileFrom = "vcxproj";
            // vcxproj 들이 있는 최상위 폴더를 분석 범위로. 루트에 바로 있으면 전체.
            var tops = projects.Select(p => p.Contains('/') ? p[..p.IndexOf('/')] : "").Distinct().ToList();
            if (!tops.Contains(""))
            {
                config.Include = tops.Select(t => t + "/**").ToList();
            }
            notes.Add($"Visual Studio 프로젝트 {projects.Count}개에서 include 경로와 define 을 읽습니다.");
        }
        else
        {
            config.Cpp.CompileFrom = "flags";
            var flagsFile = Path.Combine(root, "compile_flags.txt");
            if (File.Exists(flagsFile))
            {
                ReadCompileFlags(flagsFile, config);
                notes.Add("compile_flags.txt 의 옵션을 씁니다.");
            }
            else
            {
                config.Cpp.ExtraIncludeDirs = GuessIncludeDirs(root);
                notes.Add("프로젝트 파일이 없어 include 경로를 추측했습니다(헤더가 있는 src/include 폴더). 해석이 틀리면 cpp.extraIncludeDirs 를 고치세요.");
            }
        }

        // 2) 서드파티 폴더는 범위에서 뺀다(범위 밖 함수는 "외부"로 보이고 들어가지 않는다)
        foreach (var dir in SourceFiles.Directories(root, maxDepth: 3))
        {
            var rel = Path.GetRelativePath(root, dir).Replace('\\', '/');
            if (ThirdPartyNames.Contains(Path.GetFileName(dir).ToLowerInvariant()) &&
                !config.Exclude.Any(e => rel.StartsWith(e.TrimEnd('*', '/'), StringComparison.OrdinalIgnoreCase)))
            {
                config.Exclude.Add(rel + "/**");
            }
        }

        var filter = GlobMatcher.Build(config.Include, config.Exclude);
        var sourceCount = CompileDatabase.EnumerateSources(root, filter, config.Exclude).Count();
        if (sourceCount == 0)
        {
            notes.Add("분석 범위에 .cpp 가 없습니다. include/exclude 를 확인하세요.");
        }

        // 3) 진입점 규칙: 실제로 걸리는 것을 센다
        // 규칙 네 개를 한 번의 파일 순회로 센다
        var rules = Presets.Select(p => p.ToRule()).ToList();
        var probe = new TargetConfig { Root = root, Exclude = config.Exclude, EntryPoints = rules };
        var all = CppTraceAnalyzer.FindEntryPoints(probe, filter);
        var detected = new List<DetectedPreset>();
        for (var i = 0; i < Presets.Length; i++)
        {
            var found = all.Where(f => ReferenceEquals(f.Rule, rules[i])).ToList();
            var samples = found.Select(f => f.ClassName is null ? f.Method : $"{f.ClassName}::{f.Method}")
                .Distinct().Take(4).ToList();
            detected.Add(new DetectedPreset(Presets[i], found.Select(f => (f.ClassName, f.Method)).Distinct().Count(), samples));
        }
        var specific = detected.Take(3).Where(d => d.Matches > 0).ToList();
        var chosen = specific.Count > 0 ? specific : detected.Skip(3).Take(1).Where(d => d.Matches > 0).ToList();
        chosen.AddRange(detected.Skip(4).Where(d => d.Matches > 0));
        foreach (var d in detected)
        {
            d.Enabled = chosen.Contains(d);
        }
        config.EntryPoints = chosen.Select(d => d.Preset.ToRule()).ToList();
        if (chosen.Count == 0)
        {
            notes.Add("패킷 핸들러 모양을 찾지 못했습니다. targets/<이름>.json 의 entryPoints 에 정규식을 직접 적어야 합니다.");
        }

        return new TargetSuggestion(config, detected, notes, sourceCount);
    }

    public static string SanitizeName(string name)
    {
        var s = InvalidName().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return s.Length == 0 ? "target" : s;
    }

    private static bool IsThirdParty(string relativePath) =>
        relativePath.Split('/').Any(p => ThirdPartyNames.Contains(p.ToLowerInvariant()));

    private static void ReadCompileFlags(string path, TargetConfig config)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith("-I", StringComparison.Ordinal) && line.Length > 2)
            {
                config.Cpp.ExtraIncludeDirs.Add(line[2..]);
            }
            else if (line.StartsWith("-D", StringComparison.Ordinal) && line.Length > 2)
            {
                config.Cpp.ExtraDefines.Add(line[2..]);
            }
            else if (line.StartsWith("-std=", StringComparison.Ordinal))
            {
                config.Cpp.Standard = line[5..];
            }
        }
    }

    private static List<string> GuessIncludeDirs(string root)
    {
        var dirs = new List<string> { "." };
        foreach (var dir in SourceFiles.Directories(root, maxDepth: 6))
        {
            var rel = Path.GetRelativePath(root, dir).Replace('\\', '/');
            var leaf = Path.GetFileName(dir).ToLowerInvariant();
            if (IsThirdParty(rel) || leaf is not ("src" or "include" or "source"))
            {
                continue;
            }
            if (Directory.EnumerateFiles(dir, "*.h*", SearchOption.TopDirectoryOnly).Any())
            {
                dirs.Add(rel);
            }
        }
        return dirs;
    }

    [GeneratedRegex(@"[^a-z0-9_.-]+")]
    private static partial Regex InvalidName();
}

public sealed record EntryPreset(string Name, string Pattern, List<string> Files, string Description)
{
    public EntryPointRule ToRule() => new() { Name = Name, Pattern = Pattern, Files = [.. Files] };
}

public sealed class DetectedPreset(EntryPreset preset, int matches, List<string> samples)
{
    public EntryPreset Preset { get; } = preset;
    public int Matches { get; } = matches;
    public List<string> Samples { get; } = samples;
    public bool Enabled { get; set; }
}

public sealed record TargetSuggestion(TargetConfig Config, List<DetectedPreset> Presets, List<string> Notes, int SourceCount);
