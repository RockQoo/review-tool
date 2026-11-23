namespace ReviewTool.Core;

// 분석 결과 한 벌. 언어와 무관한 공통 형식이라 C++(clangd)와 나중의 C#(Roslyn) 어댑터가
// 같은 모양을 채우고, 화면은 이 형식만 안다.
public sealed class TraceGraph
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string TargetName { get; set; } = "";
    public string RootDirectory { get; set; } = "";
    public string Language { get; set; } = "";
    public DateTimeOffset AnalyzedAt { get; set; }
    public long DurationMs { get; set; }
    public string AnalyzerVersion { get; set; } = "";

    public List<EntryPoint> EntryPoints { get; set; } = [];
    public Dictionary<string, TraceNode> Nodes { get; set; } = [];
    public List<TraceEdge> Edges { get; set; } = [];

    // "못 본 것". 비어 있지 않으면 화면 상단에 그대로 보여 준다.
    public List<string> Diagnostics { get; set; } = [];

    // 신선도 확인용: 분석에 쓴 입력 파일(소스·프로젝트 파일)과 대상 설정의 지문.
    public List<InputFile> Inputs { get; set; } = [];
    public string ConfigHash { get; set; } = "";

    // 노드의 Signature/BodyHash 를 만든 방식(CodeFingerprint.Version). 0 = 지문 없음(옛 결과).
    public int FingerprintVersion { get; set; }

    // 메시지(패킷·레인 메시지)를 보내고 받는 곳. 소스 텍스트로 찾은 것이라 추정이다(MessagePass).
    // 비어 있으면 3단계 이전 결과라 재분석해야 여정이 보인다.
    public List<ChannelInfo> Channels { get; set; } = [];
    public List<MessageSite> Messages { get; set; } = [];

    // 흐름에 나온 클래스의 선언(멤버·부모). 클래스 다이어그램용.
    public List<TypeInfo> Types { get; set; } = [];
}

public sealed class ChannelInfo
{
    public string Name { get; set; } = "";
    // true: 다른 프로세스로 가는 패킷(여정으로만 잇는다). false: 같은 프로세스 안의 메시지(호출 선으로 잇는다).
    public bool CrossProcess { get; set; }
}

public enum MessageRole
{
    Send,
    Receive,
}

public sealed class MessageSite
{
    public string Channel { get; set; } = "";
    public string Key { get; set; } = "";            // 패킷·메시지 이름 (C2ZMove, EZoneMsg::Tick)
    public MessageRole Role { get; set; }
    public string NodeId { get; set; } = "";         // 보내거나 받는 함수
    public SourceRef Site { get; set; } = new();     // 보낸 줄, case 줄, 또는 핸들러를 등록한 줄
    // 어떻게 찾았나: "직접"(이름이 그 줄에 있음), "변수 타입"(보낸 변수의 선언에서), "case", "등록"
    public string How { get; set; } = "";
}

public sealed class TypeInfo
{
    public string Name { get; set; } = "";           // 한정 이름 (Network::Session)
    public string Kind { get; set; } = "class";      // class / struct
    public string File { get; set; } = "";
    public int Line { get; set; }
    public List<string> Bases { get; set; } = [];    // 부모 이름(짧은 이름, 소스 텍스트 기준)
    public List<TypeMember> Members { get; set; } = [];
}

public sealed class TypeMember
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";           // field / method / ctor
    public string Detail { get; set; } = "";         // 타입 또는 시그니처 (clangd detail)
}

public sealed class EntryPoint
{
    public string NodeId { get; set; } = "";
    // 화면에 보이는 이름. 패킷 핸들러라면 패킷 이름(C2ZMove), 없으면 함수 이름.
    public string Label { get; set; } = "";
    // 왼쪽 목록을 묶는 단위(예: "ZoneServer / PlayerStreamHandler").
    public string Group { get; set; } = "";
    // 진입점으로 판단한 근거: 등록한 줄.
    public SourceRef RegisteredAt { get; set; } = new();
    public string Detector { get; set; } = "";
    // 진입점 종류(EntryKind). 이 값이 없던 때 저장한 결과는 비어 있고, 그때는 EntryKind.Of 가 Detector 로 추정한다.
    public string Kind { get; set; } = "";
}

public sealed class TraceNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";          // 한정 이름 (Zone::Fanout)
    public string Detail { get; set; } = "";        // 시그니처 등 clangd가 준 부가 설명
    public NodeKind Kind { get; set; }
    public string File { get; set; } = "";          // 루트 기준 상대 경로, '/' 구분
    public int StartLine { get; set; }              // 1부터
    public int EndLine { get; set; }
    public string Code { get; set; } = "";
    public bool CodeTruncated { get; set; }

    // 전후 비교용 지문. 잘리지 않은 전체 본문 기준이며 공백·주석 차이는 무시한다.
    public string Signature { get; set; } = "";
    public string BodyHash { get; set; } = "";

    // 이 노드 아래를 왜 더 펼치지 않았는지. null 이면 끝까지 펼쳤다.
    public StopReason? Stop { get; set; }

    // 선언만 있는 가상 함수의 구현 후보(파생 클래스에서 같은 이름). 소스 텍스트로 찾은 "추정"이다.
    public List<ImplementationRef> Implementations { get; set; } = [];

    // 설정(tags)의 이름 규칙에 걸린 표시: "DB", "스레드" 등. 흐름 요약에 쓴다.
    public List<string> Tags { get; set; } = [];
    // 설정(tags)의 본문 규칙(code)에 걸린 줄: 호출로 잡히지 않는 SP 이름 등.
    public List<TaggedLine> TagLines { get; set; } = [];
}

public sealed class TaggedLine
{
    public int Line { get; set; }
    public List<string> Tags { get; set; } = [];
}

public sealed class ImplementationRef
{
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public int Line { get; set; }
}

public enum NodeKind
{
    Function,
    Method,
    Constructor,
    External,   // 분석 범위 밖(표준 라이브러리, 3rd 등). 코드 없이 이름만.
}

public enum StopReason
{
    External,       // 범위 밖이라 들어가지 않음
    Hidden,         // 설정의 hideCallees 에 걸림(로그 등 잡음)
    DepthLimit,     // 깊이 제한
    NodeLimit,      // 전체 노드 수 제한
    NoDefinition,   // 정의를 찾지 못함(선언만 있음)
    Failed,         // clangd 요청 실패
}

public sealed class TraceEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    // 호출한 위치들. 같은 함수를 여러 번 부르면 여러 개.
    public List<SourceRef> CallSites { get; set; } = [];
    // 어떻게 연결했는지.
    //  Static       : clangd 가 정적으로 해석한 호출(가상 함수는 선언된 쪽으로 연결된다)
    //  Registration : X.Dispatch(...) 를 같은 파일에서 X.Register(...) 한 핸들러로 이은 것(추정)
    //  Message      : 같은 프로세스 안 메시지(레인 등)를 보낸 곳 → 그 메시지를 등록한 핸들러(추정)
    public string Resolution { get; set; } = "Static";
}

public sealed class SourceRef
{
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }

    public override string ToString() => $"{File}:{Line}";
}
