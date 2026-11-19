namespace ReviewTool.Core;

// 비교 기준 보관: data/<대상>/snapshots/
//  - previous.json : 재분석 직전 결과(자동, 항상 하나)
//  - <시각>.json   : 사용자가 "기준으로 저장"한 것
public static class SnapshotStore
{
    public const string PreviousId = "previous";

    public sealed record Snapshot(string Id, string Label, DateTimeOffset AnalyzedAt);

    public static string Directory(WorkPaths paths) =>
        System.IO.Directory.CreateDirectory(Path.Combine(paths.Directory, "snapshots")).FullName;

    // 새 결과를 저장하기 직전에 부른다: 지금 trace.json 을 previous.json 으로 옮겨 둔다.
    public static void KeepPrevious(WorkPaths paths)
    {
        if (File.Exists(paths.TraceFile))
        {
            File.Copy(paths.TraceFile, Path.Combine(Directory(paths), PreviousId + ".json"), overwrite: true);
        }
    }

    public static Snapshot SaveBaseline(WorkPaths paths, TraceGraph graph)
    {
        var id = graph.AnalyzedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        TraceStore.Save(graph, Path.Combine(Directory(paths), id + ".json"));
        return new Snapshot(id, Label(id, graph.AnalyzedAt), graph.AnalyzedAt);
    }

    public static IReadOnlyList<Snapshot> List(WorkPaths paths)
    {
        var result = new List<Snapshot>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory(paths), "*.json"))
        {
            // 목록에는 분석 시각만 필요하다. 그래프 전체(코드 포함)를 읽지 않고 앞부분의 analyzedAt 만 읽는다.
            var id = Path.GetFileNameWithoutExtension(file);
            if (ReadAnalyzedAt(file) is { } at)
            {
                result.Add(new Snapshot(id, Label(id, at), at));
            }
        }
        // 직전 분석이 맨 앞, 나머지는 최신순
        return result.OrderBy(s => s.Id == PreviousId ? 0 : 1).ThenByDescending(s => s.AnalyzedAt).ToList();
    }

    public static TraceGraph? Load(WorkPaths paths, string id) =>
        id.Any(c => c is '/' or '\\' or '.') ? null : TraceStore.TryLoad(Path.Combine(Directory(paths), id + ".json"));

    private static string Label(string id, DateTimeOffset analyzedAt) =>
        (id == PreviousId ? "직전 분석" : "저장한 기준") + $" ({analyzedAt.ToLocalTime():MM-dd HH:mm})";

    // 최상위 "analyzedAt" 만 찾고 멈춘다(TraceGraph 직렬화에서 앞쪽에 있다). 형식이 다르면 null.
    internal static DateTimeOffset? ReadAnalyzedAt(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var buffer = new byte[Math.Min(stream.Length, 4096)];
            var read = stream.Read(buffer, 0, buffer.Length);
            var reader = new System.Text.Json.Utf8JsonReader(buffer.AsSpan(0, read), isFinalBlock: read == stream.Length, state: default);
            while (reader.Read())
            {
                if (reader.TokenType == System.Text.Json.JsonTokenType.PropertyName && reader.CurrentDepth == 1 &&
                    reader.ValueTextEquals("analyzedAt") && reader.Read())
                {
                    return reader.GetDateTimeOffset();
                }
                if (reader.CurrentDepth == 1 && reader.TokenType is System.Text.Json.JsonTokenType.StartArray or System.Text.Json.JsonTokenType.StartObject)
                {
                    reader.Skip();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
        }
        return null;
    }
}
