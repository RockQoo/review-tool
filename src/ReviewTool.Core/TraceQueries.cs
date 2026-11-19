using System.Text;

namespace ReviewTool.Core;

// 호출 관계 빠른 조회. 엣지는 분석기가 호출자 본문의 소스 순서대로 넣었다.
public sealed class TraceIndex
{
    private static readonly List<TraceEdge> None = [];
    private readonly Dictionary<string, List<TraceEdge>> children_;

    public TraceIndex(TraceGraph graph)
    {
        children_ = graph.Edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.ToList());
        Callers = graph.Edges.GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.Select(e => e.From).Distinct().ToList());
    }

    public Dictionary<string, List<string>> Callers { get; }

    public List<TraceEdge> ChildrenOf(string id) => children_.GetValueOrDefault(id) ?? None;

    // from 본문에서 to 를 부른 줄들
    public IEnumerable<int> CallLines(string from, string to) =>
        ChildrenOf(from).Where(e => e.To == to).SelectMany(e => e.CallSites).Select(s => s.Line).Distinct().Order();
}

// 화면·MCP·문맥 복사가 같은 말로 설명하도록 모아 둔 문구와 조회.
public static class TraceQueries
{
    public static string ShortName(string name)
    {
        // Pipeline::MessageProducer::PushMsg -> MessageProducer::PushMsg
        var parts = name.Split("::");
        return parts.Length <= 2 ? name : string.Join("::", parts[^2..]);
    }

    public static string StopText(StopReason r) => r switch
    {
        StopReason.External => "분석 범위 밖 함수라 들어가지 않았습니다.",
        StopReason.Hidden => "설정(hideCallees)으로 숨긴 함수라 안쪽 호출은 따라가지 않았습니다.",
        StopReason.DepthLimit => "깊이 제한에 걸려 안쪽 호출은 따라가지 않았습니다.",
        StopReason.NodeLimit => "전체 함수 수 제한에 걸려 안쪽 호출은 따라가지 않았습니다.",
        StopReason.NoDefinition => "정의(본문)를 찾지 못했습니다. 가상 함수·인터페이스라면 실제 구현은 따로 찾아야 합니다.",
        StopReason.Failed => "clangd 조회가 실패했습니다.",
        _ => "",
    };

    public static bool IsGuess(TraceEdge e) => e.Resolution != "Static";

