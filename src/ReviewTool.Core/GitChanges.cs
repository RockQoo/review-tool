using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ReviewTool.Core;

// 분석 폴더의 git 변경(작업 폴더 vs HEAD)을 읽고, 바뀐 줄이 어느 함수(흐름)에 걸리는지 맞춘다.
// 분석 대상 저장소에는 아무것도 쓰지 않는다: 읽기 명령만 쓰고, --no-optional-locks 로 git 이 색인(index)을 고쳐 쓰지 않게 한다.
public static partial class GitChanges
{
    // File: 분석 폴더 기준 상대 경로('/'). DeletionOnly: 지우기만 한 자리(Start = 지운 줄 바로 뒤 줄)
    public sealed record Hunk(string File, int Start, int End, bool DeletionOnly)
    {
        public string Where => DeletionOnly ? $"{File}:{Start} (지운 줄)" : Start == End ? $"{File}:{Start}" : $"{File}:{Start}-{End}";
    }

    public sealed record Result(string? Error, IReadOnlyList<Hunk> Hunks, IReadOnlyList<string> Untracked);

    public sealed record Matched(Hunk Hunk, IReadOnlyList<string> NodeIds);

    public sealed record Unmatched(Hunk Hunk, string Reason);

    public sealed record Map(IReadOnlyList<Matched> Hits, IReadOnlyList<Unmatched> Misses, IReadOnlyList<string> Untracked);

    public static readonly string[] Limits =
    [
        "git 이 아는 변경만 본다(작업 폴더 vs 마지막 커밋). 커밋한 뒤의 변경은 나오지 않는다.",
        "줄 번호는 지금 파일 기준이고 함수 범위는 마지막 분석 기준이다. 분석 뒤 코드가 바뀌었으면(신선도 \"바뀜\") 다시 분석해야 정확하다.",
        "\"흐름에 안 걸림\"은 진입점에서 닿은 함수 본문 밖이라는 뜻이다. 등록 코드·전역·헤더 선언·매크로 변경일 수 있으니 직접 본다.",
    ];

    // run: 테스트에서 바꿔 끼우는 git 실행기(작업 폴더, 인자) → (종료 코드, 표준 출력, 표준 오류)
    public static Result Read(string root, Func<string, string[], (int Code, string Out, string Err)>? run = null)
    {
        run ??= RunGit;
        var check = run(root, ["rev-parse", "--verify", "HEAD"]);
        if (check.Code != 0)
        {
            return new Result($"git 저장소가 아니거나 커밋이 없습니다: {FirstLine(check.Err)}", [], []);
        }
        // --relative: 분석 폴더 기준 경로로, 분석 폴더 밖 변경은 빼고 준다
        var diff = run(root, ["diff", "-U0", "--no-color", "--no-ext-diff", "--relative", "HEAD", "--"]);
        if (diff.Code != 0)
        {
            return new Result($"git diff 실패: {FirstLine(diff.Err)}", [], []);
        }
        var others = run(root, ["ls-files", "--others", "--exclude-standard"]);
        var untracked = others.Code == 0
            ? others.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : [];
        return new Result(null, ParseDiff(diff.Out), untracked);
    }

    public static List<Hunk> ParseDiff(string text)
    {
        var hunks = new List<Hunk>();
        string? file = null;
        string? oldFile = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                oldFile = PathOf(line[4..]);
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                file = PathOf(line[4..]) ?? oldFile;   // 지운 파일은 +++ /dev/null
            }
            else if (file is not null && HunkHeader().Match(line) is { Success: true } m)
            {
                var start = int.Parse(m.Groups["start"].Value);
                var count = m.Groups["count"].Success ? int.Parse(m.Groups["count"].Value) : 1;
                hunks.Add(count == 0
                    ? new Hunk(file, Math.Max(1, start + 1), Math.Max(1, start + 1), true)
                    : new Hunk(file, start, start + count - 1, false));
            }
        }
        return hunks;
    }

    // 바뀐 줄 ↔ 함수 본문 범위(분석 범위 안 함수만)
    public static Map Build(TraceGraph graph, Result changes)
    {
        var functions = graph.Nodes.Values.Where(n => n.Kind != NodeKind.External && n.File.Length > 0 && n.StartLine > 0)
            .GroupBy(n => Norm(n.File)).ToDictionary(g => g.Key, g => g.ToList());
        var inputs = graph.Inputs.Select(i => Norm(i.File)).ToHashSet();
        var hits = new List<Matched>();
        var misses = new List<Unmatched>();
        foreach (var h in changes.Hunks)
        {
            var key = Norm(h.File);
            var inFile = functions.GetValueOrDefault(key) ?? [];
            var ids = inFile.Where(n => n.StartLine <= h.End && Math.Max(n.EndLine, n.StartLine) >= h.Start)
                .OrderBy(n => n.StartLine).Select(n => n.Id).ToList();
            if (ids.Count > 0)
            {
                hits.Add(new Matched(h, ids));
            }
            else
            {
                misses.Add(new Unmatched(h, inFile.Count > 0 || inputs.Contains(key)
                    ? "함수 본문 밖(등록·전역·선언·매크로 등)이거나 진입점에서 닿지 않은 함수"
                    : "분석에 쓰지 않은 파일(범위 밖·설정·새로 생긴 파일 등)"));
            }
        }
        return new Map(hits, misses, changes.Untracked);
    }

    private static string Norm(string path) => path.Replace('\\', '/').TrimStart('.', '/').ToLowerInvariant();

    private static string? PathOf(string spec)
    {
        var s = spec.Trim();
        if (s == "/dev/null")
        {
            return null;
        }
        if (s.StartsWith('"') && s.EndsWith('"') && s.Length >= 2)
        {
            s = s[1..^1];   // 따옴표 친 경로(공백 등). 이스케이프는 단순 경로만 다룬다
        }
        return s.StartsWith("a/", StringComparison.Ordinal) || s.StartsWith("b/", StringComparison.Ordinal) ? s[2..] : s;
    }

    private static string FirstLine(string s) => s.Split('\n').FirstOrDefault()?.Trim() ?? "";

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(?<start>\d+)(?:,(?<count>\d+))? @@")]
    private static partial Regex HunkHeader();

    private static (int, string, string) RunGit(string root, string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("--no-optional-locks");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=false");   // 한글 경로를 \353... 로 바꾸지 않게
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(root);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (-1, "", "git 이 30초 안에 끝나지 않았습니다");
            }
            return (p.ExitCode, stdout.Result, stderr.Result);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, "", $"git 을 실행하지 못했습니다(설치·PATH 확인): {ex.Message}");
        }
    }
}
