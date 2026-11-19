using System.Text;
using System.Text.RegularExpressions;

namespace ReviewTool.Core;

// 점검 규칙 하나. 종류(kind)마다 쓰는 칸이 다르다. 정규식은 함수 한정 이름(Ns::Class::Method)이나 본문 줄에 건다.
//  forbidInScope : 본문에서 scopeOpen 이 걸린 줄부터 그 블록 끝(또는 scopeClose)까지 forbid 에 맞는 호출이 있으면 지적
//                  (예: 락 안에서 I/O). depth 단계 아래 함수까지 본다.
//  requireWithin : 진입점에서 subject 함수까지 within 함수(또는 withinEdges 연결, withinEntry 진입점)를 거치지 않고 닿는 경로
//                  (예: 작업 단위 밖 모델 변경, 레인 밖 게임 상태 접근)
//  order         : 한 흐름에서 first 를 부른 뒤 then 을 부르는데 unless 가 흐름 어디에도 없음(예: 재화 차감 뒤 지급, 되돌리기 없음)
//  dependency    : from 파일(glob)의 함수가 to 파일의 함수를 부르거나 #include 함(예: 공용 라이브러리 → 콘텐츠)
public sealed class CheckRule
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Description { get; set; } = "";

    // forbidInScope
    public string ScopeOpen { get; set; } = "";
    public string ScopeClose { get; set; } = "";
    // "block"(여는 줄이 있는 블록 끝까지) 또는 "statement"(그 문장만: ref.Write()->Foo() 같은 임시 잠금)
    public string Scope { get; set; } = "block";
    public string Forbid { get; set; } = "";
    public List<string> ForbidTags { get; set; } = [];
    public int Depth { get; set; } = 2;

    // requireWithin
    public string Subject { get; set; } = "";
    public string Within { get; set; } = "";
    public string WithinEntry { get; set; } = "";       // 진입점 표시 이름·묶음·찾은 규칙 이름에 건다
    public List<string> WithinEdges { get; set; } = []; // 이 연결(Message, Registration)을 지나면 안으로 들어간 것

    // order
    public string First { get; set; } = "";
    public string Then { get; set; } = "";
    public string Unless { get; set; } = "";

    // dependency
    public List<string> From { get; set; } = [];
    public List<string> To { get; set; } = [];

    public static List<CheckRule> Defaults() =>
    [
        new()
        {
            Name = "락 안에서 I/O", Kind = Checks.ForbidInScope,
            Description = "뮤텍스를 잡은 채 네트워크·파일·DB·대기를 하면 다른 스레드가 그만큼 멈춥니다.",
            // m.lock(); 은 문장 맨 앞일 때만(if (auto s = weak.lock()) 같은 weak_ptr 는 잠금이 아니다). C# 은 lock (x) { } 문.
            ScopeOpen = @"\b(?:std::)?(?:lock_guard|unique_lock|scoped_lock|shared_lock)\b|^\s*(?:[\w\[\]]+(?:\.|->))+lock\s*\(\s*\)\s*;|^\s*lock\s*\(",
            ScopeClose = @"\.unlock\s*\(\s*\)",
            Forbid = @"(?i)(^|::)(send\w*|async_\w+|write\w*|read\w*|recv\w*|query\w*|execute\w*|connect\w*|flush\w*|fopen|fwrite|fread|fprintf|sleep\w*|wait\w*)$",
            ForbidTags = ["DB", "네트워크"],
        },
        new()
        {
            Name = "차감 뒤 지급(되돌리기 없음)", Kind = Checks.Order, Enabled = false,
            Description = "재화를 먼저 빼고 보상을 주는데, 지급이 실패했을 때 되돌리는 호출이 흐름에 없으면 재화만 사라집니다.",
            First = @"(?i)(^|::)\w*(consume|deduct|spend|subtract|withdraw|pay)\w*$",
            Then = @"(?i)(^|::)\w*(give|grant|reward|deposit|additem|addgold|addmoney)\w*$",
            Unless = @"(?i)rollback|refund|restore|revert|compensat|transaction|unitofwork",
        },
        new()
        {
            Name = "작업 단위 밖 모델 변경", Kind = Checks.RequireWithin, Enabled = false,
            Description = "모델(저장 대상)을 바꾸는 함수는 작업 단위(UnitOfWork·트랜잭션)를 거쳐서만 불려야 합니다.",
            Subject = @"(?i)model\w*::(set|update|add|remove|delete)\w*$",
            Within = @"(?i)unitofwork|transaction",
        },
        new()
        {
            Name = "레인 밖 게임 상태 접근", Kind = Checks.RequireWithin, Enabled = false,
            Description = "게임 상태는 레인(전용 스레드)에서만 바꿔야 합니다. 메시지 연결을 지나면 레인 안으로 봅니다.",
            Subject = @"(?i)(world|zone|game)state::\w+$",
            Within = @"(?i)lane|strand",
            WithinEdges = ["Message"],
        },
        new()
        {
            Name = "공용 코드가 콘텐츠에 의존", Kind = Checks.Dependency, Enabled = false,
            Description = "공용 라이브러리는 콘텐츠(게임 규칙) 코드를 부르거나 포함하면 안 됩니다.",
            From = ["**/Common/**", "**/Core/**"],
            To = ["**/Content/**", "**/Contents/**"],
        },
    ];
}

public enum Certainty
{
    High,
    Medium,
    Low,
}

public enum RuleState
{
    Ran,
    Off,
    Error,
}

