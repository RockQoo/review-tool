namespace ReviewTool.Cpp;

// clangd 의 호출 계층은 함수 "이름" 위치만 준다. 본문 범위는 소스를 직접 읽어 중괄호 짝으로 찾는다.
// 완전한 C++ 파서가 아니라 화면에 코드를 보여 주기 위한 근사치다. 문자열·문자·주석은 건너뛴다.
public static partial class FunctionBody
{
    public sealed record Span(int StartLine, int EndLine, bool HasBody); // 1부터 세는 줄

    // nameLine0/nameCol 은 0부터 센 이름 위치.
    public static Span Find(string[] lines, int nameLine0, int nameCol, int maxBackLines = 3)
    {
        if (nameLine0 < 0 || nameLine0 >= lines.Length)
        {
            return new Span(nameLine0 + 1, nameLine0 + 1, false);
        }

        var start = ExtendBackward(lines, nameLine0, maxBackLines);
        var parenDepth = 0;
        var line = nameLine0;
        var col = nameCol;
        var state = new ScanState();
        var sawParams = false;     // 매개변수 목록 ')' 를 지났나
        var inInitList = false;    // 그 뒤 ':' 를 만나 생성자 초기화 목록 안인가

        // 1) 이름 뒤에서 본문 '{' 를 찾는다. 괄호 밖의 ';' 가 먼저 나오면 선언뿐이다.
        while (line < lines.Length)
        {
            var text = lines[line];
            for (; col < text.Length; col++)
            {
                if (state.Skip(text, ref col))
                {
                    continue;
                }
                var c = text[col];
                if (c == '(')
                {
                    parenDepth++;
                }
                else if (c == ')')
                {
                    parenDepth--;
                    if (parenDepth == 0)
                    {
                        sawParams = true;
                    }
                }
                else if (parenDepth == 0 && c == ':' && sawParams && !IsScope(text, col))
                {
                    inInitList = true;
                }
                else if (parenDepth == 0 && c == ';')
                {
                    return new Span(start + 1, line + 1, false);
                }
                else if (parenDepth == 0 && c == '{')
                {
                    // 2) 짝 맞는 '}' 를 찾는다.
                    var (endLine, endCol) = MatchBrace(lines, line, col, ref state);
                    if (endLine < 0)
                    {
                        return new Span(start + 1, lines.Length, true);
                    }
                    // 초기화 목록의 a_{x} 는 '{' 바로 앞이 멤버 이름이다. 본문 '{' 앞은 ')' 나 '}'.
                    if (inInitList && IsIdentifierEnd(PrevNonSpace(lines, line, col - 1)))
                    {
                        line = endLine;
                        col = endCol;
                        text = lines[line];
                        continue;
                    }
                    return new Span(start + 1, endLine + 1, true);
                }
            }
            line++;
            col = 0;
        }
        return new Span(start + 1, nameLine0 + 1, false);
    }

