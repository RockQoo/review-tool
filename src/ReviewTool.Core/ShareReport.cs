using System.Text;
using System.Text.RegularExpressions;

namespace ReviewTool.Core;

// 공유 보고서: 고른 진입점(또는 전체)의 흐름을 Markdown 한 장으로. 회사 코드를 남에게 보여 줄 때를 대비해
// 소스 본문(코드 블록·줄 텍스트)과 절대 경로를 빼는 옵션이 있다. 기본은 둘 다 뺀다(구조만).
public static class ShareReport
{
    public sealed class Options
    {
        // 비우면 모든 진입점
        public List<string> EntryNodeIds { get; set; } = [];
        // 진입점 함수 코드, 흐름 요약·점검 근거의 줄 텍스트
        public bool IncludeSource { get; set; }
        // 분석 폴더(절대 경로)와 그 밖의 드라이브 경로를 그대로 둘지
        public bool IncludeAbsolutePaths { get; set; }
        public bool Tree { get; set; } = true;
        public bool Journey { get; set; } = true;
        public bool Summary { get; set; } = true;
        public bool Checks { get; set; } = true;
        public int TreeDepth { get; set; } = 3;
        // 진입점을 전부 넣을 때 너무 길어지지 않게
        public int MaxEntries { get; set; } = 60;
    }

