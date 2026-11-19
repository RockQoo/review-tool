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
        return new Snapshot(id, Label(id, graph), graph.AnalyzedAt);
    }

    public static IReadOnlyList<Snapshot> List(WorkPaths paths)
    {
        var result = new List<Snapshot>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory(paths), "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var graph = TraceStore.TryLoad(file);
            if (graph is not null)
            {
                result.Add(new Snapshot(id, Label(id, graph), graph.AnalyzedAt));
            }
        }
        // 직전 분석이 맨 앞, 나머지는 최신순
        return result.OrderBy(s => s.Id == PreviousId ? 0 : 1).ThenByDescending(s => s.AnalyzedAt).ToList();
    }

    public static TraceGraph? Load(WorkPaths paths, string id) =>
        id.Any(c => c is '/' or '\\' or '.') ? null : TraceStore.TryLoad(Path.Combine(Directory(paths), id + ".json"));

    private static string Label(string id, TraceGraph g) =>
        (id == PreviousId ? "직전 분석" : "저장한 기준") + $" ({g.AnalyzedAt.ToLocalTime():MM-dd HH:mm})";
}
