namespace ReviewTool.Core;

// "이 함수를 바꾸면 어떤 진입점(패킷)이 영향을 받나": 호출자를 거슬러 올라가 닿는 진입점과, 각각의 대표 경로 하나.
public static class ImpactAnalyzer
{
    public sealed record ImpactedEntry(EntryPoint Entry, IReadOnlyList<string> Path);   // Path: 진입점 → … → 대상

    public sealed record ImpactResult(string TargetId, IReadOnlyList<ImpactedEntry> Entries, int CallerCount);

    // 증명하지 못하는 것. 결과와 함께 항상 보여 준다(빈 목록을 "안전"으로 읽지 않게).
    public static readonly string[] Limits =
    [
        "정적 호출만 거슬러 올라갑니다. 디스패처(std::function)·가상 함수·함수 포인터로 부르는 곳은 빠질 수 있습니다.",
        "분석 범위 밖(다른 프로세스, 타이머, 배치)에서 부르는 경로는 나오지 않습니다.",
        "영향받는 진입점이 없다는 것은 \"찾지 못했다\"는 뜻이지 \"바꿔도 안전하다\"는 뜻이 아닙니다.",
    ];

    public static ImpactResult Analyze(TraceGraph graph, string targetId)
    {
        var callers = new Dictionary<string, List<string>>();
        foreach (var e in graph.Edges)
        {
            (callers.TryGetValue(e.To, out var list) ? list : callers[e.To] = []).Add(e.From);
        }
        var entryByNode = graph.EntryPoints.GroupBy(e => e.NodeId).ToDictionary(g => g.Key, g => g.First());

        // 대상에서 위로 너비 우선. next[x] = x 에서 대상 쪽으로 한 걸음 (가장 짧은 경로가 대표 경로가 된다)
        var next = new Dictionary<string, string?> { [targetId] = null };
        var queue = new Queue<string>();
        queue.Enqueue(targetId);
        var found = new List<ImpactedEntry>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (entryByNode.TryGetValue(id, out var entry))
            {
                var path = new List<string>();
                for (string? cur = id; cur is not null; cur = next[cur])
                {
                    path.Add(cur);
                }
                found.Add(new ImpactedEntry(entry, path));
            }
            foreach (var caller in callers.GetValueOrDefault(id) ?? [])
            {
                if (next.TryAdd(caller, id))
                {
                    queue.Enqueue(caller);
                }
            }
        }
        return new ImpactResult(targetId,
            found.OrderBy(f => f.Entry.Group).ThenBy(f => f.Entry.Label).ToList(),
            next.Count - 1);
    }
}
