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
    // 어떻게 연결했는지. clangd 는 정적으로 해석하므로 가상 함수는 선언된 쪽으로 연결된다.
    public string Resolution { get; set; } = "Static";
}

public sealed class SourceRef
{
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }

    public override string ToString() => $"{File}:{Line}";
}
