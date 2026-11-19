using System.Text;
using System.Text.RegularExpressions;

namespace ReviewTool.Core;

// 루트 기준 상대 경로('/' 구분)에 쓰는 간단한 glob. ** = 폴더 여러 단계, * = 한 단계 안, ? = 한 글자.
public static class GlobMatcher
{
    public static Regex ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        var g = glob.Replace('\\', '/');
        for (var i = 0; i < g.Length; i++)
        {
            var c = g[i];
            if (c == '*')
            {
                if (i + 1 < g.Length && g[i + 1] == '*')
                {
                    i++;
                    // "**/" 는 0단계 이상
                    if (i + 1 < g.Length && g[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static Func<string, bool> Build(IReadOnlyList<string> include, IReadOnlyList<string> exclude)
    {
        var inc = include.Select(ToRegex).ToArray();
        var exc = exclude.Select(ToRegex).ToArray();
        return path =>
        {
            var p = path.Replace('\\', '/');
            if (inc.Length > 0 && !inc.Any(r => r.IsMatch(p)))
            {
                return false;
            }
            return !exc.Any(r => r.IsMatch(p));
        };
    }
}