    // 이름 뒤 괄호 안의 매개변수 "타입"만 뽑는다(이름·기본값·공백 차이 제거). 오버로드를 가르는 용도라 근사치다.
    // 예: "(const Common::C2ZMove& packet, int n = 0)" -> ["const Common::C2ZMove&", "int"]
    public static List<string>? ParameterTypes(string[] lines, int nameLine0, int nameCol)
    {
        if (nameLine0 < 0 || nameLine0 >= lines.Length)
        {
            return null;
        }
        // 이름 뒤 첫 '(' 부터 짝 ')' 까지 (최대 20줄)
        var text = string.Join('\n', lines.Skip(nameLine0).Take(20));
        var open = text.IndexOf('(', Math.Min(nameCol, text.Length));
        if (open < 0)
        {
            return null;
        }
        var depth = 0;
        var close = -1;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                close = i;
                break;
            }
        }
        if (close < 0)
        {
            return null;
        }
        var inner = CommentRegex().Replace(text[(open + 1)..close], " ");
        var result = new List<string>();
        foreach (var raw in SplitTopLevel(inner))
        {
            var p = raw;
            var eq = TopLevelIndexOf(p, '=');
            if (eq >= 0)
            {
                p = p[..eq];
            }
            p = SpaceRegex().Replace(p, " ").Trim();
            if (p.Length == 0 || p == "void")
            {
                continue;
            }
            // 끝의 식별자가 매개변수 이름이면 뗀다(타입 키워드거나 토큰이 하나뿐이면 이름이 없는 것)
            var m = TrailingName().Match(p);
            if (m.Success && m.Groups[1].Value.Length > 0 && !TypeKeywords.Contains(m.Groups[2].Value))
            {
                p = m.Groups[1].Value.TrimEnd();
            }
            result.Add(SpaceRegex().Replace(p.Replace(" &", "&").Replace(" *", "*"), " "));
        }
        return result;
    }

    private static readonly HashSet<string> TypeKeywords =
        ["int", "char", "bool", "float", "double", "long", "short", "unsigned", "signed", "void", "auto", "const", "volatile"];

    private static IEnumerable<string> SplitTopLevel(string s)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '(' or '<' or '[' or '{':
                    depth++;
                    break;
                case ')' or '>' or ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return s[start..i];
                    start = i + 1;
                    break;
            }
        }
        yield return s[start..];
    }

    private static int TopLevelIndexOf(string s, char c)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is '(' or '<' or '[' or '{')
            {
                depth++;
            }
            else if (s[i] is ')' or '>' or ']' or '}')
            {
                depth--;
            }
            else if (s[i] == c && depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"//[^\n]*|/\*.*?\*/", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex CommentRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex SpaceRegex();

    // (타입 부분)(끝 식별자). 타입 부분은 공백·&·*·> 로 끝나야 한다.
    [System.Text.RegularExpressions.GeneratedRegex(@"^(.*[\s&\*>])([A-Za-z_]\w*)$")]
    private static partial System.Text.RegularExpressions.Regex TrailingName();

    // 반환형이나 template<> 가 윗줄에 있는 경우를 포함한다.
    private static int ExtendBackward(string[] lines, int nameLine0, int maxBackLines)
    {
        var start = nameLine0;
        for (var i = 0; i < maxBackLines && start > 0; i++)
        {
            var prev = lines[start - 1].Trim();
            if (prev.Length == 0 || prev.EndsWith(';') || prev.EndsWith('}') || prev.EndsWith('{') ||
                prev.StartsWith("//", StringComparison.Ordinal) || prev.StartsWith('#') || prev.EndsWith("*/", StringComparison.Ordinal) || prev.EndsWith(':'))
            {
                break;
            }
            start--;
        }
        return start;
    }

    private static (int Line, int Col) MatchBrace(string[] lines, int line, int col, ref ScanState state)
    {
        var depth = 0;
        while (line < lines.Length)
        {
            var text = lines[line];
            for (; col < text.Length; col++)
            {
                if (state.Skip(text, ref col))
                {
                    continue;
                }
                if (text[col] == '{')
                {
                    depth++;
                }
                else if (text[col] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return (line, col);
                    }
                }
            }
            line++;
            col = 0;
        }
        return (-1, -1);
    }

    private static bool IsScope(string text, int col) =>
        (col + 1 < text.Length && text[col + 1] == ':') || (col > 0 && text[col - 1] == ':');

    private static bool IsIdentifierEnd(char c) => char.IsLetterOrDigit(c) || c is '_' or '>';

    private static char PrevNonSpace(string[] lines, int line, int col)
    {
        for (var l = line; l >= 0 && l > line - 3; l--)
        {
            var text = lines[l];
            for (var c = l == line ? col : text.Length - 1; c >= 0; c--)
            {
                if (!char.IsWhiteSpace(text[c]))
                {
                    return text[c];
                }
            }
        }
        return '\0';
    }

    // 문자열·문자 리터럴·주석 안의 괄호를 세지 않기 위한 상태.
    private struct ScanState
    {
        private bool inBlockComment_;

        // 건너뛸 문자면 col 을 그 끝으로 옮기고 true.
        public bool Skip(string text, ref int col)
        {
            if (inBlockComment_)
            {
                var end = text.IndexOf("*/", col, StringComparison.Ordinal);
                if (end < 0)
                {
                    col = text.Length;
                }
                else
                {
                    col = end + 1;
                    inBlockComment_ = false;
                }
                return true;
            }
            var c = text[col];
            if (c == '/' && col + 1 < text.Length)
            {
                if (text[col + 1] == '/')
                {
                    col = text.Length;
                    return true;
                }
                if (text[col + 1] == '*')
                {
                    inBlockComment_ = true;
                    col++;
                    return true;
                }
            }
            if (c is '"' or '\'')
            {
                // 원시 문자열 R"(...)" 은 드물어 단순 처리
                var i = col + 1;
                while (i < text.Length && text[i] != c)
                {
                    if (text[i] == '\\')
                    {
                        i++;
                    }
                    i++;
                }
                col = Math.Min(i, text.Length);
                return true;
            }
            return false;
        }
    }
}
