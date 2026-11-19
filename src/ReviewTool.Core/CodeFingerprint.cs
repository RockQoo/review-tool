using System.Text.RegularExpressions;

namespace ReviewTool.Core;

// 함수 본문·시그니처의 지문. 공백과 주석만 바뀐 것은 "바뀜"으로 세지 않는다.
public static partial class CodeFingerprint
{
    public static string Body(string code) => Freshness.HashText(Normalize(code));

    // 본문 '{' 앞까지(반환형·이름·매개변수·const 등). 공백을 하나로.
    public static string Signature(string code)
    {
        var withoutComments = Comments().Replace(code, " ");
        var brace = withoutComments.IndexOf('{');
        var head = brace >= 0 ? withoutComments[..brace] : withoutComments;
        return Spaces().Replace(head, " ").Trim();
    }

    public static string Normalize(string code) => Spaces().Replace(Comments().Replace(code, " "), "");

    // 문자열 안의 // 는 드물어 단순 처리한다(지문 용도)
    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
