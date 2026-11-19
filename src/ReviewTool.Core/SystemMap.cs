namespace ReviewTool.Core;

// 전체 흐름도: 프로세스 × 클래스 지도. 클래스 사이 호출을 묶고, 프로세스를 넘는 패킷을 따로 잇는다.
public static class SystemMap
{
    public sealed record ClassBox(string Id, string Process, int Functions, int Entries);

    // Kind: call(정적 호출), guess(등록·메시지 추정 연결), packet(다른 프로세스로 보낸 패킷)
    public sealed record Link(string From, string To, string Kind, int Count, IReadOnlyList<string> Keys);

    public sealed record Result(IReadOnlyList<string> Processes, IReadOnlyList<ClassBox> Classes, IReadOnlyList<Link> Links);

    // 클래스 이름: Ns::Class::Method → Ns::Class. 클래스 밖 함수는 [파일 이름].
    public static string ClassOf(TraceNode node)
    {
        if (node.Kind == NodeKind.External)
        {
            return "";
        }
        var name = StripTemplate(node.Name);
        var cut = name.LastIndexOf("::", StringComparison.Ordinal);
        if (cut > 0 && node.Kind is NodeKind.Method or NodeKind.Constructor)
        {
            return name[..cut];
        }
        return "[" + System.IO.Path.GetFileName(node.File) + "]";
    }

    private static string StripTemplate(string name)
    {
        var sb = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var ch in name)
        {
            if (ch == '<')
            {
                depth++;
            }
            else if (ch == '>')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0)
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    public static Result Build(TraceGraph graph, TraceIndex index)
    {
        var processes = new ProcessMap(graph);
        var entryNodes = graph.EntryPoints.Select(e => e.NodeId).ToHashSet();
        var classOf = graph.Nodes.Values.Where(n => n.Kind != NodeKind.External).ToDictionary(n => n.Id, ClassOf);
        var classes = classOf.GroupBy(kv => kv.Value).Select(g =>
        {
            var process = g.Select(kv => processes.OfFile(graph.Nodes[kv.Key].File))
                .GroupBy(p => p).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key;
            return new ClassBox(g.Key, process, g.Count(), g.Count(kv => entryNodes.Contains(kv.Key)));
        }).OrderBy(c => c.Process, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();

        var links = new Dictionary<(string, string, string), (int Count, SortedSet<string> Keys)>();
        void Add(string from, string to, string kind, string? key)
        {
            if (from == to)
            {
                return;
            }
            var k = (from, to, kind);
            if (!links.TryGetValue(k, out var v))
            {
                v = (0, new SortedSet<string>(StringComparer.Ordinal));
            }
            if (key is not null)
            {
                v.Keys.Add(key);
            }
            links[k] = (v.Count + 1, v.Keys);
        }
        foreach (var e in graph.Edges)
        {
            if (classOf.TryGetValue(e.From, out var a) && classOf.TryGetValue(e.To, out var b))
            {
                Add(a, b, e.Resolution == "Static" ? "call" : "guess", null);
            }
        }
        var cross = graph.Channels.Where(c => c.CrossProcess).Select(c => c.Name).ToHashSet();
        var receivers = graph.Messages.Where(m => m.Role == MessageRole.Receive && cross.Contains(m.Channel))
            .GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.Select(m => m.NodeId).Distinct().ToList());
        foreach (var s in graph.Messages.Where(m => m.Role == MessageRole.Send && cross.Contains(m.Channel)))
        {
            if (!classOf.TryGetValue(s.NodeId, out var from))
            {
                continue;
            }
            foreach (var r in receivers.GetValueOrDefault(s.Key) ?? [])
            {
                if (classOf.TryGetValue(r, out var to))
                {
                    Add(from, to, "packet", s.Key);
                }
            }
        }
        var result = links.Select(kv => new Link(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Value.Count, [.. kv.Value.Keys])).ToList();
        var names = classes.Select(c => c.Process).Distinct().OrderBy(p => p.Length == 0).ThenBy(p => p, StringComparer.Ordinal).ToList();
        return new Result(names, classes, result);
    }

    // 진입점 하나가 거치는 클래스: 같은 프로세스 안 호출 + 여정으로 이어진 다른 프로세스의 처리
    public static HashSet<string> ClassesOnFlow(TraceGraph graph, TraceIndex index, string entryNodeId)
    {
        var starts = new List<string> { entryNodeId };
        if (graph.Channels.Count > 0)
        {
            var journey = new Journey(graph, index).Build(entryNodeId);
            Collect(journey.Downstream, starts);
        }
        var seen = new HashSet<string>();
        var queue = new Queue<string>(starts.Where(graph.Nodes.ContainsKey));
        foreach (var s in queue)
        {
            seen.Add(s);
        }
        while (queue.Count > 0 && seen.Count < 5000)
        {
            foreach (var e in index.ChildrenOf(queue.Dequeue()))
            {
                if (seen.Add(e.To))
                {
                    queue.Enqueue(e.To);
                }
            }
        }
        return seen.Select(id => ClassOf(graph.Nodes[id])).Where(c => c.Length > 0).ToHashSet();
    }

    private static void Collect(JourneyNode n, List<string> into)
    {
        if (n.Kind == JourneyKind.Station && n.NodeId.Length > 0)
        {
            into.Add(n.NodeId);
        }
        foreach (var c in n.Children)
        {
            Collect(c, into);
        }
    }
}