    // id 그대로, 이름 정확히, "::이름" 으로 끝남, 이름 일부(대소문자 무시) 순으로 찾는다.
    // 정확히 하나로 정해지면 그 하나, 아니면 후보들.
    public static IReadOnlyList<TraceNode> FindFunctions(TraceGraph graph, string query, int limit = 20)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return [];
        }
        if (graph.Nodes.TryGetValue(query, out var exact))
        {
            return [exact];
        }
        var nodes = graph.Nodes.Values;
        foreach (var match in new Func<TraceNode, bool>[]
                 {
                     n => n.Name == query,
                     n => n.Name.EndsWith("::" + query, StringComparison.Ordinal),
                     n => n.Name.Contains(query, StringComparison.OrdinalIgnoreCase),
                 })
        {
            var found = nodes.Where(match).OrderBy(n => n.Kind == NodeKind.External).ThenBy(n => n.Name, StringComparer.Ordinal)
                             .ThenBy(n => n.File, StringComparer.Ordinal).Take(limit).ToList();
            if (found.Count > 0)
            {
                return found;
            }
        }
        return [];
    }

    // 진입점 → … → target 의 가장 짧은 호출 경로. 진입점에서 닿지 않으면 null.
    public static List<string>? PathFrom(TraceGraph graph, TraceIndex index, string entryNodeId, string targetId)
    {
        if (!graph.Nodes.ContainsKey(entryNodeId) || !graph.Nodes.ContainsKey(targetId))
        {
            return null;
        }
        var prev = new Dictionary<string, string?> { [entryNodeId] = null };
        var queue = new Queue<string>([entryNodeId]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (id == targetId)
            {
                var path = new List<string>();
                for (string? cur = id; cur is not null; cur = prev[cur])
                {
                    path.Add(cur);
                }
                path.Reverse();
                return path;
            }
            foreach (var e in index.ChildrenOf(id))
            {
                if (prev.TryAdd(e.To, id))
                {
                    queue.Enqueue(e.To);
                }
            }
        }
        return null;
    }

    // 들여쓴 호출 트리 텍스트. 이미 위에서 펼친 함수는 다시 펼치지 않는다(재귀·공유 호출).
    public static string CallTreeText(TraceGraph graph, TraceIndex index, string rootId, int maxDepth = 4, int maxLines = 300)
    {
        var sb = new StringBuilder();
        var expandedAt = new Dictionary<string, int>();   // 자식을 실제로 펼친 깊이
        var lines = 0;
        var cut = false;

        void Walk(string id, int depth, TraceEdge? via)
        {
            if (lines >= maxLines)
            {
                cut = true;
                return;
            }
            var n = graph.Nodes[id];
            sb.Append(' ', depth * 2).Append("- ").Append(n.Name);
            if (n.Kind != NodeKind.External)
            {
                sb.Append("  (").Append(n.File).Append(':').Append(n.StartLine).Append(')');
            }
            if (via is not null && IsGuess(via))
            {
                sb.Append("  [추정 연결: ").Append(via.Resolution).Append(']');
            }
            if (n.Kind == NodeKind.External)
            {
                sb.Append("  [범위 밖]");
            }
            else if (n.Stop is { } stop)
            {
                sb.Append("  [멈춤: ").Append(stop).Append(']');
            }
            if (n.Implementations.Count > 0)
            {
                sb.Append("  [구현 후보(추정): ").Append(string.Join(", ", n.Implementations.Select(i => i.Name))).Append(']');
            }
            var children = index.ChildrenOf(id);
            // 같은 깊이나 더 얕은 곳에서 이미 자식을 펼쳤으면 반복하지 않는다(재귀·공유 호출).
            // 깊이 제한으로 못 펼친 것은 기록하지 않아서, 더 얕은 경로에서 다시 만나면 펼친다.
            var repeated = children.Count > 0 && expandedAt.TryGetValue(id, out var at) && at <= depth;
            var cutHere = children.Count > 0 && !repeated && depth + 1 > maxDepth;
            if (repeated)
            {
                sb.Append("  (위에서 펼침)");
            }
            sb.Append("  id=").Append(id).Append('\n');
            lines++;
            if (repeated || children.Count == 0)
            {
                return;
            }
            if (cutHere)
            {
                sb.Append(' ', (depth + 1) * 2).Append("… (깊이 ").Append(maxDepth).Append(" 에서 자름, 호출 ").Append(children.Count).Append("개)\n");
                lines++;
                return;
            }
            expandedAt[id] = depth;
            foreach (var e in children)
            {
                Walk(e.To, depth + 1, e);
            }
        }

        if (graph.Nodes.ContainsKey(rootId))
        {
            Walk(rootId, 0, null);
        }
        if (cut)
        {
            sb.Append("… (").Append(maxLines).Append("줄에서 자름)\n");
        }
        return sb.ToString();
    }

    // 줄 번호를 붙인 코드. 호출이 있는 줄 끝에 "// → 부른 함수" 를 단다.
    public static string NumberedCode(TraceGraph graph, TraceIndex index, TraceNode node, ISet<int>? highlight = null)
    {
        var calls = new Dictionary<int, List<string>>();
        foreach (var e in index.ChildrenOf(node.Id))
        {
            foreach (var s in e.CallSites)
            {
                var list = calls.TryGetValue(s.Line, out var l) ? l : calls[s.Line] = [];
                var name = ShortName(graph.Nodes[e.To].Name) + (IsGuess(e) ? "(추정)" : "");
                if (!list.Contains(name))
                {
                    list.Add(name);
                }
            }
        }
        var lines = node.Code.Split('\n');
        var width = (node.StartLine + lines.Length).ToString().Length;
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var no = node.StartLine + i;
            sb.Append(highlight?.Contains(no) == true ? '>' : ' ')
              .Append(no.ToString().PadLeft(width)).Append(" | ").Append(lines[i].TrimEnd('\r'));
            if (calls.TryGetValue(no, out var names))
            {
                sb.Append("    // → ").Append(string.Join(", ", names));
            }
            sb.Append('\n');
        }
        if (node.CodeTruncated)
        {
            sb.Append("… 길어서 잘림 (").Append(node.File).Append(':').Append(node.EndLine).Append(" 까지)\n");
        }
        return sb.ToString();
    }
}