// 점검: 저장된 분석 결과와 대상 설정의 checks 로 그때그때 돈다(재분석 없이 규칙을 고쳐 다시 볼 수 있게).
public static class Checks
{
    public const string ForbidInScope = "forbidInScope";
    public const string RequireWithin = "requireWithin";
    public const string Order = "order";
    public const string Dependency = "dependency";

    public const int MaxFindingsPerRule = 200;

    public sealed record Evidence(SourceRef Site, string Text, string Note);

    // Path: 가장 가까운 진입점 → … → 문제 함수(찾지 못하면 그 함수 하나). 화면은 이것을 코드 스택으로 연다.
    // Entries: 이 지적에 닿는 진입점 함수 id 전부(진입점으로 거를 때 쓴다). 비어 있으면 진입점과 무관(#include 등).
    public sealed record Finding(string Rule, string Kind, Certainty Certainty, string Why, string Title, string Message,
                                 string NodeId, SourceRef Site, IReadOnlyList<Evidence> Evidence, IReadOnlyList<string> Path)
    {
        public IReadOnlyList<string> Entries { get; init; } = [];
    }

    public sealed record RuleRun(CheckRule Rule, RuleState State, string Note, int Count, bool Cut);

    public sealed record Report(IReadOnlyList<RuleRun> Runs, IReadOnlyList<Finding> Findings);

    public static readonly string[] Limits =
    [
        "규칙은 함수 이름과 소스 텍스트로 거는 추정입니다. 이름이 규칙과 다르면 빠지고, 이름만 비슷하면 잘못 걸립니다.",
        "분석에서 펼친 함수(호출 트리)만 봅니다. 람다 안 호출, 깊이·개수 제한 너머는 보지 않습니다.",
        "순서는 소스에 적힌 순서입니다. 분기·예외·비동기 실행 순서는 따지지 않습니다.",
        "지적이 없다는 것은 \"찾지 못했다\"는 뜻이지 \"규칙을 지킨다\"는 뜻이 아닙니다. 꺼진 규칙은 돌지 않았습니다.",
    ];

    public static string KindText(string kind) => kind switch
    {
        ForbidInScope => "범위 안 금지",
        RequireWithin => "안에서만",
        Order => "순서",
        Dependency => "의존 금지",
        _ => kind,
    };

    public static string CertaintyText(Certainty c) => c switch
    {
        Certainty.High => "높음",
        Certainty.Medium => "보통",
        _ => "낮음",
    };

    // 루트 기준 상대 경로로 소스를 읽는다(#include 확인용). 루트 밖이나 읽지 못하면 null.
    public static Func<string, string[]?> ReadFrom(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return relative =>
        {
            try
            {
                var path = Path.GetFullPath(Path.Combine(full, relative));
                return path.StartsWith(full, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? File.ReadAllLines(path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        };
    }

    public static Report Run(IReadOnlyList<CheckRule> rules, TraceGraph graph, TraceIndex index, Func<string, string[]?> readLines)
    {
        var context = new Context(graph, index, readLines);
        var runs = new List<RuleRun>();
        var findings = new List<Finding>();
        foreach (var rule in rules)
        {
            if (!rule.Enabled)
            {
                runs.Add(new RuleRun(rule, RuleState.Off, "꺼짐(설정 checks 에서 enabled: true 로 켭니다)", 0, false));
                continue;
            }
            try
            {
                var (found, note) = rule.Kind switch
                {
                    ForbidInScope => RunForbidInScope(rule, context),
                    RequireWithin => RunRequireWithin(rule, context),
                    Order => RunOrder(rule, context),
                    Dependency => RunDependency(rule, context),
                    _ => throw new RuleError($"모르는 종류(kind): \"{rule.Kind}\". {ForbidInScope}·{RequireWithin}·{Order}·{Dependency} 중 하나입니다."),
                };
                var cut = found.Count > MaxFindingsPerRule;
                // 진입점을 따로 정하지 않은 지적은 그 함수까지 닿는 진입점 전부(대표 경로 하나만으로 거르지 않게)
                findings.AddRange(found.Take(MaxFindingsPerRule).Select(f =>
                    f.Entries.Count > 0 || f.NodeId.Length == 0 ? f : f with { Entries = context.EntriesReaching(f.NodeId) }));
                runs.Add(new RuleRun(rule, RuleState.Ran, note, Math.Min(found.Count, MaxFindingsPerRule), cut));
            }
            catch (Exception ex) when (ex is RuleError or ArgumentException or RegexMatchTimeoutException)
            {
                runs.Add(new RuleRun(rule, RuleState.Error, ex.Message, 0, false));
            }
        }
        return new Report(runs, findings);
    }

    private sealed class RuleError(string message) : Exception(message);

    private static Regex Re(CheckRule rule, string field, string pattern, bool required = true)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return required ? throw new RuleError($"{field} 가 비어 있습니다.") : Never;
        }
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException ex)
        {
            throw new RuleError($"{field} 정규식 오류: {ex.Message}");
        }
    }

    private static readonly Regex Never = new("(?!)");

