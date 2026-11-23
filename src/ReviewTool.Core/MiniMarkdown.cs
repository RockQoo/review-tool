using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ReviewTool.Core;

// AI 답을 화면에 보일 만큼만 Markdown → HTML. 먼저 전부 이스케이프하므로 답 안의 HTML·스크립트는 글자로만 보인다.
// 지원: 제목(#), 목록(-, *, 1.), 코드 블록(```), 굵게(**), 인라인 코드(`). 나머지는 글자 그대로.
public static partial class MiniMarkdown
{
    public static string ToHtml(string markdown)
    {
        var sb = new StringBuilder();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inCode = false;
        string? list = null;   // "ul" | "ol"

        void CloseList()
        {
            if (list is not null)
            {
                sb.Append("</").Append(list).Append('>');
                list = null;
            }
        }

        foreach (var raw in lines)
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                CloseList();
                sb.Append(inCode ? "</code></pre>" : "<pre><code>");
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                sb.Append(WebUtility.HtmlEncode(raw)).Append('\n');
                continue;
            }
            if (raw.Trim().Length == 0)
            {
                CloseList();   // 문단 간격은 CSS(p 여백)로
                continue;
            }
            if (HorizontalRule().IsMatch(raw))
            {
                CloseList();
                sb.Append("<hr>");
                continue;
            }

            var heading = Heading().Match(raw);
            if (heading.Success)
            {
                CloseList();
                var level = Math.Min(heading.Groups[1].Length + 2, 6);   // # → h3 (화면 안 작은 제목)
                sb.Append("<h").Append(level).Append('>').Append(Inline(heading.Groups[2].Value)).Append("</h").Append(level).Append('>');
                continue;
            }
            var item = ListItem().Match(raw);
            if (item.Success)
            {
                var kind = char.IsDigit(item.Groups[1].Value[0]) ? "ol" : "ul";
                if (list != kind)
                {
                    CloseList();
                    sb.Append('<').Append(kind).Append('>');
                    list = kind;
                }
                sb.Append("<li>").Append(Inline(item.Groups[2].Value)).Append("</li>");
                continue;
            }
            if (list is not null && raw.StartsWith("  ", StringComparison.Ordinal))
            {
                // 목록 항목에 이어지는 줄
                sb.Append("<div class=\"cont\">").Append(Inline(raw.Trim())).Append("</div>");
                continue;
            }
            CloseList();
            sb.Append("<p>").Append(Inline(raw)).Append("</p>");
        }
        CloseList();
        if (inCode)
        {
            sb.Append("</code></pre>");
        }
        return sb.ToString();
    }

    // 이스케이프한 뒤 `코드` 와 **굵게** 만 태그로. 코드 조각은 자리표시로 빼 두고 굵게를 적용해서
    // "**`A` 를 부른다**" 처럼 굵게 안에 코드가 있어도 되고, 코드 안의 ** 는 굵게가 되지 않는다.
    private static string Inline(string text)
    {
        var parts = text.Split('`');
        var codes = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            var encoded = WebUtility.HtmlEncode(parts[i].Replace('\u0001', ' ').Replace('\u0002', ' '));
            if (i % 2 == 1 && i < parts.Length - 1)   // 짝이 맞는 백틱 안
            {
                sb.Append('\u0001').Append(codes.Count).Append('\u0002');
                codes.Add("<code>" + encoded + "</code>");
                continue;
            }
            if (i % 2 == 1)
            {
                sb.Append('`');   // 짝 없는 백틱은 글자로
            }
            sb.Append(encoded);
        }
        var html = Bold().Replace(sb.ToString(), "<b>$1</b>");
        return CodeSlot().Replace(html, m => codes[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]);
    }

    [GeneratedRegex("\u0001(\\d+)\u0002")]
    private static partial Regex CodeSlot();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s{0,3}([-*]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex HorizontalRule();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();
}
