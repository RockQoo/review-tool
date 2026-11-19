using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReviewTool.Core;

namespace ReviewTool.Cpp;

// 정적 호출 그래프가 끊기는 두 곳을 소스 텍스트로 잇는 후처리. clangd 에 파일을 새로 열지 않는다
// (열면 동적 색인이 다른 함수의 호출 기록을 가린다 — CLAUDE.md).
//  1) 디스패처: X.Dispatch(...) 호출 → 같은 파일에서 X.Register(..&C::H) 한 핸들러 H   (Resolution = Registration)
//  2) 가상 함수: 선언만 있는 C::M → C 를 상속한 클래스의 M 정의 위치(구현 후보 목록만, 펼치지는 않음)
internal static partial class LinkPass
{
    public sealed record Registration(string Receiver, string RelativeFile, string HandlerNodeId);

    // 등록 줄에서 받는 쪽 이름: "gatewayDispatcher_.Register(" -> gatewayDispatcher_, "Table().Register(" -> Table()
    public static string? ReceiverBefore(string line, string method)
    {
        var m = Regex.Match(line, $@"([A-Za-z_]\w*(?:\(\))?)\s*(?:\.|->)\s*{Regex.Escape(method)}\s*\(");
        return m.Success ? m.Groups[1].Value : null;
    }

    // "public Network::IPacketHandler, private Base<T>" -> [IPacketHandler, Base]
    internal static IEnumerable<string> BaseNames(string bases)
    {
        var depth = 0;
        var start = 0;
        var parts = new List<string>();
        for (var i = 0; i <= bases.Length; i++)
        {
            if (i == bases.Length || (bases[i] == ',' && depth == 0))
            {
                parts.Add(bases[start..i]);
                start = i + 1;
            }
            else if (bases[i] == '<')
            {
                depth++;
            }
            else if (bases[i] == '>')
            {
                depth--;
            }
        }
        foreach (var raw in parts)
        {
            var p = Regex.Replace(raw, @"<.*>", "", RegexOptions.Singleline);
            p = Regex.Replace(p, @"\b(public|protected|private|virtual)\b", "").Trim();
            var name = p.Split("::").Last().Trim();
            if (Regex.IsMatch(name, @"^[A-Za-z_]\w*$"))
            {
                yield return name;
            }
        }
    }

    // 등록 줄에서 &C::H 앞에 있는 호출의 받는 쪽: "dispatcher_.Register(this, &C::H)" -> dispatcher_
    internal static string? RegistrationReceiver(string line, int handlerColumn)
    {
        var before = line[..Math.Min(handlerColumn, line.Length)];
        var m = Regex.Matches(before, @"([A-Za-z_]\w*(?:\(\))?)\s*(?:\.|->)\s*\w+\s*\(").LastOrDefault();
        return m?.Groups[1].Value;
    }

