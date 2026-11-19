using System.Text.RegularExpressions;

namespace ReviewTool.Core;

// 분석 후처리(언어 무관, 소스 텍스트 기반):
//  1) 메시지 통로(설정 channels)마다 보내는 곳·받는 곳을 찾아 graph.Messages 에 남긴다.
//  2) 같은 프로세스 안 메시지(레인 등)는 보낸 함수 → 받는 핸들러를 호출 선(Resolution = Message)으로 잇는다.
//     다른 프로세스로 가는 패킷은 선으로 잇지 않는다(호출 트리·영향 분석이 프로세스를 넘지 않게). 여정(Journey)이 따로 잇는다.
//  3) 설정 tags 의 이름 규칙으로 함수에 표시를 붙인다.
// 찾은 것은 모두 추정이다. 보내는 곳은 분석한 함수(진입점에서 닿은 함수) 안에서만 찾는다.
public static partial class MessagePass
{
    public sealed record Result(int Sends, int Receives, int Linked, int Tagged);

    private sealed record Compiled(MessageChannel Channel, Regex? Send, Regex? Key, Regex? KeyName, Regex? Receive);

    public static Result Run(TargetConfig config, TraceGraph graph, Func<string, string[]> readLines)
    {
        var channels = config.Channels.Where(c => c.Name.Length > 0).Select(c => new Compiled(c,
            Make(c.Send), Make(c.Key), Make(c.KeyName), Make(c.Receive))).ToList();
        graph.Channels = channels.Select(c => new ChannelInfo { Name = c.Channel.Name, CrossProcess = c.Channel.CrossProcess }).ToList();
        graph.Messages.Clear();
        var codeTags = config.Tags.Where(t => t.Name.Length > 0 && !string.IsNullOrWhiteSpace(t.Code))
            .Select(t => (t.Name, Regex: new Regex(t.Code, RegexOptions.CultureInvariant))).ToList();

        foreach (var node in graph.Nodes.Values)
        {
            node.TagLines = [];
            if (node.Kind == NodeKind.External || node.File.Length == 0)
            {
                continue;
            }
            var body = BodyLines(config, node, readLines);
            foreach (var c in channels)
            {
                ScanBody(c, node, body, graph.Messages);
            }
            node.TagLines = TagLines(codeTags, body);
        }

        // 진입점 표시 이름이 메시지 이름이면 그 진입점은 그 메시지를 받는 곳이다(등록 줄이 근거).
        foreach (var entry in graph.EntryPoints)
        {
            var c = channels.FirstOrDefault(x => x.Channel.Name == entry.Detector)
                    ?? channels.FirstOrDefault(x => x.KeyName?.IsMatch(entry.Label) == true);
            if (c is null)
            {
                continue;
            }
            graph.Messages.Add(new MessageSite
            {
                Channel = c.Channel.Name, Key = entry.Label, Role = MessageRole.Receive, NodeId = entry.NodeId,
                Site = new SourceRef { File = entry.RegisteredAt.File, Line = entry.RegisteredAt.Line, Column = entry.RegisteredAt.Column },
                How = "등록",
            });
        }

        var linked = LinkInProcess(graph);
        var tagged = ApplyTags(config, graph);
        return new Result(graph.Messages.Count(m => m.Role == MessageRole.Send),
                          graph.Messages.Count(m => m.Role == MessageRole.Receive), linked, tagged);
    }

    private static Regex? Make(string pattern) =>
        string.IsNullOrWhiteSpace(pattern) ? null : new Regex(pattern, RegexOptions.CultureInvariant);

    // 본문 줄(1부터 세는 줄 번호와 함께). 파일을 읽을 수 없으면 저장된 코드(잘렸을 수 있음).
    internal static List<(int Line, string Text)> BodyLines(TargetConfig config, TraceNode node, Func<string, string[]> readLines)
    {
        var lines = readLines(Path.Combine(config.Root, node.File));
        var result = new List<(int, string)>();
        if (lines.Length >= node.EndLine && node.StartLine >= 1 && node.EndLine >= node.StartLine)
        {
            for (var l = node.StartLine; l <= node.EndLine; l++)
            {
                result.Add((l, lines[l - 1]));
            }
            return result;
        }
        var code = node.Code.Split('\n');
        for (var i = 0; i < code.Length; i++)
        {
            result.Add((node.StartLine + i, code[i].TrimEnd('\r')));
        }
        return result;
    }

