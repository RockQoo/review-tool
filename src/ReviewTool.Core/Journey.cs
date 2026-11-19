using System.Text;

namespace ReviewTool.Core;

public enum JourneyKind
{
    Station,    // 메시지를 받는 곳(진입점, case 줄, 네트워크 수신 함수)
    Origin,     // 더 거슬러 올라갈 수 없음(보낸 쪽이 분석 범위 밖이거나 못 찾음)
    Exit,       // 보냈지만 받는 곳을 못 찾음(클라이언트 등 범위 밖)
    Repeat,     // 위에서 이미 펼친 곳
    Cut,        // 깊이·개수 제한
}

// 여정 트리의 한 칸. 부모와 어떻게 이어지는지(Link*)를 함께 담는다.
//  거슬러 올라가기: 부모 ← 자식. 자식(앞 단계)의 Path 끝 함수가 LinkKey 를 보냈고, 그게 부모에 도착했다.
//  따라 내려가기:   부모 → 자식. 부모의 Path 끝 함수가 LinkKey 를 보냈고, 자식이 받았다.
//  LinkKey 가 "" 이면 보낸 것이 아니라 같은 프로세스 안 호출로 이어진 것(중계 봉투를 연 뒤 안쪽 핸들러로).
public sealed class JourneyNode
{
    public JourneyKind Kind { get; init; }
    public string Key { get; init; } = "";             // 이 곳이 받은 메시지. "" = 이름 없이 받는 함수(OnPacket 같은 네트워크 수신)
    public string Channel { get; init; } = "";
    public string NodeId { get; init; } = "";          // 받는 함수
    public string Process { get; init; } = "";
    public SourceRef? ReceivedAt { get; init; }
    public string ReceivedHow { get; init; } = "";

    public string LinkKey { get; init; } = "";
    public string? SenderId { get; init; }
    public SourceRef? SentAt { get; init; }
    public string SentHow { get; init; } = "";
    public string SenderProcess { get; init; } = "";
    // 같은 프로세스 안 호출 경로(코드 스택으로 연다). 첫 함수부터 보낸 함수(또는 도착 함수)까지.
    public IReadOnlyList<string> Path { get; init; } = [];

    public List<JourneyNode> Children { get; } = [];
}

public sealed record JourneyResult(JourneyNode Upstream, JourneyNode Downstream, bool HasMessageData, int Count);

// 패킷 여정: 보낸 곳(이름 X) ↔ X 를 받는 곳을 이어 프로세스를 넘는 흐름을 만든다. 같은 프로세스 안은 호출 선을 따라간다.
public sealed class Journey
{
    public const int MaxDepth = 6;
    public const int MaxNodes = 300;

    public static readonly string[] Limits =
    [
        "보내는 곳·받는 곳은 소스 텍스트(설정 channels 의 정규식)로 찾은 추정입니다.",
        "보내는 곳은 분석한 함수(진입점에서 닿은 함수) 안에서만 찾습니다. 흐름 밖 콜백·타이머가 보내는 것은 빠질 수 있습니다.",
        "실행 중에 정해지는 이름(중계 봉투 안의 패킷 id 등)은 이름으로 잇지 못합니다. 그런 곳은 \"안쪽 호출로 이어짐\"으로 보입니다.",
    ];

    private sealed record Rp(string NodeId, MessageSite? Site)
    {
        public string Key => Site?.Key ?? "";
        public string Id => NodeId + "|" + Key;
    }

    private readonly TraceGraph graph_;
    private readonly TraceIndex index_;
    private readonly ProcessMap processes_;
    private readonly HashSet<string> cross_;
    private readonly Dictionary<string, List<MessageSite>> receivesByNode_;
    private readonly Dictionary<string, List<MessageSite>> sendsByNode_;
    private readonly Dictionary<string, List<MessageSite>> sendsByKey_;
    private readonly Dictionary<string, List<MessageSite>> receiversByKey_;
    private readonly HashSet<string> entryNodes_;
    private int count_;

