using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReviewTool.Core;

// 분석 대상 하나의 설정. targets/<이름>.json 으로 둔다.
// 원본 저장소에는 아무것도 쓰지 않는 것이 전제라 설정도 이 도구 쪽에 둔다.
public sealed class TargetConfig
{
    public string Name { get; set; } = "";
    public string Root { get; set; } = "";
    public string Language { get; set; } = "cpp";

    // 분석할 소스(루트 기준 glob). 비우면 전부.
    public List<string> Include { get; set; } = [];
    public List<string> Exclude { get; set; } = [];

    public List<EntryPointRule> EntryPoints { get; set; } = [];

    // 이 이름(정규식)에 걸리는 함수는 노드로는 보이되 그 아래로 들어가지 않는다. 로그 같은 잡음용.
    public List<string> HideCallees { get; set; } = [];

    public int MaxDepth { get; set; } = 8;
    public int MaxNodes { get; set; } = 3000;
    public int MaxCodeLines { get; set; } = 200;

    // C++ 전용 설정
    public CppOptions Cpp { get; set; } = new();

    [JsonIgnore]
    public string? SourcePath { get; private set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        // 사람이 직접 고치는 파일이라 한글·정규식 기호를 \uXXXX 로 바꾸지 않는다(웹에 내보내지 않는 로컬 파일).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    // targets/<이름>.json 으로 저장. 같은 이름이 있으면 overwrite 가 false 일 때 실패한다.
    public string Save(string targetsDirectory, bool overwrite = false)
    {
        Directory.CreateDirectory(targetsDirectory);
        var path = Path.Combine(targetsDirectory, Name + ".json");
        if (File.Exists(path) && !overwrite)
        {
            throw new IOException($"이미 있는 대상 이름입니다: {Name}");
        }
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        SourcePath = path;
        return path;
    }

    public static TargetConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<TargetConfig>(File.ReadAllText(path), JsonOptions)
                     ?? throw new InvalidDataException($"설정을 읽지 못함: {path}");
        config.SourcePath = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(config.Name))
        {
            config.Name = Path.GetFileNameWithoutExtension(path);
        }
        if (!Directory.Exists(config.Root))
        {
            throw new DirectoryNotFoundException($"분석 대상 폴더가 없음: {config.Root}");
        }
        config.Root = Path.GetFullPath(config.Root);
        return config;
    }
}

// 진입점 찾는 규칙 하나. 소스 텍스트에 정규식을 돌려 찾는다.
// 이름 있는 그룹: method(필수) — 진입점 함수 이름이 있는 자리, class(선택), label(선택).
public sealed class EntryPointRule
{
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";
    // 파일 glob. 비우면 Include 전체.
    public List<string> Files { get; set; } = [];
}

public sealed class CppOptions
{
    public string Clangd { get; set; } = "";
    // 컴파일 옵션을 어디서 얻나: "vcxproj"(프로젝트 파일 읽기) 또는 "flags"(아래 값만 사용).
    public string CompileFrom { get; set; } = "vcxproj";
    public string Standard { get; set; } = "c++23";
    public List<string> ExtraIncludeDirs { get; set; } = [];
    public List<string> ExtraDefines { get; set; } = [];
    // vcxproj 의 $(이름) 매크로 값. RepoRoot 처럼 Directory.Build.props 에서 오는 것.
    public Dictionary<string, string> Macros { get; set; } = [];
    // 표준 라이브러리 헤더 경로. 비우면 Windows 에서는 Visual Studio 를 찾아 자동으로 채운다.
    public List<string> SystemIncludeDirs { get; set; } = [];
    public bool DetectMsvc { get; set; } = true;
    public int IndexTimeoutSeconds { get; set; } = 600;
}