    public static string Build(TraceGraph graph, TraceIndex index, Options options, Checks.Report? checks = null)
    {
        var entries = (options.EntryNodeIds.Count == 0
                ? graph.EntryPoints
                : graph.EntryPoints.Where(e => options.EntryNodeIds.Contains(e.NodeId)))
            .Where(e => graph.Nodes.ContainsKey(e.NodeId))
            .GroupBy(e => e.NodeId).Select(g => g.First())
            .ToList();
        var shown = entries.Take(Math.Max(1, options.MaxEntries)).ToList();

        var sb = new StringBuilder();
        sb.Append("# 흐름 보고서: ").Append(graph.TargetName).Append("\n\n");
        sb.Append("- 분석: ").Append(graph.AnalyzedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")).Append(" · 언어 ").Append(graph.Language)
          .Append(" · 진입점 ").Append(graph.EntryPoints.Count).Append(" · 함수 ").Append(graph.Nodes.Count).Append(" · 호출 ").Append(graph.Edges.Count).Append('\n');
        sb.Append("- 이 보고서: 진입점 ").Append(shown.Count).Append("개")
          .Append(entries.Count > shown.Count ? $" (전체 {entries.Count}개 중 앞 {shown.Count}개)" : "")
          .Append(" · 소스 본문 ").Append(options.IncludeSource ? "포함" : "뺌")
          .Append(" · 절대 경로 ").Append(options.IncludeAbsolutePaths ? "포함" : "뺌").Append('\n');
        sb.Append("- 경로는 분석 폴더 기준 상대 경로(`파일:줄`)다.\n");

        if (options.Checks && checks is not null)
        {
            sb.Append("\n## 점검 규칙\n\n| 규칙 | 상태 | 지적 |\n|---|---|---|\n");
            foreach (var r in checks.Runs)
            {
                sb.Append("| ").Append(Cell(r.Rule.Name)).Append(" | ")
                  .Append(r.State switch { RuleState.Off => "꺼짐", RuleState.Error => "오류", _ => "돌았음" }).Append(" | ")
                  .Append(r.State == RuleState.Ran ? r.Count.ToString() : "-").Append(" |\n");
            }
            // 진입점과 무관한 지적(#include 등)은 여기서 한 번만
            var global = checks.Findings.Where(f => f.Entries.Count == 0).ToList();
            if (global.Count > 0)
            {
                sb.Append('\n');
                AppendFindings(sb, global, options.IncludeSource);
            }
        }

        foreach (var entry in shown)
        {
            var node = graph.Nodes[entry.NodeId];
            sb.Append("\n---\n\n## ").Append(entry.Label).Append("  (").Append(entry.Group).Append(")\n\n");
            sb.Append("- 함수: `").Append(node.Name).Append("` ").Append(node.File).Append(':').Append(node.StartLine).Append('\n');
            sb.Append("- 진입점 근거: ").Append(entry.RegisteredAt).Append(" (").Append(entry.Detector).Append(")\n");
            if (options.IncludeSource && node.Code.Length > 0)
            {
                sb.Append("\n```\n").Append(node.Code.TrimEnd()).Append('\n');
                if (node.CodeTruncated)
                {
                    sb.Append("// … 잘림\n");
                }
                sb.Append("```\n");
            }
            if (options.Tree)
            {
                sb.Append("\n### 호출 트리 (깊이 ").Append(options.TreeDepth).Append(", 분석 범위 밖 함수는 뺌)\n\n```\n")
                  .Append(Tree(graph, index, entry.NodeId, options.TreeDepth).TrimEnd()).Append("\n```\n");
            }
            if (options.Journey && graph.Channels.Count > 0)
            {
                var journey = new Journey(graph, index);
                sb.Append('\n').Append(Demote(StripLimits(journey.ToText(journey.Build(entry.NodeId), entry.Label))));
            }
            if (options.Summary)
            {
                var summary = FlowSummary.Build(graph, index, entry.NodeId);
                if (summary.Items.Count > 0)
                {
                    sb.Append("\n### 흐름 요약\n\n");
                    foreach (var i in summary.Items)
                    {
                        var caller = graph.Nodes[i.CallerId];
                        sb.Append(new string(' ', Math.Min(i.Depth, 8) * 2)).Append("- [").Append(string.Join(", ", i.Tags)).Append("] ")
                          .Append(i.CalleeId.Length > 0 ? graph.Nodes[i.CalleeId].Name : $"{caller.Name} 본문")
                          .Append("  (").Append(caller.File).Append(':').Append(i.Line).Append(')');
                        if (options.IncludeSource && i.LineText.Length > 0)
                        {
                            sb.Append("  `").Append(i.LineText.Replace('`', '\'')).Append('`');
                        }
                        sb.Append('\n');
                    }
                }
            }
            if (options.Checks && checks is not null)
            {
                var mine = checks.Findings.Where(f => f.Entries.Contains(entry.NodeId)).ToList();
                if (mine.Count > 0)
                {
                    sb.Append("\n### 점검 지적 ").Append(mine.Count).Append("개\n\n");
                    AppendFindings(sb, mine, options.IncludeSource);
                }
            }
        }

        sb.Append("\n---\n\n## 이 보고서가 증명하지 않는 것\n\n");
        foreach (var l in Limits)
        {
            sb.Append("- ").Append(l).Append('\n');
        }
        var text = sb.ToString();
        return options.IncludeAbsolutePaths ? text : HidePaths(text, graph.RootDirectory);
    }

    // 공유용 트리: 분석 범위 안 함수만, 내부 id 없이. 추정 연결·멈춘 이유·이미 펼친 함수는 표시한다.
    private static string Tree(TraceGraph graph, TraceIndex index, string rootId, int maxDepth, int maxLines = 120)
    {
        var sb = new StringBuilder();
        var lines = 0;
        var expanded = new HashSet<string>();
        void Walk(string id, int depth, TraceEdge? via)
        {
            if (lines >= maxLines)
            {
                return;
            }
            var n = graph.Nodes[id];
            sb.Append(' ', depth * 2).Append("- ").Append(n.Name).Append("  (").Append(n.File).Append(':').Append(n.StartLine).Append(')');
            if (via is not null && TraceQueries.IsGuess(via))
            {
                sb.Append("  [").Append(TraceQueries.ResolutionText(via.Resolution)).Append(']');
            }
            if (n.Stop is { } stop && stop != StopReason.External)
            {
                sb.Append("  [멈춤: ").Append(stop).Append(']');
            }
            lines++;
            if (!expanded.Add(id))
            {
                sb.Append("  (위에서 펼침)\n");
                return;
            }
            sb.Append('\n');
            var children = index.ChildrenOf(id)
                .Where(e => graph.Nodes.TryGetValue(e.To, out var c) && c.Kind != NodeKind.External && !TraceQueries.IsAddressOnly(graph, e))
                .GroupBy(e => e.To).Select(g => g.First()).ToList();
            if (depth >= maxDepth)
            {
                if (children.Count > 0)
                {
                    sb.Append(' ', (depth + 1) * 2).Append("… 호출 ").Append(children.Count).Append("개 더\n");
                    lines++;
                }
                return;
            }
            foreach (var e in children)
            {
                Walk(e.To, depth + 1, e);
            }
        }
        Walk(rootId, 0, null);
        if (lines >= maxLines)
        {
            sb.Append("… (").Append(maxLines).Append("줄에서 자름)\n");
        }
        return sb.ToString();
    }

    public static readonly string[] Limits =
    [
        "정적 분석 결과다. 가상 함수·함수 포인터·실행 중에 정해지는 경로는 빠지거나 \"추정\"으로 표시된다.",
        "패킷 여정·흐름 요약·점검은 이름과 소스 텍스트 규칙으로 만든 추정이다.",
        "지적이 없다는 것은 \"찾지 못했다\"는 뜻이지 \"문제없다\"는 뜻이 아니다.",
    ];

    private static void AppendFindings(StringBuilder sb, IEnumerable<Checks.Finding> findings, bool includeSource)
    {
        foreach (var f in findings.Take(50))
        {
            sb.Append("- **").Append(f.Rule).Append("** ").Append(f.Title).Append(" · 확실도 ").Append(Core.Checks.CertaintyText(f.Certainty))
              .Append(" · ").Append(f.Site).Append('\n');
            foreach (var e in f.Evidence.Take(5))
            {
                sb.Append("  - ").Append(e.Site).Append(' ').Append(e.Note);
                if (includeSource && e.Text.Length > 0)
                {
                    sb.Append("  `").Append(e.Text.Replace('`', '\'')).Append('`');
                }
                sb.Append('\n');
            }
        }
    }

    private static string Cell(string s) => s.Replace('|', '/');

    // 여정 글의 "# 제목" → "### 제목", "## 소제목" → "#### 소제목"(보고서 안에 끼우려고)
    private static string Demote(string text) =>
        Regex.Replace(text, @"^(#{1,4}) ", m => new string('#', m.Groups[1].Length + 2) + " ", RegexOptions.Multiline);

    // 여정 글 끝의 한계 목록은 보고서 끝에 한 번만 둔다
    private static string StripLimits(string text)
    {
        var at = text.IndexOf("\n## 이 결과가 보여 주지 못하는 것", StringComparison.Ordinal);
        return at >= 0 ? text[..(at + 1)] : text;
    }

    // 분석 폴더 → <분석 폴더>, 그 밖의 절대 경로(C:\..., \\server\..., /home/..., /Users/...) → <경로>
    public static string HidePaths(string text, string root)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            var r = root.TrimEnd('\\', '/');
            foreach (var form in new[] { r, r.Replace('\\', '/'), r.Replace('/', '\\') }.Distinct())
            {
                text = text.Replace(form, "<분석 폴더>", StringComparison.OrdinalIgnoreCase);
            }
        }
        return AbsolutePath().Replace(text, "<경로>");
    }

    private static Regex AbsolutePath() => new(
        @"(?<![\w<])(?:[A-Za-z]:[\\/]|\\\\[\w.$-]+\\|/(?:home|Users|mnt|var|opt|tmp)/)[^\s`'""|()<>]*",
        RegexOptions.CultureInvariant);
}