    public static int LinkDispatchers(TargetConfig config, TraceGraph graph, IReadOnlyList<Registration> registrations,
                                      Func<string, string[]> readLines)
    {
        if (string.IsNullOrWhiteSpace(config.DispatchCall) || registrations.Count == 0)
        {
            return 0;
        }
        var dispatch = new Regex(config.DispatchCall);
        var dispatchEdges = graph.Edges.Where(e => graph.Nodes.TryGetValue(e.To, out var n) && dispatch.IsMatch(n.Name)).ToList();
        var existing = graph.Edges.Select(e => (e.From, e.To)).ToHashSet();
        var linked = 0;
        foreach (var edge in dispatchEdges)
        {
            var method = graph.Nodes[edge.To].Name.Split("::").Last();
            foreach (var site in edge.CallSites)
            {
                var lines = readLines(Path.Combine(config.Root, site.File));
                if (site.Line < 1 || site.Line > lines.Length)
                {
                    continue;
                }
                var receiver = ReceiverBefore(lines[site.Line - 1], method);
                if (receiver is null)
                {
                    continue;
                }
                foreach (var reg in registrations.Where(r => r.Receiver == receiver &&
                                                             string.Equals(r.RelativeFile, site.File, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!existing.Add((edge.From, reg.HandlerNodeId)))
                    {
                        continue;
                    }
                    graph.Edges.Add(new TraceEdge
                    {
                        From = edge.From,
                        To = reg.HandlerNodeId,
                        Resolution = "Registration",
                        CallSites = [new SourceRef { File = site.File, Line = site.Line, Column = site.Column }],
                    });
                    linked++;
                }
            }
        }
        return linked;
    }

    // 선언만 있는 메서드(NoDefinition)마다, 그 클래스를 상속한 클래스의 같은 이름 메서드 정의를 찾는다.
    public static async Task<int> FindImplementationsAsync(TargetConfig config, TraceGraph graph, ClangdSession session,
                                                           Func<string, bool> filter, Func<string, string[]> readLines,
                                                           CancellationToken ct)
    {
        var targets = graph.Nodes.Values
            .Where(n => n.Stop == StopReason.NoDefinition && n.Kind == NodeKind.Method && n.Name.Contains("::"))
            .ToList();
        if (targets.Count == 0)
        {
            return 0;
        }

        // 클래스 → 직접 상속한 클래스들 (소스 텍스트의 class X : public Y)
        var derived = new Dictionary<string, HashSet<string>>();
        foreach (var file in SourceFiles.Enumerate(config.Root, SourceFiles.IsCode, config.Exclude))
        {
            var rel = Path.GetRelativePath(config.Root, file).Replace('\\', '/');
            if (!filter(rel))
            {
                continue;
            }
            foreach (Match m in ClassDecl().Matches(string.Join('\n', readLines(file))))
            {
                foreach (var b in BaseNames(m.Groups["bases"].Value))
                {
                    (derived.TryGetValue(b, out var set) ? set : derived[b] = []).Add(m.Groups["name"].Value);
                }
            }
        }

        var found = 0;
        foreach (var node in targets)
        {
            var parts = node.Name.Split("::");
            var method = parts[^1];
            var cls = parts[^2];
            // 손자 클래스까지
            var classes = new HashSet<string>();
            var queue = new Queue<string>([cls]);
            while (queue.Count > 0)
            {
                foreach (var d in derived.GetValueOrDefault(queue.Dequeue()) ?? [])
                {
                    if (classes.Add(d))
                    {
                        queue.Enqueue(d);
                    }
                }
            }
            foreach (var d in classes.Order())
            {
                JsonArray symbols;
                try
                {
                    symbols = await session.WorkspaceSymbolAsync($"{d}::{method}", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    continue;
                }
                foreach (var s in symbols.OfType<JsonObject>())
                {
                    var container = s["containerName"]?.GetValue<string>() ?? "";
                    if (s["name"]?.GetValue<string>() != method || !(container == d || container.EndsWith("::" + d, StringComparison.Ordinal)))
                    {
                        continue;
                    }
                    var loc = s["location"]!;
                    var path = PathUri.ToPath(loc["uri"]!.GetValue<string>());
                    var line0 = loc["range"]!["start"]!["line"]!.GetValue<int>();
                    var col = loc["range"]!["start"]!["character"]!.GetValue<int>();
                    var rel = Path.GetRelativePath(config.Root, path).Replace('\\', '/');
                    if (rel.StartsWith("..", StringComparison.Ordinal) || !FunctionBody.Find(readLines(path), line0, col).HasBody)
                    {
                        continue;
                    }
                    if (node.Implementations.All(i => i.File != rel || i.Line != line0 + 1))
                    {
                        node.Implementations.Add(new ImplementationRef { Name = $"{d}::{method}", File = rel, Line = line0 + 1 });
                        found++;
                    }
                }
            }
        }
        return found;
    }

    [GeneratedRegex(@"\b(?:class|struct)\s+(?:\w+\s+)*?(?<name>[A-Za-z_]\w*)(?:\s+final)?\s*:(?!:)\s*(?<bases>[^{;]+)\{")]
    private static partial Regex ClassDecl();

}