    // from 에서 to 까지 가장 짧은 호출 경로(함수 주소만 넘긴 선은 따라가지 않는다). 없으면 빈 목록.
    // 화면에서 고른 진입점으로 지적을 열 때 쓴다.
    public static IReadOnlyList<string> PathFrom(TraceGraph graph, TraceIndex index, string from, string to)
    {
        var parent = new Dictionary<string, string> { [from] = "" };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0 && !parent.ContainsKey(to))
        {
            var id = queue.Dequeue();
            foreach (var e in index.ChildrenOf(id))
            {
                if (!TraceQueries.IsAddressOnly(graph, e) && parent.TryAdd(e.To, id))
                {
                    queue.Enqueue(e.To);
                }
            }
        }
        if (!parent.ContainsKey(to))
        {
            return [];
        }
        var path = new List<string>();
        for (var cur = to; cur.Length > 0; cur = parent[cur])
        {
            path.Add(cur);
        }
        path.Reverse();
        return path;
    }

    // 규칙마다 다시 만들지 않는 것: 진입점에서의 대표 경로, 정리한 본문 줄, 선마다의 "주소만 넘김" 판정, 거꾸로 닿는 진입점
    private sealed class Context(TraceGraph graph, TraceIndex index, Func<string, string[]?> readLines)
    {
        public TraceGraph Graph { get; } = graph;
        public TraceIndex Index { get; } = index;
        public Func<string, string[]?> ReadLines { get; } = readLines;
        private Dictionary<string, string>? parent_;
        private readonly Dictionary<string, string[]> clean_ = [];
        private readonly Dictionary<TraceEdge, bool> addressOnly_ = new(ReferenceEqualityComparer.Instance);
        private Dictionary<string, List<string>>? callers_;
        private HashSet<string>? entryNodes_;
        private readonly Dictionary<string, IReadOnlyList<string>> entriesOf_ = [];

        // 이 함수까지 닿는 진입점 함수 id 전부(호출자를 거슬러 올라간다)
        public IReadOnlyList<string> EntriesReaching(string nodeId)
        {
            if (entriesOf_.TryGetValue(nodeId, out var cached))
            {
                return cached;
            }
            if (callers_ is null)
            {
                callers_ = [];
                foreach (var e in Graph.Edges.Where(e => !AddressOnly(e)))
                {
                    (callers_.TryGetValue(e.To, out var list) ? list : callers_[e.To] = []).Add(e.From);
                }
                entryNodes_ = Graph.EntryPoints.Select(e => e.NodeId).ToHashSet();
            }
            var seen = new HashSet<string> { nodeId };
            var queue = new Queue<string>();
            queue.Enqueue(nodeId);
            var found = new List<string>();
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (entryNodes_!.Contains(id))
                {
                    found.Add(id);
                }
                foreach (var c in callers_.GetValueOrDefault(id) ?? [])
                {
                    if (seen.Add(c))
                    {
                        queue.Enqueue(c);
                    }
                }
            }
            return entriesOf_[nodeId] = found;
        }

        // 모든 진입점에서 한꺼번에 너비 우선: 어느 함수든 가장 가까운 진입점에서의 경로 하나
        public List<string> PathTo(string nodeId)
        {
            if (parent_ is null)
            {
                parent_ = [];
                var queue = new Queue<string>();
                foreach (var e in Graph.EntryPoints.Where(e => Graph.Nodes.ContainsKey(e.NodeId)))
                {
                    if (parent_.TryAdd(e.NodeId, ""))
                    {
                        queue.Enqueue(e.NodeId);
                    }
                }
                while (queue.Count > 0)
                {
                    var id = queue.Dequeue();
                    foreach (var edge in Index.ChildrenOf(id))
                    {
                        if (!AddressOnly(edge) && parent_.TryAdd(edge.To, id))
                        {
                            queue.Enqueue(edge.To);
                        }
                    }
                }
            }
            var path = new List<string>();
            for (var cur = nodeId; cur.Length > 0 && path.Count < 64; cur = parent_.GetValueOrDefault(cur) ?? "")
            {
                path.Add(cur);
            }
            path.Reverse();
            return path;
        }

        public string[] Clean(TraceNode node)
        {
            if (!clean_.TryGetValue(node.Id, out var lines))
            {
                lines = CodeText.Clean(node.Code.Split('\n'));
                clean_[node.Id] = lines;
            }
            return lines;
        }

        public bool AddressOnly(TraceEdge e)
        {
            if (!addressOnly_.TryGetValue(e, out var v))
            {
                v = TraceQueries.IsAddressOnly(Graph, e);
                addressOnly_[e] = v;
            }
            return v;
        }
    }

    private static string LineText(TraceNode node, int line)
    {
        var lines = node.Code.Split('\n');
        var i = line - node.StartLine;
        return i >= 0 && i < lines.Length ? lines[i].Trim() : "";
    }

    private static Certainty Worst(Certainty a, Certainty b) => (Certainty)Math.Max((int)a, (int)b);

    private static IEnumerable<TraceNode> CodeNodes(TraceGraph g) =>
        g.Nodes.Values.Where(n => n.Kind != NodeKind.External && n.Code.Length > 0).OrderBy(n => n.File, StringComparer.Ordinal).ThenBy(n => n.StartLine);

    // ---- 범위 안 금지 ----

    private static (List<Finding>, string) RunForbidInScope(CheckRule rule, Context c)
    {
        var open = Re(rule, "scopeOpen", rule.ScopeOpen);
        var close = Re(rule, "scopeClose", rule.ScopeClose, required: false);
        var forbid = Re(rule, "forbid", rule.Forbid, required: rule.ForbidTags.Count == 0);
        var tags = rule.ForbidTags.ToHashSet();
        var statement = rule.Scope == "statement";
        var depth = Math.Clamp(rule.Depth, 0, 6);
        bool Bad(TraceNode n) => forbid.IsMatch(n.Name) || n.Tags.Any(tags.Contains);

        var found = new List<Finding>();
        var scopes = 0;
        foreach (var node in CodeNodes(c.Graph))
        {
            var lines = c.Clean(node);
            for (var i = 0; i < lines.Length; i++)
            {
                var m = open.Match(lines[i]);
                if (!m.Success)
                {
                    continue;
                }
                scopes++;
                // 범위는 (줄, 열)로 잰다: 같은 줄에서 잠그기 전·푼 뒤의 호출은 빼야 한다. 열은 0부터(LSP 와 같음).
                var begin = (i, statement ? m.Index : m.Index + m.Length);
                var (end, reachedEnd) = statement ? StatementEnd(lines, i, m.Index) : BlockEnd(lines, i, m.Index + m.Length, close);
                bool Inside(int line, int column)
                {
                    var at = (line - node.StartLine, column);
                    return Compare(at, begin) >= 0 && Compare(at, end) < 0;
                }
                var to = node.StartLine + end.Line;
                var evidence = new List<Evidence>();
                var certainty = Certainty.High;
                var why = "잠근 범위 안에 정적 호출이 직접 있습니다.";
                var deepest = new List<string>();
                foreach (var edge in c.Index.ChildrenOf(node.Id))
                {
                    var sites = edge.CallSites.Where(s => Inside(s.Line, s.Column)).ToList();
                    if (sites.Count == 0 || c.AddressOnly(edge) || !c.Graph.Nodes.TryGetValue(edge.To, out var callee))
                    {
                        continue;
                    }
                    var site = sites[0];
                    var guess = TraceQueries.IsGuess(edge);
                    if (Bad(callee))
                    {
                        var byName = forbid.IsMatch(callee.Name);
                        evidence.Add(new Evidence(site, LineText(node, site.Line),
                            $"{TraceQueries.ShortName(callee.Name)} 호출" + (byName ? "" : $" (표시: {string.Join(", ", callee.Tags.Where(tags.Contains))})")));
                        if (!byName || guess)
                        {
                            certainty = Worst(certainty, guess ? Certainty.Low : Certainty.Medium);
                        }
                        if (deepest.Count == 0)
                        {
                            deepest = [edge.To];
                        }
                        continue;
                    }
                    if (depth > 0 && callee.Kind != NodeKind.External && Below(c, edge.To, depth, Bad) is { } chain)
                    {
                        var last = c.Graph.Nodes[chain[^1]];
                        evidence.Add(new Evidence(site, LineText(node, site.Line),
                            $"{TraceQueries.ShortName(callee.Name)} 안에서 {chain.Count}단계 아래 {TraceQueries.ShortName(last.Name)}"));
                        certainty = Worst(certainty, guess ? Certainty.Low : Certainty.Medium);
                        if (deepest.Count == 0)
                        {
                            deepest = [edge.To, .. chain];
                        }
                    }
                }
                foreach (var hit in node.TagLines.Where(t => t.Line - node.StartLine > i && t.Line <= to && t.Tags.Any(tags.Contains)))
                {
                    evidence.Add(new Evidence(new SourceRef { File = node.File, Line = hit.Line }, LineText(node, hit.Line),
                        $"본문 줄 표시: {string.Join(", ", hit.Tags.Where(tags.Contains))}"));
                    certainty = Worst(certainty, Certainty.Medium);
                }
                if (evidence.Count == 0)
                {
                    continue;
                }
                if (certainty != Certainty.High)
                {
                    why = certainty == Certainty.Low
                        ? "추정 연결(등록·메시지)을 지나서 찾았습니다."
                        : "아래 함수 안에서, 또는 이름 표시(tags)로 찾았습니다.";
                }
                if (!reachedEnd && node.CodeTruncated)
                {
                    why += " 본문이 잘려 범위 끝을 보지 못했습니다.";
                }
                var openRef = new SourceRef { File = node.File, Line = node.StartLine + i };
                found.Add(new Finding(rule.Name, rule.Kind, certainty, why,
                    $"{TraceQueries.ShortName(node.Name)}: 잠근 범위 안에서 {evidence.Count}곳",
                    $"{openRef.Line}줄({LineText(node, openRef.Line)})부터 {to}줄까지 잠근 채로 부릅니다.",
                    node.Id, openRef, evidence.Take(10).ToList(), [.. c.PathTo(node.Id), .. deepest]));
            }
        }
        return (found, $"잠그는 곳 {scopes}개를 봄");
    }

    private static int Compare((int Line, int Column) a, (int Line, int Column) b) =>
        a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column);

    // 여는 곳(match 끝)부터 중괄호를 세어, 그 블록이 닫히는 '}' 또는 같은 깊이에서 close 가 걸린 곳(둘 다 범위 밖 첫 위치)
    // 잠금이 문장(lock_guard l(m); / m.lock();)이면 그 줄이 속한 블록 끝까지, 뒤에 블록이 따라오면(C# lock (x) { } ·
    // using (...) { }) 그 블록 끝까지.
    private static ((int Line, int Column) End, bool Reached) BlockEnd(string[] lines, int start, int column, Regex close)
    {
        var ownBlock = OwnsBlock(lines, start, column);
        var depth = 0;
        for (var i = start; i < lines.Length; i++)
        {
            var text = lines[i];
            var from = i == start ? column : 0;
            var closeAt = close.Match(text, from) is { Success: true } cm ? cm.Index : -1;
            for (var k = from; k < text.Length; k++)
            {
                if (k == closeAt && depth == 0)
                {
                    return ((i, k), true);
                }
                if (text[k] == '{')
                {
                    depth++;
                }
                else if (text[k] == '}' && (--depth < 0 || (ownBlock && depth == 0)))
                {
                    return ((i, k), true);
                }
            }
        }
        return ((lines.Length - 1, int.MaxValue), false);
    }

    // 여는 곳 뒤에서 ';' 보다 '{' 가 먼저 오고 그 '{' 바로 앞이 ')' 이면(lock (x) {) 뒤따르는 블록이 범위다.
    // scoped_lock l{m}; 의 '{' 는 앞이 이름이라 초기화로 본다.
    private static bool OwnsBlock(string[] lines, int start, int column)
    {
        var last = ' ';
        for (var i = start; i < lines.Length && i < start + 4; i++)
        {
            for (var k = i == start ? column : 0; k < lines[i].Length; k++)
            {
                var ch = lines[i][k];
                if (ch == ';')
                {
                    return false;
                }
                if (ch == '{')
                {
                    return last == ')';
                }
                if (!char.IsWhiteSpace(ch))
                {
                    last = ch;
                }
            }
        }
        return false;
    }

    // 여는 곳 뒤 문장 끝 ';' 까지(최대 8줄)
    private static ((int Line, int Column) End, bool Reached) StatementEnd(string[] lines, int start, int column)
    {
        for (var i = start; i < lines.Length && i < start + 8; i++)
        {
            if (lines[i].IndexOf(';', i == start ? column : 0) is var k && k >= 0)
            {
                return ((i, k + 1), true);
            }
        }
        return ((Math.Min(lines.Length - 1, start + 7), int.MaxValue), false);
    }

    // id 아래 depth 단계 안에서 처음 만나는 bad 함수까지의 경로(id 제외)
    private static List<string>? Below(Context c, string id, int depth, Func<TraceNode, bool> bad)
    {
        var parent = new Dictionary<string, string> { [id] = "" };
        var frontier = new List<string> { id };
        for (var d = 0; d < depth && frontier.Count > 0; d++)
        {
            var next = new List<string>();
            foreach (var u in frontier)
            {
                foreach (var e in c.Index.ChildrenOf(u))
                {
                    if (c.AddressOnly(e) || !c.Graph.Nodes.TryGetValue(e.To, out var v) || !parent.TryAdd(e.To, u))
                    {
                        continue;
                    }
                    if (bad(v))
                    {
                        var chain = new List<string>();
                        for (var cur = e.To; cur != id; cur = parent[cur])
                        {
                            chain.Add(cur);
                        }
                        chain.Reverse();
                        return chain;
                    }
                    if (v.Kind != NodeKind.External)
                    {
                        next.Add(e.To);
                    }
                }
            }
            frontier = next;
        }
        return null;
    }

    // ---- 안에서만 ----

    private static (List<Finding>, string) RunRequireWithin(CheckRule rule, Context c)
    {
        var subject = Re(rule, "subject", rule.Subject);
        var within = Re(rule, "within", rule.Within, required: rule.WithinEdges.Count == 0 && string.IsNullOrWhiteSpace(rule.WithinEntry));
        var withinEntry = Re(rule, "withinEntry", rule.WithinEntry, required: false);
        var edges = rule.WithinEdges.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var g = c.Graph;
        var subjects = g.Nodes.Values.Count(n => subject.IsMatch(n.Name));
        if (subjects == 0)
        {
            return ([], "subject 에 맞는 함수가 분석 결과에 없음(이름 규칙을 확인하세요)");
        }

        // 대상 함수마다: 닿은 진입점들과 가장 짧은 경로
        var hits = new Dictionary<string, List<(EntryPoint Entry, List<string> Path, bool Guess)>>();
        var skipped = 0;
        foreach (var entry in g.EntryPoints.Where(e => g.Nodes.ContainsKey(e.NodeId)))
        {
            var root = g.Nodes[entry.NodeId];
            if (within.IsMatch(root.Name) || withinEntry.IsMatch(entry.Label) || withinEntry.IsMatch(entry.Group) || withinEntry.IsMatch(entry.Detector))
            {
                skipped++;
                continue;
            }
            var parent = new Dictionary<string, (string From, bool Guess)> { [entry.NodeId] = ("", false) };
            var queue = new Queue<string>();
            queue.Enqueue(entry.NodeId);
            while (queue.Count > 0 && parent.Count < 20000)
            {
                var u = queue.Dequeue();
                foreach (var e in c.Index.ChildrenOf(u))
                {
                    if (c.AddressOnly(e) || edges.Contains(e.Resolution) || !g.Nodes.TryGetValue(e.To, out var v) ||
                        within.IsMatch(v.Name) || parent.ContainsKey(e.To))
                    {
                        continue;
                    }
                    parent[e.To] = (u, parent[u].Guess || TraceQueries.IsGuess(e));
                    if (subject.IsMatch(v.Name))
                    {
                        var path = new List<string>();
                        for (var cur = e.To; cur.Length > 0; cur = parent[cur].From)
                        {
                            path.Add(cur);
                        }
                        path.Reverse();
                        (hits.TryGetValue(e.To, out var list) ? list : hits[e.To] = []).Add((entry, path, parent[e.To].Guess));
                        continue;   // 대상 함수 안쪽은 더 보지 않는다
                    }
                    if (v.Kind != NodeKind.External)
                    {
                        queue.Enqueue(e.To);
                    }
                }
            }
        }

        var found = new List<Finding>();
        foreach (var (id, list) in hits.OrderBy(h => g.Nodes[h.Key].Name, StringComparer.Ordinal))
        {
            var node = g.Nodes[id];
            var best = list.OrderBy(h => h.Guess).ThenBy(h => h.Path.Count).First();
            var caller = g.Nodes[best.Path[^2]];
            var line = c.Index.CallLines(caller.Id, id).DefaultIfEmpty(caller.StartLine).First();
            var site = new SourceRef { File = caller.File, Line = line };
            var certainty = best.Guess ? Certainty.Low : Certainty.High;
            var evidence = list.OrderBy(h => h.Path.Count).Take(8).Select(h =>
            {
                var last = g.Nodes[h.Path[^2]];
                var l = c.Index.CallLines(last.Id, id).DefaultIfEmpty(last.StartLine).First();
                return new Evidence(new SourceRef { File = last.File, Line = l }, LineText(last, l),
                    $"{h.Entry.Label} → … → {TraceQueries.ShortName(last.Name)} ({h.Path.Count - 1}단계{(h.Guess ? ", 추정 연결 포함" : "")})");
            }).ToList();
            found.Add(new Finding(rule.Name, rule.Kind, certainty,
                best.Guess ? "경로에 추정 연결(등록·메시지)이 끼어 있습니다." : "진입점에서 정적 호출만으로 닿습니다.",
                $"{TraceQueries.ShortName(node.Name)}: 진입점 {list.Count}개에서 바로 닿음",
                $"{best.Entry.Label} 에서 {(rule.Within.Length > 0 ? "within 함수" : "안쪽 연결")}을 거치지 않고 {TraceQueries.ShortName(node.Name)} 까지 옵니다.",
                id, site, evidence, best.Path) { Entries = list.Select(h => h.Entry.NodeId).Distinct().ToList() });
        }
        return (found, $"대상 함수 {subjects}개, 진입점 {g.EntryPoints.Count - skipped}개를 봄" + (skipped > 0 ? $"(안쪽 진입점 {skipped}개 제외)" : ""));
    }

    // ---- 순서 ----

    private sealed record Step(string CalleeId, string CallerId, int Line, int Column, List<string> Path, bool Guess);

    // 진입점에서 소스 순서대로 깊이 우선(흐름 요약과 같은 걸음)
    private static List<Step> Steps(Context c, string rootId)
    {
        var steps = new List<Step>();
        var expanded = new HashSet<string>();
        void Walk(List<string> path, bool guess)
        {
            var id = path[^1];
            if (!expanded.Add(id) || path.Count > 12 || steps.Count > 2000)
            {
                return;
            }
            // 같은 함수를 여러 번 부르면 부를 때마다 한 걸음(두 번째 호출이 순서에서 빠지지 않게). 안쪽은 처음 만난 곳에서만 펼친다.
            var sites = c.Index.ChildrenOf(id)
                .Where(e => !c.AddressOnly(e) && c.Graph.Nodes.ContainsKey(e.To))
                .SelectMany(e => e.CallSites.Count > 0
                    ? e.CallSites.Select(s => (Edge: e, s.Line, s.Column))
                    : [(Edge: e, Line: c.Graph.Nodes[id].StartLine, Column: 0)])
                .OrderBy(x => x.Line).ThenBy(x => x.Column).ToList();
            foreach (var (e, line, column) in sites)
            {
                var callee = c.Graph.Nodes[e.To];
                var g = guess || TraceQueries.IsGuess(e);
                steps.Add(new Step(e.To, id, line, column, [.. path, e.To], g));
                if (callee.Kind != NodeKind.External && !path.Contains(e.To))
                {
                    Walk([.. path, e.To], g);
                }
            }
        }
        Walk([rootId], false);
        return steps;
    }

    private static (List<Finding>, string) RunOrder(CheckRule rule, Context c)
    {
        var first = Re(rule, "first", rule.First);
        var then = Re(rule, "then", rule.Then);
        var unless = Re(rule, "unless", rule.Unless, required: false);
        var g = c.Graph;
        var found = new List<Finding>();
        var saved = 0;
        foreach (var entry in g.EntryPoints.Where(e => g.Nodes.ContainsKey(e.NodeId)))
        {
            var steps = Steps(c, entry.NodeId);
            var a = steps.FindIndex(s => first.IsMatch(g.Nodes[s.CalleeId].Name));
            if (a < 0)
            {
                continue;
            }
            var b = steps.FindIndex(a + 1, s => then.IsMatch(g.Nodes[s.CalleeId].Name));
            if (b < 0)
            {
                continue;
            }
            if (steps.Any(s => unless.IsMatch(g.Nodes[s.CalleeId].Name)) || unless.IsMatch(g.Nodes[entry.NodeId].Name))
            {
                saved++;
                continue;
            }
            var (sa, sb) = (steps[a], steps[b]);
            var (na, nb) = (g.Nodes[sa.CalleeId], g.Nodes[sb.CalleeId]);
            var ca = g.Nodes[sa.CallerId];
            var cb = g.Nodes[sb.CallerId];
            var guess = sa.Guess || sb.Guess;
            found.Add(new Finding(rule.Name, rule.Kind, guess ? Certainty.Low : Certainty.Medium,
                (guess ? "경로에 추정 연결이 끼어 있습니다. " : "") + "소스 순서로 판단했습니다(분기·실패 처리는 따지지 않음).",
                $"{entry.Label}: {TraceQueries.ShortName(na.Name)} 뒤 {TraceQueries.ShortName(nb.Name)}",
                $"{TraceQueries.ShortName(na.Name)} 를 먼저 부르고 {TraceQueries.ShortName(nb.Name)} 를 부릅니다. " +
                "뒤의 것이 실패해도 앞의 것을 되돌리는 호출(unless)을 이 흐름에서 찾지 못했습니다.",
                sb.CallerId, new SourceRef { File = cb.File, Line = sb.Line },
                [
                    new Evidence(new SourceRef { File = ca.File, Line = sa.Line }, LineText(ca, sa.Line), $"먼저: {TraceQueries.ShortName(na.Name)}"),
                    new Evidence(new SourceRef { File = cb.File, Line = sb.Line }, LineText(cb, sb.Line), $"나중: {TraceQueries.ShortName(nb.Name)}"),
                ],
                sb.Path) { Entries = [entry.NodeId] });
        }
        return (found, $"진입점 {g.EntryPoints.Count}개를 봄" + (saved > 0 ? $", 되돌리는 호출이 있어 넘긴 흐름 {saved}개" : ""));
    }

    // ---- 의존 금지 ----

    private static readonly Regex IncludeLine = new(@"^\s*#\s*include\s*[<""](?<path>[^>""]+)[>""]", RegexOptions.CultureInvariant);

    private static (List<Finding>, string) RunDependency(CheckRule rule, Context c)
    {
        if (rule.From.Count == 0 || rule.To.Count == 0)
        {
            throw new RuleError("from 과 to(파일 glob)가 둘 다 있어야 합니다.");
        }
        var isFrom = GlobMatcher.Build(rule.From, []);
        var isTo = GlobMatcher.Build(rule.To, []);
        bool Cross(string file) => file.Length > 0 && isFrom(file) is var f && f && !isTo(file);
        var g = c.Graph;
        var found = new List<Finding>();

        foreach (var group in g.Edges.Where(e => g.Nodes.TryGetValue(e.From, out var a) && Cross(a.File) &&
                                                 g.Nodes.TryGetValue(e.To, out var b) && b.Kind != NodeKind.External && isTo(b.File) &&
                                                 !c.AddressOnly(e))
                     .GroupBy(e => e.From).OrderBy(x => g.Nodes[x.Key].File, StringComparer.Ordinal))
        {
            var from = g.Nodes[group.Key];
            var guess = group.All(TraceQueries.IsGuess);
            var evidence = group.Take(10).Select(e =>
            {
                var site = e.CallSites.FirstOrDefault() ?? new SourceRef { File = from.File, Line = from.StartLine };
                var to = g.Nodes[e.To];
                return new Evidence(site, LineText(from, site.Line), $"{TraceQueries.ShortName(to.Name)} ({to.File})" +
                                                                    (TraceQueries.IsGuess(e) ? " · " + TraceQueries.ResolutionText(e.Resolution) : ""));
            }).ToList();
            found.Add(new Finding(rule.Name, rule.Kind, guess ? Certainty.Low : Certainty.High,
                guess ? "추정 연결로만 이어집니다." : "정적 호출입니다.",
                $"{TraceQueries.ShortName(from.Name)} → 콘텐츠 함수 {group.Count()}개",
                $"{from.File} 의 {TraceQueries.ShortName(from.Name)} 가 to 쪽 함수를 부릅니다.",
                from.Id, evidence[0].Site, evidence, [.. c.PathTo(from.Id), group.First().To]));
        }

        var resolver = new IncludeResolver(g.Inputs.Select(i => i.File.Replace('\\', '/')));
        var unread = 0;
        foreach (var file in g.Inputs.Select(i => i.File.Replace('\\', '/')).Where(Cross).Order(StringComparer.Ordinal))
        {
            var lines = c.ReadLines(file);
            if (lines is null)
            {
                unread++;
                continue;
            }
            var evidence = new List<Evidence>();
            var sure = true;
            // 블록 주석·문자열 안의 #include 는 빼려고, 주석을 비운 줄에서 지시문인지 보고 경로는 원래 줄에서 읽는다
            var clean = CodeText.Clean(lines);
            for (var i = 0; i < lines.Length; i++)
            {
                var m = IncludeLine.Match(lines[i]);
                if (m.Success && IncludeDirective.IsMatch(clean[i]) && resolver.Resolve(m.Groups["path"].Value, file) is { } hit && isTo(hit.File))
                {
                    sure &= hit.Unique;
                    evidence.Add(new Evidence(new SourceRef { File = file, Line = i + 1 }, lines[i].Trim(),
                        $"포함: {hit.File}" + (hit.Unique ? "" : " (같은 이름 파일 중 가장 가까운 것)")));
                }
            }
            if (evidence.Count > 0)
            {
                found.Add(new Finding(rule.Name, rule.Kind, sure ? Certainty.High : Certainty.Medium,
                    sure ? "#include 줄입니다." : "#include 줄입니다. 같은 이름 파일이 여럿이라 가장 가까운 폴더의 것으로 봤습니다.",
                    $"{file}: 콘텐츠 헤더 {evidence.Count}개 포함", $"{file} 이(가) to 쪽 파일을 #include 합니다.",
                    "", evidence[0].Site, evidence.Take(10).ToList(), []));
            }
        }
        return (found, $"from 파일 {g.Nodes.Values.Where(n => Cross(n.File)).Select(n => n.File).Distinct().Count()}개의 함수와 #include 를 봄" +
                       (unread > 0 ? $", 읽지 못한 파일 {unread}개" : ""));
    }

    private static readonly Regex IncludeDirective = new(@"^\s*#\s*include\b", RegexOptions.CultureInvariant);

    // #include "a/b.h" 가 가리키는 입력 파일: 포함한 파일 기준 상대 경로가 있으면 그것, 아니면 경로 끝이 같은 파일 중
    // 포함한 파일과 가장 많은 폴더를 공유하는 것(각 프로젝트의 pch.h 처럼 같은 이름이 여럿일 때). 컴파일러의 include 경로는 보지 않는다.
    // 파일 이름별 색인과 (폴더, include) 결과 캐시를 둬서 include 마다 전체 목록을 훑지 않는다.
    private sealed class IncludeResolver
    {
        private readonly Dictionary<string, string> exact_ = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> byName_ = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Dir, string Include), (string File, bool Unique)?> cache_ = [];

        public IncludeResolver(IEnumerable<string> files)
        {
            foreach (var f in files)
            {
                if (exact_.TryAdd(f, f))
                {
                    var name = f[(f.LastIndexOf('/') + 1)..];
                    (byName_.TryGetValue(name, out var list) ? list : byName_[name] = []).Add(f);
                }
            }
        }

        public (string File, bool Unique)? Resolve(string include, string includer)
        {
            var inc = include.Replace('\\', '/');
            var dir = includer.Contains('/') ? includer[..includer.LastIndexOf('/')] : "";
            if (cache_.TryGetValue((dir, inc), out var cached))
            {
                return cached;
            }
            return cache_[(dir, inc)] = Find(inc, dir);
        }

        private (string File, bool Unique)? Find(string inc, string dir)
        {
            if (exact_.TryGetValue(Normalize(dir.Length > 0 ? dir + "/" + inc : inc), out var exact))
            {
                return (exact, true);
            }
            var tail = Normalize(inc);
            if (tail.Length == 0 || !byName_.TryGetValue(tail[(tail.LastIndexOf('/') + 1)..], out var named))
            {
                return null;
            }
            var candidates = named.Where(f => f.Equals(tail, StringComparison.OrdinalIgnoreCase) ||
                                              f.EndsWith("/" + tail, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0)
            {
                return null;
            }
            var parts = dir.Split('/');
            int Shared(string f)
            {
                var p = f.Split('/');
                var n = 0;
                while (n < parts.Length && n < p.Length - 1 && string.Equals(parts[n], p[n], StringComparison.OrdinalIgnoreCase))
                {
                    n++;
                }
                return n;
            }
            var best = candidates.OrderByDescending(Shared).ThenBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal).First();
            return (best, candidates.Count == 1);
        }
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var p in path.Split('/'))
        {
            if (p == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (p is not ("." or ""))
            {
                parts.Add(p);
            }
        }
        return string.Join('/', parts);
    }

    // ---- 글 ----

    public static string ToText(TraceGraph graph, Report report, string title, Certainty? atLeast = null)
    {
        var sb = new StringBuilder();
        sb.Append("# 점검: ").Append(title).Append("\n\n");
        sb.Append("| 규칙 | 종류 | 상태 | 지적 |\n|---|---|---|---|\n");
        foreach (var r in report.Runs)
        {
            sb.Append("| ").Append(r.Rule.Name).Append(" | ").Append(KindText(r.Rule.Kind)).Append(" | ")
              .Append(r.State switch { RuleState.Off => "꺼짐", RuleState.Error => "오류: " + r.Note.Replace('|', '/'), _ => r.Note })
              .Append(" | ").Append(r.State == RuleState.Ran ? r.Count + (r.Cut ? "+" : "") : "-").Append(" |\n");
        }
        foreach (var group in report.Findings.Where(f => atLeast is null || f.Certainty <= atLeast).GroupBy(f => f.Rule))
        {
            sb.Append("\n## ").Append(group.Key).Append('\n');
            foreach (var f in group)
            {
                sb.Append("\n- **").Append(f.Title).Append("** · 확실도 ").Append(CertaintyText(f.Certainty))
                  .Append(" · ").Append(f.Site).Append('\n');
                sb.Append("  ").Append(f.Message).Append(" (").Append(f.Why).Append(")\n");
                foreach (var e in f.Evidence)
                {
                    sb.Append("  - ").Append(e.Site).Append(' ').Append(e.Note);
                    if (e.Text.Length > 0)
                    {
                        sb.Append("  `").Append(e.Text.Replace('`', '\'')).Append('`');
                    }
                    sb.Append('\n');
                }
                if (f.Path.Count > 1)
                {
                    sb.Append("  - 경로: ").Append(string.Join(" → ", f.Path.Where(graph.Nodes.ContainsKey)
                        .Select(p => TraceQueries.ShortName(graph.Nodes[p].Name)))).Append('\n');
                }
            }
        }
        sb.Append("\n## 이 결과가 증명하지 않는 것\n\n");
        foreach (var l in Limits)
        {
            sb.Append("- ").Append(l).Append('\n');
        }
        return sb.ToString();
    }
}

