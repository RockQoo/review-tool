using System.Text;

namespace ReviewTool.Core;

// 코드 문법 색칠(화면 표시용). C++·C# 의 키워드·문자열·주석·숫자·전처리 줄만 나눈다.
// 줄 단위로 그리는 코드 칸에 맞게 줄마다 토큰 목록을 돌려주고, 여러 줄 주석·원시 문자열은 이전 줄 상태를 이어받는다.
// 컴파일러가 아니라 눈으로 읽기 편하게 하는 것이 목적이라 매크로·템플릿 등에서 틀릴 수 있다(코드 글자는 그대로다).
public static class CodeColor
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name",
        Justification = "토큰 종류 '문자열'을 뜻하며 이름이 CSS 클래스 tk-string 으로 쓰인다")]
    public enum Kind { Plain, Keyword, String, Comment, Number, Preprocessor }

    public sealed record Token(string Text, Kind Kind);

    private enum State { Normal, BlockComment, RawString, VerbatimString }

    public static List<List<Token>> Lines(IReadOnlyList<string> lines, string language)
    {
        var csharp = language.Equals("csharp", StringComparison.OrdinalIgnoreCase);
        var keywords = csharp ? CSharpKeywords : CppKeywords;
        var result = new List<List<Token>>(lines.Count);
        var state = State.Normal;
        var rawEnd = "";   // RawString 이 끝나는 글자(C++: )delim", C#: """)
        foreach (var line in lines)
        {
            var tokens = new List<Token>();
            var plain = new StringBuilder();
            void Flush()
            {
                if (plain.Length > 0)
                {
                    tokens.Add(new Token(plain.ToString(), Kind.Plain));
                    plain.Clear();
                }
            }
            void Add(string text, Kind kind)
            {
                Flush();
                if (text.Length > 0)
                {
                    tokens.Add(new Token(text, kind));
                }
            }

            var i = 0;
            // 이전 줄에서 이어지는 것
            if (state == State.BlockComment)
            {
                var end = line.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                {
                    Add(line, Kind.Comment);
                    result.Add(tokens);
                    continue;
                }
                Add(line[..(end + 2)], Kind.Comment);
                i = end + 2;
                state = State.Normal;
            }
            else if (state == State.RawString)
            {
                var end = line.IndexOf(rawEnd, StringComparison.Ordinal);
                if (end < 0)
                {
                    Add(line, Kind.String);
                    result.Add(tokens);
                    continue;
                }
                Add(line[..(end + rawEnd.Length)], Kind.String);
                i = end + rawEnd.Length;
                state = State.Normal;
            }
            else if (state == State.VerbatimString)
            {
                var end = VerbatimEnd(line, 0);
                if (end < 0)
                {
                    Add(line, Kind.String);
                    result.Add(tokens);
                    continue;
                }
                Add(line[..end], Kind.String);
                i = end;
                state = State.Normal;
            }
            else if (line.TrimStart().StartsWith('#'))
            {
                Add(line, Kind.Preprocessor);
                result.Add(tokens);
                continue;
            }

            while (i < line.Length)
            {
                var c = line[i];
                var rest = line.AsSpan(i);
                if (rest.StartsWith("//"))
                {
                    Add(line[i..], Kind.Comment);
                    i = line.Length;
                }
                else if (rest.StartsWith("/*"))
                {
                    var end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        Add(line[i..], Kind.Comment);
                        state = State.BlockComment;
                        i = line.Length;
                    }
                    else
                    {
                        Add(line[i..(end + 2)], Kind.Comment);
                        i = end + 2;
                    }
                }
                else if (!csharp && RawStart(line, i) is { } raw)
                {
                    // C++ R"delim( ... )delim"  (앞에 u8·L 등이 붙을 수 있다)
                    var (openLen, close) = raw;
                    var end = line.IndexOf(close, i + openLen, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        Add(line[i..], Kind.String);
                        state = State.RawString;
                        rawEnd = close;
                        i = line.Length;
                    }
                    else
                    {
                        Add(line[i..(end + close.Length)], Kind.String);
                        i = end + close.Length;
                    }
                }
                else if (csharp && rest.StartsWith("\"\"\""))
                {
                    // C# 원시 문자열 """ ... """ (따옴표 수는 3개만 본다)
                    var end = line.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        Add(line[i..], Kind.String);
                        state = State.RawString;
                        rawEnd = "\"\"\"";
                        i = line.Length;
                    }
                    else
                    {
                        Add(line[i..(end + 3)], Kind.String);
                        i = end + 3;
                    }
                }
                else if (csharp && (rest.StartsWith("@\"") || rest.StartsWith("$@\"") || rest.StartsWith("@$\"")))
                {
                    // C# 축자 문자열 @"..."(여러 줄 가능, "" 는 따옴표 하나)
                    var open = rest.StartsWith("@\"") ? 2 : 3;
                    var end = VerbatimEnd(line, i + open);
                    if (end < 0)
                    {
                        Add(line[i..], Kind.String);
                        state = State.VerbatimString;
                        i = line.Length;
                    }
                    else
                    {
                        Add(line[i..end], Kind.String);
                        i = end;
                    }
                }
                else if (c == '"' || c == '\'' || (csharp && c == '$' && i + 1 < line.Length && line[i + 1] == '"'))
                {
                    var start = i;
                    var quote = c == '$' ? '"' : c;
                    i += c == '$' ? 2 : 1;
                    while (i < line.Length && line[i] != quote)
                    {
                        i += line[i] == '\\' ? 2 : 1;
                    }
                    i = Math.Min(line.Length, i + 1);
                    Add(line[start..i], Kind.String);
                }
                else if (char.IsDigit(c) && (i == 0 || !IsIdent(line[i - 1])))
                {
                    var start = i;
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '.' or '\'' or '_'))
                    {
                        i++;
                    }
                    Add(line[start..i], Kind.Number);
                }
                else if (IsIdentStart(c))
                {
                    var start = i;
                    while (i < line.Length && IsIdent(line[i]))
                    {
                        i++;
                    }
                    var word = line[start..i];
                    if (keywords.Contains(word))
                    {
                        Add(word, Kind.Keyword);
                    }
                    else
                    {
                        plain.Append(word);
                    }
                }
                else
                {
                    plain.Append(c);
                    i++;
                }
            }
            Flush();
            result.Add(tokens);
        }
        return result;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

    // C++ 원시 문자열 시작이면 (여는 부분 길이, 닫는 글자)
    private static (int OpenLength, string Close)? RawStart(string line, int i)
    {
        var p = i;
        foreach (var prefix in new[] { "u8R\"", "uR\"", "UR\"", "LR\"", "R\"" })
        {
            if (string.CompareOrdinal(line, p, prefix, 0, prefix.Length) == 0)
            {
                if (i > 0 && IsIdent(line[i - 1]))
                {
                    return null;
                }
                var delimStart = p + prefix.Length;
                var paren = line.IndexOf('(', delimStart);
                if (paren < 0 || paren - delimStart > 16)
                {
                    return null;
                }
                var delim = line[delimStart..paren];
                return (paren + 1 - i, ")" + delim + "\"");
            }
        }
        return null;
    }

    // 축자 문자열의 끝 다음 위치("" 는 건너뜀). 없으면 -1
    private static int VerbatimEnd(string line, int from)
    {
        for (var i = from; i < line.Length; i++)
        {
            if (line[i] != '"')
            {
                continue;
            }
            if (i + 1 < line.Length && line[i + 1] == '"')
            {
                i++;
                continue;
            }
            return i + 1;
        }
        return -1;
    }

    private static readonly HashSet<string> CppKeywords =
    [
        "alignas", "alignof", "asm", "auto", "bool", "break", "case", "catch", "char", "char8_t", "char16_t", "char32_t",
        "class", "co_await", "co_return", "co_yield", "concept", "const", "consteval", "constexpr", "constinit",
        "const_cast", "continue", "decltype", "default", "delete", "do", "double", "dynamic_cast", "else", "enum",
        "explicit", "export", "extern", "false", "final", "float", "for", "friend", "goto", "if", "inline", "int", "long",
        "mutable", "namespace", "new", "noexcept", "nullptr", "operator", "override", "private", "protected", "public",
        "register", "reinterpret_cast", "requires", "return", "short", "signed", "sizeof", "static", "static_assert",
        "static_cast", "struct", "switch", "template", "this", "thread_local", "throw", "true", "try", "typedef",
        "typeid", "typename", "union", "unsigned", "using", "virtual", "void", "volatile", "wchar_t", "while",
    ];

    private static readonly HashSet<string> CSharpKeywords =
    [
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
        "const", "continue", "decimal", "default", "delegate", "do", "double", "dynamic", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "get", "global", "goto", "if",
        "implicit", "in", "init", "int", "interface", "internal", "is", "lock", "long", "nameof", "namespace", "new",
        "null", "object", "operator", "out", "override", "params", "partial", "private", "protected", "public",
        "readonly", "record", "ref", "required", "return", "sbyte", "sealed", "set", "short", "sizeof", "stackalloc",
        "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked",
        "unsafe", "ushort", "using", "var", "virtual", "void", "volatile", "when", "where", "while", "with", "yield",
    ];
}