    // 함수 하나의 보내는 곳·받는 곳(테스트용 입구)
    public static List<MessageSite> Scan(MessageChannel channel, TraceNode node, List<(int Line, string Text)> body)
    {
        var output = new List<MessageSite>();
        ScanBody(new Compiled(channel, Make(channel.Send), Make(channel.Key), Make(channel.KeyName), Make(channel.Receive)),
                 node, body, output);
        return output;
    }

    private static void ScanBody(Compiled c, TraceNode node, List<(int Line, string Text)> body, List<MessageSite> output)
    {
        // 시그니처(첫 '{' 앞)는 받은 인자라 보내는 곳·변수 선언으로 보지 않는다
        var code = body.Select(b => StripComment(b.Text)).ToList();
        var open = code.FindIndex(t => t.Contains('{'));
        if (open < 0)
        {
            return;
        }
        var startCol = code[open].IndexOf('{') + 1;
        for (var i = open; i < code.Count; i++)
        {
            var text = i == open ? new string(' ', startCol) + code[i][startCol..] : code[i];
            if (c.Receive is not null)
            {
                foreach (Match m in c.Receive.Matches(text))
                {
                    var key = m.Groups["key"];
                    if (key.Success && Accept(c, key.Value))
                    {
                        output.Add(Site(c, key.Value, MessageRole.Receive, node, body[i].Line, key.Index, "case"));
                    }
                }
            }
            if (c.Send is null)
            {
                continue;
            }
            var send = c.Send.Match(text);
            if (!send.Success)
            {
                continue;
            }
            // 문장 끝(;)까지 최대 8줄 이어 붙인다: SendPacket(\n PacketId::X, ...)
            var statement = text[send.Index..];
            for (var j = i + 1; !statement.Contains(';') && j < code.Count && j <= i + 8; j++)
            {
                statement += " " + code[j];
            }
            var semi = statement.IndexOf(';');
            if (semi >= 0)
            {
                statement = statement[..semi];
            }
            var keys = new List<string>();
            if (c.Key is not null)
            {
                foreach (Match m in c.Key.Matches(statement))
                {
                    var key = m.Groups["key"];
                    if (key.Success && Accept(c, key.Value) && !keys.Contains(key.Value))
                    {
                        keys.Add(key.Value);
                    }
                }
            }
            var how = "직접";
            if (keys.Count == 0 && c.KeyName is not null)
            {
                // 문장에 이름이 없으면: 보낸 변수의 선언(이 함수 본문, 이 줄 앞) 타입이 이름 모양인지
                how = "변수 타입";
                var before = string.Join('\n', code.Skip(open).Take(i - open + 1));
                foreach (Match v in Identifier().Matches(statement[send.Length..]))
                {
                    var type = DeclaredTypes(before, v.Value).LastOrDefault(c.KeyName.IsMatch);
                    if (type is not null && !keys.Contains(type))
                    {
                        keys.Add(type);
                    }
                }
            }
            foreach (var k in keys)
            {
                output.Add(Site(c, k, MessageRole.Send, node, body[i].Line, send.Index, how));
            }
        }
    }

    private static bool Accept(Compiled c, string key) => c.KeyName is null || c.KeyName.IsMatch(key);

    private static MessageSite Site(Compiled c, string key, MessageRole role, TraceNode node, int line, int column, string how) => new()
    {
        Channel = c.Channel.Name, Key = key, Role = role, NodeId = node.Id,
        Site = new SourceRef { File = node.File, Line = line, Column = column },
        How = how,
    };

    // "Common::W2ZLeaveZone leave{}" 처럼 변수 이름 바로 앞에 오는 단어들(타입 후보, 마지막 :: 뒤). 순서대로.
    // "return leave;" 의 return 같은 것도 섞이므로 부르는 쪽이 이름 모양으로 거른다.
    internal static IEnumerable<string> DeclaredTypes(string text, string variable) =>
        Regex.Matches(text, $@"(?<type>[A-Za-z_]\w*)\s*[&*]?\s+{Regex.Escape(variable)}\s*(?:[{{=;(,)\[]|$)", RegexOptions.Multiline)
             .Select(m => m.Groups["type"].Value);