// 정규식을 걸기 전에 주석과 문자열·문자 상수 안을 비운다(줄 수와 열 위치는 그대로).
public static class CodeText
{
    public static string[] Clean(IReadOnlyList<string> lines)
    {
        var result = new string[lines.Count];
        var inBlock = false;
        for (var li = 0; li < lines.Count; li++)
        {
            var src = lines[li];
            var sb = new StringBuilder(src.Length);
            var i = 0;
            while (i < src.Length)
            {
                if (inBlock)
                {
                    if (src[i] == '*' && i + 1 < src.Length && src[i + 1] == '/')
                    {
                        inBlock = false;
                        sb.Append("  ");
                        i += 2;
                    }
                    else
                    {
                        sb.Append(' ');
                        i++;
                    }
                    continue;
                }
                var ch = src[i];
                if (ch == '/' && i + 1 < src.Length && src[i + 1] == '/')
                {
                    sb.Append(' ', src.Length - i);
                    break;
                }
                if (ch == '/' && i + 1 < src.Length && src[i + 1] == '*')
                {
                    inBlock = true;
                    sb.Append("  ");
                    i += 2;
                    continue;
                }
                if (ch is '"' or '\'')
                {
                    sb.Append(ch);
                    i++;
                    while (i < src.Length && src[i] != ch)
                    {
                        var step = src[i] == '\\' && i + 1 < src.Length ? 2 : 1;
                        sb.Append(' ', step);
                        i += step;
                    }
                    if (i < src.Length)
                    {
                        sb.Append(ch);
                        i++;
                    }
                    continue;
                }
                sb.Append(ch);
                i++;
            }
            result[li] = sb.ToString();
        }
        return result;
    }
}
