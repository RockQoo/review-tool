using System.Text.RegularExpressions;

namespace ReviewTool.Core;

// 함수 본문의 줄 단위 전후 비교(최장 공통 부분열). 코드 칸에서 "바뀐 줄 보기"에 쓴다.
// 줄을 맞출 때는 앞뒤 공백과 연속 공백을 무시한다(들여쓰기만 바뀐 줄은 같은 줄로 본다). 보여 주는 글자는 원래 줄이다.
public static partial class LineDiff
{
    public enum Op { Same, Added, Removed }

    // Before/After: 그 줄의 파일 줄 번호(없는 쪽은 null)
    public sealed record Line(Op Op, string Text, int? Before, int? After);

    // 줄이 너무 많으면(분석 결과의 코드는 보통 200줄에서 잘려 있다) 표가 커지지 않게 앞에서 자른다
    public const int MaxLines = 600;

    public static List<Line> Compare(IReadOnlyList<string> before, int beforeStart, IReadOnlyList<string> after, int afterStart)
    {
        var a = before.Take(MaxLines).ToList();
        var b = after.Take(MaxLines).ToList();
        var ka = a.Select(Key).ToArray();
        var kb = b.Select(Key).ToArray();
        // lcs[i, j] = a[i..], b[j..] 의 최장 공통 부분열 길이
        var lcs = new int[ka.Length + 1, kb.Length + 1];
        for (var i = ka.Length - 1; i >= 0; i--)
        {
            for (var j = kb.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = ka[i] == kb[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }
        var result = new List<Line>(Math.Max(a.Count, b.Count));
        int x = 0, y = 0;
        while (x < ka.Length || y < kb.Length)
        {
            if (x < ka.Length && y < kb.Length && ka[x] == kb[y])
            {
                result.Add(new Line(Op.Same, b[y], beforeStart + x, afterStart + y));
                x++;
                y++;
            }
            else if (y < kb.Length && (x >= ka.Length || lcs[x, y + 1] >= lcs[x + 1, y]))
            {
                result.Add(new Line(Op.Added, b[y], null, afterStart + y));
                y++;
            }
            else
            {
                result.Add(new Line(Op.Removed, a[x], beforeStart + x, null));
                x++;
            }
        }
        // 같은 자리에서 지운 줄이 더한 줄보다 먼저 보이게(읽기 쉬운 순서: - 다음 +)
        for (var i = 1; i < result.Count; i++)
        {
            var j = i;
            while (j > 0 && result[j].Op == Op.Removed && result[j - 1].Op == Op.Added)
            {
                (result[j - 1], result[j]) = (result[j], result[j - 1]);
                j--;
            }
        }
        return result;
    }

    public static (int Added, int Removed) Count(IEnumerable<Line> lines)
    {
        var list = lines.ToList();
        return (list.Count(l => l.Op == Op.Added), list.Count(l => l.Op == Op.Removed));
    }

    private static string Key(string line) => Spaces().Replace(line.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