    private static string StripComment(string line)
    {
        // 문자열 안의 // 는 드물어 단순하게 자른다(보내는 줄을 하나 놓칠 수는 있다)
        var i = line.IndexOf("//", StringComparison.Ordinal);
        return i >= 0 ? line[..i] : line;
    }

    [GeneratedRegex(@"\b[a-z_]\w*\b")]
    private static partial Regex Identifier();

    // 같은 프로세스 안 메시지: 보낸 함수 → 같은 이름을 받는 함수. 받는 곳이 여러 프로세스에 있으면 보낸 쪽과 같은 프로세스만.
    private static int LinkInProcess(TraceGraph graph)
    {
        var inProcess = graph.Channels.Where(c => !c.CrossProcess).Select(c => c.Name).ToHashSet();
        if (inProcess.Count == 0)
        {
            return 0;
        }
        var processes = new ProcessMap(graph);
        var receivers = graph.Messages.Where(m => m.Role == MessageRole.Receive && inProcess.Contains(m.Channel))
            .GroupBy(m => (m.Channel, m.Key)).ToDictionary(g => g.Key, g => g.Select(m => m.NodeId).Distinct().ToList());
        var existing = graph.Edges.Select(e => (e.From, e.To)).ToHashSet();
        var linked = 0;
        foreach (var send in graph.Messages.Where(m => m.Role == MessageRole.Send && inProcess.Contains(m.Channel)).ToList())
        {
            if (!receivers.TryGetValue((send.Channel, send.Key), out var targets))
            {
                continue;
            }
            var from = processes.Of(send.NodeId);
            var same = targets.Where(t => processes.Of(t) == from).ToList();
            foreach (var to in same.Count > 0 ? same : targets)
            {
                var edge = graph.Edges.FirstOrDefault(e => e.From == send.NodeId && e.To == to && e.Resolution == "Message");
                if (edge is null)
                {
                    if (!existing.Add((send.NodeId, to)))
                    {
                        continue;   // 이미 정적 호출로 이어져 있음
                    }
                    edge = new TraceEdge { From = send.NodeId, To = to, Resolution = "Message" };
                    graph.Edges.Add(edge);
                    linked++;
                }
                if (!edge.CallSites.Any(s => s.Line == send.Site.Line))
                {
                    edge.CallSites.Add(new SourceRef { File = send.Site.File, Line = send.Site.Line, Column = send.Site.Column });
                }
            }
        }
        return linked;
    }

    // 본문 규칙(tags[].code)에 걸린 줄. 첫 줄(시그니처)과 주석은 보지 않는다.
    internal static List<TaggedLine> TagLines(List<(string Name, Regex Regex)> rules, List<(int Line, string Text)> body)
    {
        var result = new List<TaggedLine>();
        if (rules.Count == 0)
        {
            return result;
        }
        foreach (var (line, raw) in body.Skip(1))
        {
            var text = StripComment(raw);
            var tags = rules.Where(r => r.Regex.IsMatch(text)).Select(r => r.Name).ToList();
            if (tags.Count > 0)
            {
                result.Add(new TaggedLine { Line = line, Tags = tags });
            }
        }
        return result;
    }

    private static int ApplyTags(TargetConfig config, TraceGraph graph)
    {
        var rules = config.Tags.Where(t => t.Name.Length > 0 && t.Pattern.Length > 0)
            .Select(t => (t.Name, Regex: new Regex(t.Pattern, RegexOptions.CultureInvariant))).ToList();
        var tagged = 0;
        foreach (var node in graph.Nodes.Values)
        {
            node.Tags = rules.Where(r => r.Regex.IsMatch(node.Name)).Select(r => r.Name).ToList();
            if (node.Tags.Count > 0 || node.TagLines.Count > 0)
            {
                tagged++;
            }
        }
        return tagged;
    }
}