    public Journey(TraceGraph graph, TraceIndex index)
    {
        graph_ = graph;
        index_ = index;
        processes_ = new ProcessMap(graph);
        cross_ = graph.Channels.Where(c => c.CrossProcess).Select(c => c.Name).ToHashSet();
        var messages = graph.Messages.Where(m => cross_.Contains(m.Channel) && graph.Nodes.ContainsKey(m.NodeId)).ToList();
        receivesByNode_ = messages.Where(m => m.Role == MessageRole.Receive).GroupBy(m => m.NodeId).ToDictionary(g => g.Key, g => g.ToList());
        sendsByNode_ = messages.Where(m => m.Role == MessageRole.Send).GroupBy(m => m.NodeId).ToDictionary(g => g.Key, g => g.ToList());
        sendsByKey_ = messages.Where(m => m.Role == MessageRole.Send).GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.ToList());
        receiversByKey_ = messages.Where(m => m.Role == MessageRole.Receive).GroupBy(m => m.Key)
            .ToDictionary(g => g.Key, g => g.GroupBy(m => m.NodeId).Select(x => x.First()).ToList());
        entryNodes_ = graph.EntryPoints.Select(e => e.NodeId).ToHashSet();
    }

    public ProcessMap Processes => processes_;

    // 진입점(함수) 하나의 여정
    public JourneyResult Build(string nodeId)
    {
        count_ = 0;
        var root = new Rp(nodeId, RootSite(nodeId));
        var up = BuildUp(root, null, [], 0, []);
        var down = BuildDown(root, null, [], 0, []);
        return new JourneyResult(up, down, graph_.Channels.Count > 0, count_);
    }

    // 진입점 자체로 받는 메시지(등록 줄). 없으면 함수 전체(이름 없음).
    private MessageSite? RootSite(string nodeId) =>
        receivesByNode_.GetValueOrDefault(nodeId)?.FirstOrDefault(m => m.How == "등록");

    // 진입점인데 실제로 부르는 곳이 없다(주소만 넘긴 등록은 부르는 것이 아님): 네트워크 수신 함수, main 등
    private bool IsRaw(string nodeId) =>
        entryNodes_.Contains(nodeId) && !(index_.Callers.GetValueOrDefault(nodeId) ?? []).Any(c => Calls(c, nodeId));

    private bool IsStation(string nodeId) => receivesByNode_.ContainsKey(nodeId) || IsRaw(nodeId);

    // 이 함수가 line 줄을 지날 때 받은 메시지: 그 줄 앞의 가장 가까운 case, 없으면 등록, 없으면 이름 없음
    private MessageSite? SiteAt(string nodeId, int line)
    {
        var sites = receivesByNode_.GetValueOrDefault(nodeId);
        if (sites is null)
        {
            return null;
        }
        var file = graph_.Nodes[nodeId].File;
        var cases = sites.Where(s => s.How == "case" && s.Site.File == file).ToList();
        return cases.Where(s => s.Site.Line <= line).MaxBy(s => s.Site.Line)
               ?? sites.FirstOrDefault(s => s.How == "등록")
               ?? (cases.Count == 1 ? cases[0] : null);
    }

    // case 로 받은 곳이면 그 case 부터 다음 case 앞까지만 그 메시지의 처리로 본다
    private (int From, int To)? CaseRange(Rp rp)
    {
        if (rp.Site is not { How: "case" } site)
        {
            return null;
        }
        var next = receivesByNode_[rp.NodeId].Where(s => s.How == "case" && s.Site.File == site.Site.File && s.Site.Line > site.Site.Line)
            .Select(s => s.Site.Line).DefaultIfEmpty(int.MaxValue).Min();
        return (site.Site.Line, next - 1);
    }

    private int FirstCallLine(string from, string to) => index_.CallLines(from, to).DefaultIfEmpty(int.MaxValue).First();

    // start 를 부르는 쪽으로 올라가며 처음 만나는 받는 곳들(그 위로는 다른 메시지의 처리라 멈춘다)
    private List<(Rp Rp, List<string> Path)> Above(string start)
    {
        var next = new Dictionary<string, string?> { [start] = null };
        var queue = new Queue<string>([start]);
        var result = new List<(Rp, List<string>)>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (id != start && IsStation(id))
            {
                var path = new List<string>();
                for (string? cur = id; cur is not null; cur = next[cur])
                {
                    path.Add(cur);
                }
                result.Add((new Rp(id, SiteAt(id, FirstCallLine(id, path[1]))), path));
                continue;
            }
            foreach (var caller in index_.Callers.GetValueOrDefault(id) ?? [])
            {
                if (Calls(caller, id) && next.TryAdd(caller, id))
                {
                    queue.Enqueue(caller);
                }
            }
        }
        return result;
    }

    // 함수 주소만 넘긴 선(핸들러 등록)은 실행 흐름이 아니라 따라가지 않는다
    private readonly Dictionary<(string, string), bool> calls_ = [];

    private bool Calls(string from, string to)
    {
        if (!calls_.TryGetValue((from, to), out var yes))
        {
            yes = calls_[(from, to)] = index_.ChildrenOf(from).Any(e => e.To == to && !TraceQueries.IsAddressOnly(graph_, e));
        }
        return yes;
    }

    private List<(Rp Rp, List<string> Path)> AboveOrSelf(string nodeId, int line) =>
        IsStation(nodeId) ? [(new Rp(nodeId, SiteAt(nodeId, line)), [nodeId])] : Above(nodeId);

    // rp 에서 닿는 함수들(같은 프로세스 안 호출 선). parent 로 경로를 되짚는다.
    private (List<string> Order, Dictionary<string, string?> Parent) Reach(Rp rp)
    {
        var range = CaseRange(rp);
        var parent = new Dictionary<string, string?> { [rp.NodeId] = null };
        var order = new List<string>();
        var queue = new Queue<string>([rp.NodeId]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            order.Add(id);
            foreach (var e in index_.ChildrenOf(id))
            {
                if (id == rp.NodeId && range is { } r && !e.CallSites.Any(s => s.Line >= r.From && s.Line <= r.To))
                {
                    continue;
                }
                if (!parent.ContainsKey(e.To) && Calls(id, e.To) && parent.TryAdd(e.To, id))
                {
                    queue.Enqueue(e.To);
                }
            }
        }
        return (order, parent);
    }

    private static List<string> PathTo(Dictionary<string, string?> parent, string id)
    {
        var path = new List<string>();
        for (string? cur = id; cur is not null; cur = parent[cur])
        {
            path.Add(cur);
        }
        path.Reverse();
        return path;
    }

    private JourneyNode Station(Rp rp, LinkInfo? info) => new()
    {
        Kind = JourneyKind.Station,
        Key = rp.Key,
        Channel = rp.Site?.Channel ?? "",
        NodeId = rp.NodeId,
        Process = processes_.Of(rp.NodeId),
        ReceivedAt = rp.Site?.Site,
        ReceivedHow = rp.Site?.How ?? "",
        LinkKey = info?.Key ?? "",
        SenderId = info?.Sender?.NodeId,
        SentAt = info?.Sender?.Site,
        SentHow = info?.Sender?.How ?? "",
        SenderProcess = info?.Sender is { } s ? processes_.Of(s.NodeId) : "",
        Path = info?.Path ?? [],
    };

    private sealed record LinkInfo(string Key, MessageSite? Sender, IReadOnlyList<string> Path);

    private JourneyNode Marker(JourneyKind kind, Rp? rp, LinkInfo? info) => new()
    {
        Kind = kind,
        Key = rp?.Key ?? "",
        NodeId = rp?.NodeId ?? info?.Sender?.NodeId ?? "",
        Process = rp is not null ? processes_.Of(rp.NodeId) : "",
        LinkKey = info?.Key ?? "",
        SenderId = info?.Sender?.NodeId,
        SentAt = info?.Sender?.Site,
        SentHow = info?.Sender?.How ?? "",
        SenderProcess = info?.Sender is { } s ? processes_.Of(s.NodeId) : "",
        Path = info?.Path ?? [],
    };

    private JourneyNode BuildUp(Rp rp, LinkInfo? info, HashSet<string> onPath, int depth, HashSet<string> expanded)
    {
        count_++;
        var station = Station(rp, info);
        if (!expanded.Add(rp.Id))
        {
            return Marker(JourneyKind.Repeat, rp, info);
        }
        if (depth >= MaxDepth || count_ >= MaxNodes)
        {
            station.Children.Add(Marker(JourneyKind.Cut, null, null));
            return station;
        }
        // 1) 이 이름을 직접 보낸 곳(클라이언트 도구·다른 프로세스)
        var senders = rp.Key.Length > 0 ? sendsByKey_.GetValueOrDefault(rp.Key) ?? [] : [];
        foreach (var s in senders.GroupBy(s => s.NodeId).Select(g => g.First()).Take(12))
        {
            foreach (var (p, path) in AboveOrSelf(s.NodeId, s.Site.Line))
            {
                var childInfo = new LinkInfo(rp.Key, s, path);
                station.Children.Add(onPath.Contains(p.Id)
                    ? Marker(JourneyKind.Repeat, p, childInfo)
                    : BuildUp(p, childInfo, [.. onPath, rp.Id], depth + 1, expanded));
            }
        }
        // 2) 같은 프로세스에서 이 함수를 부르는 받는 곳(중계 봉투를 연 곳 등). 보낸 곳이 따로 있으면
        //    이름 있는 받는 곳만(이름 없는 네트워크 수신 함수까지 늘어놓으면 같은 길이 두 번 보인다).
        foreach (var (p, path) in Above(rp.NodeId))
        {
            if (senders.Count > 0 && p.Key.Length == 0)
            {
                continue;
            }
            var childInfo = new LinkInfo("", null, path);
            station.Children.Add(onPath.Contains(p.Id)
                ? Marker(JourneyKind.Repeat, p, childInfo)
                : BuildUp(p, childInfo, [.. onPath, rp.Id], depth + 1, expanded));
        }
        if (station.Children.Count == 0)
        {
            station.Children.Add(Marker(JourneyKind.Origin, rp, null));
        }
        return station;
    }

    private JourneyNode BuildDown(Rp rp, LinkInfo? info, HashSet<string> onPath, int depth, HashSet<string> expanded)
    {
        count_++;
        var station = Station(rp, info);
        if (!expanded.Add(rp.Id))
        {
            return Marker(JourneyKind.Repeat, rp, info);
        }
        if (depth >= MaxDepth || count_ >= MaxNodes)
        {
            station.Children.Add(Marker(JourneyKind.Cut, null, null));
            return station;
        }
        var (order, parent) = Reach(rp);
        var range = CaseRange(rp);
        var sends = new List<MessageSite>();
        foreach (var id in order)
        {
            foreach (var s in sendsByNode_.GetValueOrDefault(id) ?? [])
            {
                if (id == rp.NodeId && range is { } r && (s.Site.Line < r.From || s.Site.Line > r.To))
                {
                    continue;
                }
                if (!sends.Any(x => x.NodeId == s.NodeId && x.Key == s.Key))
                {
                    sends.Add(s);
                }
            }
        }
        foreach (var s in sends.Take(30))
        {
            var path = PathTo(parent, s.NodeId);
            var childInfo = new LinkInfo(s.Key, s, path);
            var receivers = receiversByKey_.GetValueOrDefault(s.Key) ?? [];
            if (receivers.Count == 0)
            {
                count_++;
                station.Children.Add(Marker(JourneyKind.Exit, null, childInfo));
                continue;
            }
            foreach (var r in receivers)
            {
                var next = new Rp(r.NodeId, r);
                station.Children.Add(onPath.Contains(next.Id)
                    ? Marker(JourneyKind.Repeat, next, childInfo)
                    : BuildDown(next, childInfo, [.. onPath, rp.Id], depth + 1, expanded));
            }
        }
        return station;
    }

    // ---- 문장 ----

    public string StationText(JourneyNode n)
    {
        var name = graph_.Nodes.TryGetValue(n.NodeId, out var node) ? node.Name : n.NodeId;
        var where = n.ReceivedAt is { } at ? $" ({at}{(n.ReceivedHow.Length > 0 ? ", " + n.ReceivedHow : "")})" : "";
        var what = n.Key.Length > 0 ? $"{n.Key} 받음" : IsRaw(n.NodeId) ? "이름 없는 진입점(네트워크 수신 함수·main 등)" : "함수 전체";
        return $"{Proc(n.Process)}{name} — {what}{where}";
    }

    public string LinkText(JourneyNode n)
    {
        if (n.SenderId is null)
        {
            return n.Path.Count > 1 ? $"안쪽 호출로 이어짐: {PathText(n.Path)}" : "";
        }
        var sender = graph_.Nodes.TryGetValue(n.SenderId, out var s) ? s.Name : n.SenderId;
        var how = n.SentHow == "변수 타입" ? ", 변수 타입으로 추정" : "";
        return $"{Proc(n.SenderProcess)}{sender} 가 {n.LinkKey} 를 보냄 ({n.SentAt}{how})";
    }

    public string PathText(IReadOnlyList<string> path) =>
        string.Join(" → ", path.Select(id => graph_.Nodes.TryGetValue(id, out var x) ? TraceQueries.ShortName(x.Name) : "?"));

    private string Proc(string process) => processes_.Names.Count == 0 ? "" : $"[{ProcessMap.Display(process)}] ";

    public static string OriginText(JourneyNode n) => n.Key.Length > 0
        ? $"{n.Key} 를 보내는 곳을 분석한 함수 안에서 찾지 못함 (클라이언트·툴·다른 저장소이거나 흐름 밖 함수)"
        : "더 거슬러 올라갈 수 없음 (이름 없는 진입점: 네트워크 수신 함수면 보낸 쪽은 분석 범위 밖, main 이면 시작점)";

    public string ExitText(JourneyNode n)
    {
        var sender = n.SenderId is not null && graph_.Nodes.TryGetValue(n.SenderId, out var s) ? s.Name : "?";
        return $"{Proc(n.SenderProcess)}{sender} 가 {n.LinkKey} 를 보냄 ({n.SentAt}) → 받는 곳을 찾지 못함 (클라이언트 등 분석 범위 밖)";
    }

    // MCP·문맥 복사용 텍스트
    public string ToText(JourneyResult result, string title)
    {
        var sb = new StringBuilder();
        sb.Append("# 패킷 여정: ").Append(title).Append("\n\n");
        if (!result.HasMessageData)
        {
            sb.Append("이 분석 결과에는 메시지 정보가 없습니다. 재분석하면 여정이 보입니다.\n");
            return sb.ToString();
        }
        sb.Append("## 여기까지 오는 길 (아래로 갈수록 앞 단계)\n\n");
        Write(sb, result.Upstream, 0, up: true);
        sb.Append("\n## 여기서 나가는 길\n\n");
        Write(sb, result.Downstream, 0, up: false);
        sb.Append("\n## 이 결과가 보여 주지 못하는 것\n\n");
        foreach (var l in Limits)
        {
            sb.Append("- ").Append(l).Append('\n');
        }
        return sb.ToString();
    }

    private void Write(StringBuilder sb, JourneyNode n, int depth, bool up)
    {
        var pad = new string(' ', depth * 2);
        switch (n.Kind)
        {
            case JourneyKind.Station:
                sb.Append(pad).Append("- ").Append(StationText(n)).Append('\n');
                if (LinkText(n) is { Length: > 0 } link)
                {
                    sb.Append(pad).Append("  ").Append(up ? "↑ " : "↓ ").Append(link).Append('\n');
                }
                break;
            case JourneyKind.Origin:
                sb.Append(pad).Append("- (시작) ").Append(OriginText(n)).Append('\n');
                break;
            case JourneyKind.Exit:
                sb.Append(pad).Append("- (끝) ").Append(ExitText(n)).Append('\n');
                break;
            case JourneyKind.Repeat:
                sb.Append(pad).Append("- ").Append(StationText(n)).Append(" (위에서 펼침)\n");
                break;
            case JourneyKind.Cut:
                sb.Append(pad).Append("- … (깊이·개수 제한에서 자름)\n");
                break;
        }
        foreach (var c in n.Children)
        {
            Write(sb, c, depth + 1, up);
        }
    }
}
