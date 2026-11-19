using System.Security.Cryptography;

namespace ReviewTool.Core;

// 분석에 쓴 입력 파일 하나의 지문. 크기·수정 시각이 같으면 같은 것으로 보고, 다르면 내용 해시로 다시 확인한다
// (git checkout 처럼 시각만 바뀌는 경우를 "바뀜"으로 세지 않으려고).
public sealed class InputFile
{
    public string File { get; set; } = "";   // 루트 기준 상대 경로
    public long Size { get; set; }
    public long LastWriteTicks { get; set; }
    public string Hash { get; set; } = "";

    public static InputFile Read(string root, string path)
    {
        var info = new FileInfo(path);
        return new InputFile
        {
            File = Path.GetRelativePath(root, path).Replace('\\', '/'),
            Size = info.Length,
            LastWriteTicks = info.LastWriteTimeUtc.Ticks,
            Hash = HashOf(path),
        };
    }

    public static string HashOf(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream))[..16];
    }
}

public enum FreshnessState
{
    Fresh,      // 기록한 입력과 지금 파일이 모두 같다
    Stale,      // 바뀐 파일, 지운 파일, 새 파일, 설정 변경 중 하나 이상
    Unknown,    // 확인할 수 없다(입력 기록이 없는 옛 결과 등). Fresh 라고 하지 않는다
}

public sealed record FreshnessReport(FreshnessState State, IReadOnlyList<string> Changed, IReadOnlyList<string> Added,
                                     IReadOnlyList<string> Removed, bool ConfigChanged, string? Reason = null)
{
    public int Count => Changed.Count + Added.Count + Removed.Count + (ConfigChanged ? 1 : 0);

    public bool IsChanged(string relativeFile) =>
        Changed.Contains(relativeFile, StringComparer.OrdinalIgnoreCase) ||
        Removed.Contains(relativeFile, StringComparer.OrdinalIgnoreCase);
}

public static class Freshness
{
    // currentFiles: 지금 분석한다면 입력이 될 파일들(절대 경로). 언어 어댑터가 같은 규칙으로 만든다.
    public static FreshnessReport Evaluate(TraceGraph graph, IEnumerable<string> currentFiles, string? currentConfigHash)
    {
        if (graph.Inputs.Count == 0)
        {
            return new FreshnessReport(FreshnessState.Unknown, [], [], [], false, "입력 기록이 없는 결과입니다(이전 버전).");
        }
        var root = graph.RootDirectory;
        if (!Directory.Exists(root))
        {
            return new FreshnessReport(FreshnessState.Unknown, [], [], [], false, $"분석한 폴더가 없습니다: {root}");
        }

        var recorded = graph.Inputs.ToDictionary(i => i.File, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();
        var added = new List<string>();
        foreach (var path in currentFiles)
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            seen.Add(rel);
            if (!recorded.TryGetValue(rel, out var before))
            {
                added.Add(rel);
                continue;
            }
            var info = new FileInfo(path);
            if (info.Length == before.Size && info.LastWriteTimeUtc.Ticks == before.LastWriteTicks)
            {
                continue;
            }
            if (info.Length != before.Size || InputFile.HashOf(path) != before.Hash)
            {
                changed.Add(rel);
            }
        }
        var removed = recorded.Keys.Where(k => !seen.Contains(k)).ToList();
        var configChanged = currentConfigHash is not null && graph.ConfigHash.Length > 0 && currentConfigHash != graph.ConfigHash;
        var state = changed.Count + added.Count + removed.Count == 0 && !configChanged ? FreshnessState.Fresh : FreshnessState.Stale;
        return new FreshnessReport(state, changed.Order().ToList(), added.Order().ToList(), removed.Order().ToList(), configChanged);
    }

    public static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16];
}
