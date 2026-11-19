using System.Text;

namespace ReviewTool.Core;

// 선택한 흐름(진입점 → … → 함수)을 AI 에게 그대로 붙여 넣을 수 있는 Markdown 으로.
// 경로·코드·호출 근거(파일:줄)·추정 연결·못 본 것을 함께 담는다. 절대 경로는 넣지 않는다.
public static class FlowContext
{
    public sealed class Options
    {
        // false 면 코드 본문을 빼고 구조(이름·파일:줄)만. 공유용.
        public bool IncludeCode { get; init; } = true;
        // 분석 뒤 바뀐 파일(신선도). 있으면 경고로 적는다.
        public IReadOnlyCollection<string>? ChangedFiles { get; init; }
    }

    public static string ToMarkdown(TraceGraph graph, IReadOnlyList<string> path, Options? options = null)
    {
        options ??= new Options();
        var index = new TraceIndex(graph);
        var nodes = path.Where(graph.Nodes.ContainsKey).Select(id => graph.Nodes[id]).ToList();
        var sb = new StringBuilder();
        if (nodes.Count == 0)
        {
            return "(선택한 흐름이 없습니다)\n";
        }

        var entry = graph.EntryPoints.FirstOrDefault(e => e.NodeId == nodes[0].Id);
        sb.Append("# 흐름: ").Append(entry?.Label ?? nodes[0].Name);
        if (nodes.Count > 1)
        {
            sb.Append(" → ").Append(TraceQueries.ShortName(nodes[^1].Name));
        }
        sb.Append("\n\n");
        sb.Append("- 대상: ").Append(graph.TargetName).Append(" (").Append(graph.Language).Append(")\n");
        sb.Append("- 분석 시각: ").Append(graph.AnalyzedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")).Append(" (정적 분석, review-tool)\n");
        if (entry is not null)
        {
            sb.Append("- 진입점: ").Append(entry.Label).Append(" · ").Append(entry.Group)
              .Append(" · 등록 ").Append(entry.RegisteredAt).Append('\n');
        }
        sb.Append("- 경로: ").Append(string.Join(" → ", nodes.Select(n => TraceQueries.ShortName(n.Name)))).Append("\n\n");

        var changed = options.ChangedFiles is { Count: > 0 } c ? new HashSet<string>(c, StringComparer.OrdinalIgnoreCase) : null;
        var fence = graph.Language switch { "cpp" => "cpp", "csharp" => "csharp", _ => "" };
        var notes = new List<string>();

        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            sb.Append("## ").Append(i + 1).Append(". ").Append(n.Name);
            if (n.Kind != NodeKind.External)
            {
                sb.Append("  (").Append(n.File).Append(':').Append(n.StartLine).Append('-').Append(n.EndLine).Append(')');
            }
            sb.Append('\n');

            if (i > 0)
            {
                var edge = index.ChildrenOf(nodes[i - 1].Id).FirstOrDefault(e => e.To == n.Id);
                var lines = index.CallLines(nodes[i - 1].Id, n.Id).ToList();
                sb.Append("호출 근거: ").Append(nodes[i - 1].File).Append(':')
                  .Append(lines.Count > 0 ? string.Join(",", lines) : "?");
                if (edge is not null && TraceQueries.IsGuess(edge))
                {
                    sb.Append(" — **추정 연결**(").Append(edge.Resolution).Append("): ").Append(TraceQueries.ResolutionHint(edge.Resolution));
                }
                sb.Append('\n');
            }
            if (changed?.Contains(n.File) == true)
            {
                sb.Append("> 주의: 이 파일은 분석 뒤 바뀌었다. 아래 코드·줄 번호는 분석 당시 것이다.\n");
            }

            if (n.Kind == NodeKind.External)
            {
                sb.Append("(분석 범위 밖 함수라 코드 없음)\n\n");
                continue;
            }
            if (options.IncludeCode)
            {
                var next = i + 1 < nodes.Count ? index.CallLines(n.Id, nodes[i + 1].Id).ToHashSet() : null;
                sb.Append("```").Append(fence).Append('\n')
                  .Append(TraceQueries.NumberedCode(graph, index, n, next))
                  .Append("```\n");
                if (next is { Count: > 0 })
                {
                    sb.Append("(`>` 표시 줄이 다음 함수를 부르는 곳)\n");
                }
            }
            if (n.Stop is { } stop && stop != StopReason.External)
            {
                notes.Add($"{n.Name}: {TraceQueries.StopText(stop)}");
            }
            if (n.Implementations.Count > 0)
            {
                sb.Append("구현 후보(추정): ")
                  .Append(string.Join(", ", n.Implementations.Select(im => $"{im.Name} ({im.File}:{im.Line})"))).Append('\n');
            }
            sb.Append('\n');
        }

        sb.Append("## 이 자료가 보여 주지 못하는 것\n");
        foreach (var note in notes)
        {
            sb.Append("- ").Append(note).Append('\n');
        }
        foreach (var d in graph.Diagnostics.Take(20))
        {
            sb.Append("- 분석 진단: ").Append(d).Append('\n');
        }
        if (graph.Diagnostics.Count > 20)
        {
            sb.Append("- 분석 진단 ").Append(graph.Diagnostics.Count - 20).Append("개 더 있음\n");
        }
        foreach (var l in Limits)
        {
            sb.Append("- ").Append(l).Append('\n');
        }
        return sb.ToString();
    }

    public static readonly string[] Limits =
    [
        "정적 분석 결과다. 가상 함수·std::function·함수 포인터 너머는 끊기거나 \"추정\"으로만 이어져 있다.",
        "경로에 없는 호출(같은 함수의 다른 분기, 다른 스레드·타이머·다른 프로세스에서 오는 호출)은 여기 없다.",
    ];

    // AI 에게 보낼 질문 전체: 지시 + 문맥 + 질문.
    public static string Prompt(string context, string question) =>
        $"""
        아래는 코드 리뷰 도구(review-tool)가 정적 분석으로 뽑은 서버 코드의 호출 흐름이다.
        이 자료와, 필요하면 현재 폴더의 소스 파일을 읽어서 질문에 답해라. 파일은 고치지 말고 읽기만 해라.
        - 판단마다 근거를 파일:줄로 적는다.
        - "추정 연결"과 정적 호출을 구분하고, 자료로 확인할 수 없는 것은 모른다고 말한다.
        - 한국어로 답한다.

        {context}

        # 질문
        {question}
        """;
}
