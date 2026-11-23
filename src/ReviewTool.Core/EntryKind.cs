namespace ReviewTool.Core;

// 진입점 종류. 화면 왼쪽 목록의 필터(패킷 / 메시지 / 시작 / 테스트 / 함수)와 MCP 목록이 같은 말을 쓴다.
//   packet   다른 프로세스에서 오는 것(패킷 핸들러, API 요청)
//   message  같은 프로세스 안 메시지(레인 등, crossProcess: false 통로)
//   start    시작 함수(main)
//   test     단위 테스트(gtest, xUnit)
//   function 그 밖에 직접 지정한 함수
public static class EntryKind
{
    public const string Packet = "packet";
    public const string Message = "message";
    public const string Start = "start";
    public const string Test = "test";
    public const string Function = "function";

    // 화면에 보이는 순서
    public static readonly string[] All = [Packet, Message, Start, Test, Function];

    public static string Label(string kind) => kind switch
    {
        Packet => "패킷",
        Message => "메시지",
        Start => "시작",
        Test => "테스트",
        _ => "함수",
    };

    // 설정에 적힌 값(영문 또는 한글 이름)을 종류로. 모르는 값이면 null.
    public static string? Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Packet or "패킷" => Packet,
        Message or "메시지" => Message,
        Start or "시작" => Start,
        Test or "테스트" => Test,
        Function or "함수" => Function,
        _ => null,
    };

    // 규칙 이름으로 추정한다(kind 를 적지 않은 규칙, 이 값이 없던 때 저장한 분석 결과).
    // 메시지를 패킷보다 먼저 본다("레인 메시지 등록" 같은 이름).
    public static string Infer(string ruleName)
    {
        var n = ruleName.ToLowerInvariant();
        if (n.Contains("테스트") || n.Contains("test"))
        {
            return Test;
        }
        if (n.Contains("시작") || n.Contains("main"))
        {
            return Start;
        }
        if (n.Contains("메시지") || n.Contains("레인") || n.Contains("message") || n.Contains("lane"))
        {
            return Message;
        }
        if (n.Contains("패킷") || n.Contains("packet") || n.Contains("등록") || n.Contains("핸들러") || n.Contains("handle") ||
            n.Contains("api") || n.Contains("http"))
        {
            return Packet;
        }
        return Function;
    }

    public static string Of(EntryPointRule rule) => Normalize(rule.Kind) ?? Infer(rule.Name);

    public static string Of(EntryPoint entry) => Normalize(entry.Kind) ?? Infer(entry.Detector);
}
