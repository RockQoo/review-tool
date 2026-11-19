using ReviewTool.Core;

namespace ReviewTool.CSharp;

// C# 폴더를 보고 분석 설정을 제안한다(C++ 감지기와 같은 모양의 TargetSuggestion).
public static class CSharpTargetDetector
{
    // 앞의 셋이 구체적인 등록 모양, 넷째는 넓은 규칙(앞의 것이 하나도 없을 때만), 다섯째·여섯째는 찾으면 함께 켠다.
    public static readonly EntryPreset[] Presets =
    [
        new("메서드 등록", @"\bRegister\w*\s*(?:<[^>()]*>)?\s*\(\s*(?:[\w.]+\s*,\s*)?(?:(?<class>[A-Z]\w*)\.|[a-z_]\w*\.)?(?<method>[A-Z]\w+)\s*\)", ["**/*.cs"],
            "dispatcher.Register(PacketId.X, HandleX) / Register<C2SLogin>(OnLogin) 처럼 메서드 이름을 넘기는 등록"),
        new("핸들러 특성", @"\[\s*(?:Packet)?Handler\b[^\]]*\]\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|static|async|override|virtual)\s+)*[\w<>\[\]?.,]+\s+(?<method>\w+)\s*\(", ["**/*.cs"],
            "[PacketHandler(...)] 처럼 특성을 단 메서드"),
        new("API 컨트롤러 액션", @"\[\s*Http(?:Get|Post|Put|Delete|Patch)\b[^\]]*\]\s*(?:\[[^\]]*\]\s*)*public\s+(?:async\s+)?[\w<>\[\]?.,]+\s+(?<method>\w+)\s*\(", ["**/*.cs"],
            "[HttpPost] public ... Action(...) — 웹 API 서버"),
        new("Handle 로 시작하는 메서드", @"^[ \t]*(?:(?:public|internal|private|protected|static|async|override|virtual)\s+)+[\w<>\[\]?.,]+\s+(?<method>Handle\w+)\s*\(", ["**/*.cs"],
            "등록 코드를 못 찾을 때 쓰는 넓은 규칙: HandleXxx 메서드 정의 전부"),
        new("시작 함수", @"\bstatic\s+(?:async\s+)?(?:void|int|Task(?:<int>)?)\s+(?<method>Main)\s*\(", ["**/*.cs"],
            "static void Main(...) — 시작 순서를 흐름 요약으로 본다(최상위 문(top-level statements)은 아직 못 잡음)"),
    ];

    // C# 패킷 모양(PacketId.X, case PacketId.X). C++ 기본값은 PacketId::X 라 C# 대상에는 이것을 쓴다.
    public static MessageChannel DefaultPacket() => new()
    {
        Name = "패킷",
        CrossProcess = true,
        Send = @"\bSend\w*\s*(?:<[^>()]*>)?\s*\(",
        Key = @"\b(?:PacketId|PacketType|Opcode|MsgId|ProtocolId)\.(?<key>\w+)|\bnew\s+(?<key>[A-Z][A-Za-z0-9]?2[A-Z]\w+)\b",
        KeyName = @"^[A-Z][A-Za-z0-9]?2[A-Z]\w+$",
        Receive = @"\bcase\s+(?:\w+\.)*(?:PacketId|PacketType|Opcode|MsgId|ProtocolId)\.(?<key>\w+)",
    };

    // C++ 소스가 없고 .csproj 나 .cs 가 있으면 C# 으로 본다
    public static bool LooksLikeCSharp(string folder)
    {
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root))
        {
            return false;
        }
        var cs = false;
        foreach (var f in SourceFiles.Enumerate(root, _ => true, maxDepth: 6).Take(20000))
        {
            if (SourceFiles.IsCode(f) || f.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            cs |= f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || CSharpTraceAnalyzer.IsSource(f);
        }
        return cs;
    }

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
            Name = TargetConfig.SanitizeName(name ?? Path.GetFileName(root)),
            Root = root,
            Language = "csharp",
            HideCallees = [@"(^|::)ILogger\w*::", @"(^|::)Log\w*$", @"^Microsoft::Extensions::Logging::"],
            Channels = [DefaultPacket()],
            Exclude = ["**/bin/**", "**/obj/**", "**/*.Designer.cs"],
        };
        var tests = SourceFiles.Directories(root, maxDepth: 3)
            .Select(d => Path.GetRelativePath(root, d).Replace('\\', '/'))
            .Where(d => d.Split('/')[^1].EndsWith("Tests", StringComparison.OrdinalIgnoreCase) || d.Split('/')[^1] is "tests" or "test")
            .ToList();
        foreach (var t in tests)
        {
            config.Exclude.Add(t + "/**");
        }
        if (tests.Count > 0)
        {
            notes.Add($"테스트 폴더 {tests.Count}개는 범위에서 뺍니다: {string.Join(", ", tests.Take(4))}");
        }
        notes.Add("C# 은 Roslyn 으로 .cs 파일만 해석합니다(빌드·NuGet 복원 없음). 패키지 안의 형식은 \"외부\"로 보입니다.");

        var filter = GlobMatcher.Build(config.Include, config.Exclude);
        var files = SourceFiles.Enumerate(root, CSharpTraceAnalyzer.IsSource, config.Exclude)
            .Where(f => filter(Path.GetRelativePath(root, f).Replace('\\', '/')))
            .ToList();
        if (files.Count == 0)
        {
            notes.Add("분석 범위에 .cs 가 없습니다. include/exclude 를 확인하세요.");
        }

        var rules = Presets.Select(p => p.ToRule()).ToList();
        var probe = new TargetConfig { Root = root, EntryPoints = rules, Channels = [] };
        var all = CSharpTraceAnalyzer.FindEntryPoints(probe, files);
        var detected = new List<DetectedPreset>();
        for (var i = 0; i < Presets.Length; i++)
        {
            var found = all.Where(f => ReferenceEquals(f.Rule, rules[i])).ToList();
            var samples = found.Select(f => f.ClassName is null ? f.Method : $"{f.ClassName}.{f.Method}").Distinct().Take(4).ToList();
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
            notes.Add("진입점 모양을 찾지 못했습니다. targets/<이름>.json 의 entryPoints 에 정규식을 직접 적어야 합니다(`(?<method>..)` 필수).");
        }
        return new TargetSuggestion(config, detected, notes, files.Count);
    }
}
