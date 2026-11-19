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

    // 디스패처 호출로 보는 함수 이름(정규식). 이 호출의 받는 쪽(X.Dispatch 의 X)과 같은 파일에서
    // X.Register(..&C::H) 한 핸들러로 선을 잇는다. 비우면 연결하지 않는다.
    public string DispatchCall { get; set; } = @"::Dispatch$";

    // 메시지 통로: 보내는 줄에서 이름(key)을 뽑고, 같은 이름을 받는 곳과 잇는다(패킷 여정·레인 연결).
    // 기본값은 흔한 패킷 모양 하나. 레인 메시지처럼 대상마다 다른 것은 여기에 더한다.
    public List<MessageChannel> Channels { get; set; } = [MessageChannel.DefaultPacket()];

    // 함수 이름 규칙으로 붙이는 표시(흐름 요약: 시작 순서, DB·캐시 접근 순서). 이름만 보는 추정이다.
    public List<TagRule> Tags { get; set; } = TagRule.Defaults();

    // 점검 규칙(4단계). 분석 결과를 바꾸지 않으므로 고쳐도 재분석할 필요가 없다(ContentHash 에서 뺀다).
    public List<CheckRule> Checks { get; set; } = CheckRule.Defaults();

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

    // 분석 결과에 영향을 주는 설정의 지문(파일 서식·주석과 무관).
    // 점검 규칙(checks)은 분석에 쓰지 않으므로 넣지 않는다(규칙만 고쳤는데 "설정 바뀜"으로 보이지 않게).
    public string ContentHash()
    {
        var node = JsonSerializer.SerializeToNode(this, JsonOptions)!.AsObject();
        node.Remove("checks");
        return Freshness.HashText(node.ToJsonString(JsonOptions));
    }

    // 대상 이름 = 파일 이름(targets/<이름>.json)이라 소문자·숫자·_.- 만 남긴다
    public static string SanitizeName(string name)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9_.-]+", "-").Trim('-');
        return s.Length == 0 ? "target" : s;
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

// 메시지 통로 하나. 정규식은 모두 소스 텍스트에 돈다.
public sealed class MessageChannel
{
    public string Name { get; set; } = "";
    // true: 다른 프로세스로 가는 패킷. false: 같은 프로세스 안 메시지(보낸 곳 → 핸들러를 호출 선으로 잇는다).
    public bool CrossProcess { get; set; } = true;
    // 보내는 호출(이 정규식이 걸린 문장에서 이름을 찾는다).
    public string Send { get; set; } = "";
    // 문장에서 이름을 뽑는 정규식. 이름 있는 그룹 key.
    public string Key { get; set; } = "";
    // 이름으로 인정하는 모양. 비우면 Key 가 뽑은 것을 다 인정한다.
    // 문장에 이름이 없으면 보낸 변수의 선언 타입이 이 모양인지 본다("변수 타입" 추정).
    // 진입점 표시 이름이 이 모양이면 그 진입점을 이 이름의 받는 곳으로 본다.
    public string KeyName { get; set; } = "";
    // 함수 본문 안에서 받는 곳(switch 의 case 등). 이름 있는 그룹 key.
    public string Receive { get; set; } = "";
    // 핸들러 등록: 그룹 key, method(필수), class(선택). 걸린 핸들러는 진입점이 된다.
    public string Register { get; set; } = "";

    public static MessageChannel DefaultPacket() => new()
    {
        Name = "패킷",
        CrossProcess = true,
        Send = @"\bSend\w*\s*\(",
        Key = @"\b(?:PacketId|PacketType|Opcode|MsgId|ProtocolId)::(?<key>\w+)|\b(?<key>\w+)::k?PacketId\b",
        KeyName = @"^[A-Z][A-Za-z0-9]?2[A-Z]\w+$",
        Receive = @"\bcase\s+(?:\w+::)*(?:PacketId|PacketType|Opcode|MsgId|ProtocolId)::(?<key>\w+)",
    };
}

public sealed class TagRule
{
    public string Name { get; set; } = "";
    // 함수의 한정 이름(Ns::Class::Method)에 거는 정규식
    public string Pattern { get; set; } = "";
    // (선택) 함수 본문 줄에 거는 정규식. 호출로 잡히지 않는 것(SP 이름 문자열, 집합 초기화 DbCommand{...})용.
    public string Code { get; set; } = "";

    public static List<TagRule> Defaults() =>
    [
        new() { Name = "설정", Pattern = @"(?i)config|setting" },
        new() { Name = "스레드", Pattern = @"(?i)thread|strand|lane|::run$|::start$|::join$" },
        new() { Name = "등록", Pattern = @"(?i)::regist(er)?\w*$|::bind$|::add(handler|listener)\w*$" },
        new() { Name = "네트워크", Pattern = @"(?i)listen|accept|connector|::connect\w*$" },
        new() { Name = "DB", Pattern = @"(?i)(^|::|_)db|database|sql|query|storedproc",
                Code = @"(?i)""[\w.\[\]]*?\b(usp|sp)_\w+|\bDbCommand\b" },
        new() { Name = "캐시", Pattern = @"(?i)cache|redis" },
        new() { Name = "작업 단위", Pattern = @"(?i)unitofwork|transaction|::commit$|::rollback$" },
    ];
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
