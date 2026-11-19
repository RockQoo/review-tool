using System.Text;

namespace ReviewTool.Core;

// 흐름 요약: 진입점에서 소스 순서대로(깊이 우선) 내려가며 표시(tags)가 붙은 함수를 부르는 곳만 모은다.
// main 이면 "설정 → 스레드 → 등록 → 네트워크" 같은 시작 순서가, 패킷 핸들러면 DB·캐시·작업 단위 접근 순서가 보인다.
public static class FlowSummary
{
    // CalleeId 가 "" 이면 호출이 아니라 본문 규칙(tags[].code)에 걸린 줄이다(CallerId 본문의 Line).
    public sealed record Item(IReadOnlyList<string> Tags, string CalleeId, string CallerId, int Line, string LineText,
                              int Depth, IReadOnlyList<string> Path, string Resolution);

    public sealed record Result(IReadOnlyList<Item> Items, bool Cut, IReadOnlyList<string> AllTags);

    public static readonly string[] Limits =
    [
        "표시는 함수 이름 규칙(설정 tags)으로 붙인 추정입니다. 이름이 규칙과 다르면 빠집니다.",
        "순서는 소스에 적힌 순서입니다. 분기·반복·비동기(레인으로 넘김)는 실제 실행 순서와 다를 수 있습니다.",
        "같은 함수는 처음 만난 곳에서 한 번만 펼칩니다. 함수 주소만 넘긴 곳(핸들러 등록)은 따라가지 않습니다.",
    ];

    public static Result Build(TraceGraph graph, TraceIndex index, string rootId, int maxDepth = 10, int limit = 400)
    {
        var items = new List<Item>();
        var expanded = new HashSet<string>();
        var cut = false;

        void Walk(List<string> path, int depth)
        {
            var id = path[^1];
            if (!expanded.Add(id))
            {
                return;
            }
            var caller = graph.Nodes[id];
            var edges = index.ChildrenOf(id);
            var callLines = edges.Where(e => graph.Nodes.TryGetValue(e.To, out var t) && t.Tags.Count > 0)
                .SelectMany(e => e.CallSites.Select(s => s.Line)).ToHashSet();
            // 본문 규칙에 걸린 줄(호출로 잡히지 않은 SP 이름 등)은 그 줄 앞의 호출들 사이에 끼운다
            var lineHits = new Queue<TaggedLine>(caller.TagLines.Where(t => !callLines.Contains(t.Line)).OrderBy(t => t.Line));
            void LinesBefore(int line)
            {
                while (lineHits.Count > 0 && lineHits.Peek().Line < line && items.Count < limit)
                {
                    var hit = lineHits.Dequeue();
                    items.Add(new Item(hit.Tags, "", id, hit.Line, LineText(caller, hit.Line), depth, [.. path], "Static"));
                }
            }
            foreach (var e in edges)
            {
                if (items.Count >= limit)
                {
                    cut = true;
                    return;
                }
                LinesBefore(e.CallSites.Count > 0 ? e.CallSites.Min(s => s.Line) : int.MaxValue);
                // Register(this, &C::H) 처럼 주소만 넘긴 것은 실행이 아니다(시작 흐름이 핸들러 안으로 새지 않게)
                if (!graph.Nodes.TryGetValue(e.To, out var callee) || TraceQueries.IsAddressOnly(graph, e))
                {
                    continue;
                }
                if (callee.Tags.Count > 0)
                {
                    var line = e.CallSites.Count > 0 ? e.CallSites.Min(s => s.Line) : caller.StartLine;
                    items.Add(new Item(callee.Tags, e.To, id, line, LineText(caller, line), depth, [.. path, e.To], e.Resolution));
                }
                if (depth < maxDepth && callee.Kind != NodeKind.External && !path.Contains(e.To))
                {
                    Walk([.. path, e.To], depth + 1);
                }
            }
            LinesBefore(int.MaxValue);
            if (lineHits.Count > 0)
            {
                cut = true;
            }
        }

        if (graph.Nodes.ContainsKey(rootId))
        {
            Walk([rootId], 0);
        }
        var tags = items.SelectMany(i => i.Tags).Distinct().ToList();
        return new Result(items, cut, tags);
    }

    private static string LineText(TraceNode node, int line)
    {
        var lines = node.Code.Split('\n');
        var i = line - node.StartLine;
        return i >= 0 && i < lines.Length ? lines[i].Trim() : "";
    }

    public static string ToText(TraceGraph graph, Result result, string title)
    {
        var sb = new StringBuilder();
        sb.Append("# 흐름 요약: ").Append(title).Append("\n\n");
        if (result.Items.Count == 0)
        {
            sb.Append("표시(tags)가 붙은 호출이 없습니다.\n");
        }
        foreach (var i in result.Items)
        {
            var caller = graph.Nodes[i.CallerId];
            sb.Append(new string(' ', i.Depth * 2)).Append("- [").Append(string.Join(", ", i.Tags)).Append("] ")
              .Append(i.CalleeId.Length > 0 ? graph.Nodes[i.CalleeId].Name : $"{caller.Name} 본문")
              .Append("  (").Append(caller.File).Append(':').Append(i.Line).Append(')');
            if (i.LineText.Length > 0)
            {
                sb.Append("  `").Append(i.LineText.Replace('`', '\'')).Append('`');
            }
            sb.Append('\n');
        }
        if (result.Cut)
        {
            sb.Append("… (개수 제한에서 자름)\n");
        }
        sb.Append("\n## 이 결과가 보여 주지 못하는 것\n\n");
        foreach (var l in Limits)
        {
            sb.Append("- ").Append(l).Append('\n');
        }
        return sb.ToString();
    }
}
