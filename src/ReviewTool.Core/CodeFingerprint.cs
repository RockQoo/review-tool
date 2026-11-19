using System.Text.RegularExpressions;

namespace ReviewTool.Core;

// 함수 본문·시그니처의 지문. 공백과 주석만 바뀐 것은 "바뀜"으로 세지 않는다.
// 문자열·문자 리터럴 안은 그대로 둔다("a b" → "ab" 는 실제 변경이다).
public static partial class CodeFingerprint
{
    // 지문 계산 방식이 바뀌면 올린다. 방식이 다른 두 결과의 본문은 비교하지 않는다(전부 "바뀜"으로 보이므로).
    public const int Version = 2;

    public static string Body(string code) => Freshness.HashText(Normalize(code));

    // 본문 '{' 앞까지(반환형·이름·매개변수·const 등). 공백을 하나로.
    public static string Signature(string code)
    {
        var text = Normalize(code, keepSingleSpaces: true);
        var brace = IndexOutsideLiterals(text, '{');
        return (brace >= 0 ? text[..brace] : text).Trim();
    }

    // 주석 제거, 리터럴 밖 공백 정리. 식별자 사이 공백은 토큰 경계라 하나 남긴다("return x" ≠ "returnx").
    public static string Normalize(string code, bool keepSingleSpaces = false) =>
        Tokens().Replace(code, m =>
        {
            if (m.Groups["lit"].Success)
            {
                return m.Value;
            }
            // 주석과 공백은 같은 취급: 앞뒤가 모두 식별자 문자일 때만 공백 하나
            var before = m.Index > 0 ? code[m.Index - 1] : ' ';
            var after = m.Index + m.Length < code.Length ? code[m.Index + m.Length] : ' ';
            return keepSingleSpaces || (IsWord(before) && IsWord(after)) ? " " : "";
        }).Trim();

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int IndexOutsideLiterals(string text, char target)
    {
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == target)
            {
                return i;
            }
        }
        return -1;
    }

    // 리터럴(보존) | 주석 | 공백. 원시 문자열 R"(...)" 은 드물어 일반 문자열로 처리한다.
    [GeneratedRegex(@"(?<lit>""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*')|//[^\n]*|/\*.*?\*/|\s+", RegexOptions.Singleline)]
    private static partial Regex Tokens();
}
