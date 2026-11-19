using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ReviewTool.Core;

namespace ReviewTool.CSharp;

// C# 어댑터. 분석 범위의 .cs 를 Roslyn 으로 한 번에 컴파일(참조는 이 도구가 도는 .NET 런타임 어셈블리만)하고,
// 진입점(설정 entryPoints 정규식)에서 호출을 따라 내려가 C++ 어댑터와 같은 TraceGraph 를 채운다.
// MSBuild·NuGet 복원을 하지 않으므로 패키지 안의 형식은 "외부"로 보이거나 이름을 풀지 못한다(진단에 수를 남긴다).
public sealed class CSharpTraceAnalyzer : ITraceAnalyzer
{
    public string Language => "csharp";

    public IEnumerable<string> EnumerateInputs(TargetConfig config)
    {
        var filter = GlobMatcher.Build(config.Include, config.Exclude);
        foreach (var file in SourceFiles.Enumerate(config.Root, f => IsSource(f) || IsProjectFile(f), config.Exclude))
        {
            var rel = Path.GetRelativePath(config.Root, file).Replace('\\', '/');
            if (IsProjectFile(file) || filter(rel))
            {
                yield return file;
            }
        }
    }

    internal static bool IsSource(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsProjectFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase);
    }

    public Task<TraceGraph> AnalyzeAsync(TargetConfig config, WorkPaths paths, IProgress<AnalysisProgress>? progress,
                                         CancellationToken cancellationToken) =>
        Task.Run(() => Analyze(config, progress, cancellationToken), cancellationToken);

    private TraceGraph Analyze(TargetConfig config, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var graph = new TraceGraph
        {
            TargetName = config.Name,
            RootDirectory = config.Root,
            Language = Language,
            AnalyzedAt = DateTimeOffset.Now,
            AnalyzerVersion = typeof(CSharpTraceAnalyzer).Assembly.GetName().Version?.ToString() ?? "",
        };
        progress?.Report(new AnalysisProgress("prepare", "입력 파일 기록 중"));
        graph.Inputs = EnumerateInputs(config).Select(f => InputFile.Read(config.Root, f)).ToList();
        graph.ConfigHash = config.ContentHash();
        graph.FingerprintVersion = CodeFingerprint.Version;

        var filter = GlobMatcher.Build(config.Include, config.Exclude);
        var files = SourceFiles.Enumerate(config.Root, IsSource, config.Exclude)
            .Where(f => filter(Path.GetRelativePath(config.Root, f).Replace('\\', '/')))
            .ToList();
        progress?.Report(new AnalysisProgress("prepare", $"분석할 .cs {files.Count}개"));
        if (files.Count == 0)
        {
            graph.Diagnostics.Add("분석할 .cs 가 없습니다. include/exclude 설정을 확인하세요.");
            return graph;
        }

        progress?.Report(new AnalysisProgress("index", "Roslyn 으로 해석 중(.csproj 마다 컴파일)"));
        var csprojs = SourceFiles.Enumerate(config.Root, f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase), config.Exclude).ToList();
        var projects = ProjectSet.Build(config.Root, files, csprojs, ct);
        var unresolvedNames = projects.UnresolvedNames(ct);

        var walker = new RoslynWalker(config, graph, projects, filter);
        var found = FindEntryPoints(config, files);
        progress?.Report(new AnalysisProgress("entry", $"진입점 후보 {found.Count}개"));
        var projectDirs = csprojs
            .Select(f => Path.GetRelativePath(config.Root, Path.GetDirectoryName(f)!).Replace('\\', '/'))
            .Where(d => d != ".")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        foreach (var candidate in found)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new AnalysisProgress("trace", candidate.Method, done++, found.Count));
            var root = walker.ResolveEntry(candidate);
            if (root is null)
            {
                graph.Diagnostics.Add($"진입점의 정의를 찾지 못함: {candidate.RelativeFile}:{candidate.Line} {candidate.Method}");
                continue;
            }
            if (graph.EntryPoints.Any(e => e.NodeId == root))
            {
                continue;
            }
            var node = graph.Nodes[root];
            graph.EntryPoints.Add(new EntryPoint
            {
                NodeId = root,
                Label = MakeLabel(candidate, node),
                Group = MakeGroup(candidate.RelativeFile, candidate.ClassName ?? ClassOf(node), projectDirs),
                RegisteredAt = new SourceRef { File = candidate.RelativeFile, Line = candidate.Line, Column = candidate.Column },
                Detector = candidate.Rule.Name,
            });
            walker.Walk(root, ct);
        }

        // 최상위 문(top-level statements) 시작 지점: "시작 함수" 규칙이 켜져 있으면 프로젝트마다 하나
        if (config.EntryPoints.Any(r => r.Name == StartRule))
        {
            foreach (var main in projects.TopLevelEntryPoints(ct))
            {
                var root = walker.EnsureEntry(main);
                if (graph.EntryPoints.Any(e => e.NodeId == root))
                {
                    continue;
                }
                var node = graph.Nodes[root];
                graph.EntryPoints.Add(new EntryPoint
                {
                    NodeId = root,
                    Label = "Main",
                    Group = MakeGroup(node.File, "Program", projectDirs),
                    RegisteredAt = new SourceRef { File = node.File, Line = node.StartLine },
                    Detector = StartRule + "(최상위 문)",
                });
                walker.Walk(root, ct);
            }
        }

        // 메시지(패킷·메시지 이름)·표시(tags): 언어와 무관한 소스 텍스트 처리
        progress?.Report(new AnalysisProgress("link", "메시지 보내는 곳·받는 곳 찾는 중"));
        var lines = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        string[] ReadLines(string path) => lines.TryGetValue(path, out var l) ? l : lines[path] = File.Exists(path) ? File.ReadAllLines(path) : [];
        var messages = MessagePass.Run(config, graph, ReadLines);
        graph.Diagnostics.Add($"메시지(추정): 보내는 곳 {messages.Sends}개, 받는 곳 {messages.Receives}개, 같은 프로세스 안 메시지 연결 {messages.Linked}개 (설정 channels 의 정규식 기준)");
        graph.Types = walker.CollectTypes();

        if (unresolvedNames > 0)
        {
            graph.Diagnostics.Add($"이름을 풀지 못한 곳 {unresolvedNames}개: NuGet 패키지·생성 코드처럼 .cs 밖의 형식은 보지 않습니다(그 호출은 빠지거나 외부로 보임).");
        }
        if (walker.UnresolvedCalls > 0)
        {
            graph.Diagnostics.Add($"대상 함수를 정하지 못한 호출 {walker.UnresolvedCalls}개 (dynamic·풀지 못한 형식의 메서드)");
        }
        if (walker.NodeLimitHit)
        {
            graph.Diagnostics.Add($"노드 수 제한({config.MaxNodes})에 걸려 일부를 펼치지 않았습니다.");
        }
        graph.EntryPoints = graph.EntryPoints.OrderBy(e => e.Group).ThenBy(e => e.Label).ToList();
        graph.DurationMs = stopwatch.ElapsedMilliseconds;
        progress?.Report(new AnalysisProgress("done", $"노드 {graph.Nodes.Count}개, 호출 {graph.Edges.Count}개, {graph.DurationMs / 1000.0:F1}초"));
        return graph;
    }

    internal const string StartRule = "시작 함수";

    public sealed record EntryCandidate(EntryPointRule Rule, string File, string RelativeFile, int Line, int Column, int Index,
                                          string Method, string? ClassName, string? Label);

    internal static List<EntryCandidate> FindEntryPoints(TargetConfig config, IEnumerable<string> files)
    {
        var channelRules = config.Channels.Where(c => !string.IsNullOrWhiteSpace(c.Register))
            .Select(c => new EntryPointRule { Name = c.Name, Pattern = c.Register });
        var rules = config.EntryPoints.Concat(channelRules)
            .Select(rule => (Rule: rule, Regex: new Regex(rule.Pattern, RegexOptions.Multiline),
                             Files: rule.Files.Count > 0 ? GlobMatcher.Build(rule.Files, []) : (_ => true)))
            .ToList();
        var perRule = rules.Select(_ => new List<EntryCandidate>()).ToList();
        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(config.Root, file).Replace('\\', '/');
            string? text = null;
            for (var r = 0; r < rules.Count; r++)
            {
                var (rule, regex, ruleFilter) = rules[r];
                if (!ruleFilter(rel))
                {
                    continue;
                }
                text ??= File.ReadAllText(file);
                foreach (Match m in regex.Matches(text))
                {
                    var method = m.Groups["method"];
                    if (!method.Success || InComment(text, m.Index) || InString(text, method.Index))
                    {
                        continue;
                    }
                    var line = 1 + text.AsSpan(0, method.Index).Count('\n');
                    var col = method.Index - (text.LastIndexOf('\n', Math.Max(0, method.Index - 1)) + 1);
                    perRule[r].Add(new EntryCandidate(rule, file, rel, line, col, method.Index, method.Value,
                        m.Groups["class"].Success ? m.Groups["class"].Value : null,
                        m.Groups["label"].Success ? m.Groups["label"].Value : m.Groups["key"].Success ? m.Groups["key"].Value : null));
                }
            }
        }
        return perRule.SelectMany(x => x).ToList();
    }

    // 같은 줄에서 앞의 따옴표 수가 홀수면 문자열 안(설명 문자열 속 예시 코드를 진입점으로 잡지 않게). 근사치.
    private static bool InString(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var quotes = 0;
        for (var i = lineStart; i < index; i++)
        {
            if (text[i] == '"' && (i == 0 || text[i - 1] != '\\'))
            {
                quotes++;
            }
        }
        return quotes % 2 == 1;
    }

    private static bool InComment(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var before = text[lineStart..index].TrimStart();
        return before.Contains("//", StringComparison.Ordinal) || before.StartsWith('*') || before.StartsWith("/*", StringComparison.Ordinal);
    }

    // 패킷 핸들러는 인자 타입이 패킷이라(HandleLogin(Session s, C2SLogin packet)) 그 이름을 표시 이름으로 쓴다
    private static string MakeLabel(EntryCandidate candidate, TraceNode node)
    {
        if (!string.IsNullOrEmpty(candidate.Label))
        {
            return candidate.Label;
        }
        var paren = node.Code.IndexOf('(');
        var close = paren >= 0 ? node.Code.IndexOf(')', paren) : -1;
        var parameters = paren >= 0 && close > paren ? node.Code[paren..close] : "";
        var packet = PacketParam().Match(parameters);
        return packet.Success ? packet.Groups[1].Value : candidate.Method;
    }

    private static Regex PacketParam() => new(@"(?:\w+\.)*([A-Z][A-Za-z0-9]?2[A-Z]\w+)\s+\w+\s*[,)]?");

    private static string ClassOf(TraceNode node)
    {
        var cut = node.Name.LastIndexOf("::", StringComparison.Ordinal);
        return cut > 0 ? node.Name[..cut].Split("::")[^1] : Path.GetFileNameWithoutExtension(node.File);
    }

    // 묶음 = "프로젝트 / 클래스". 프로젝트는 파일에서 가장 가까운 .csproj 폴더(실행 파일 하나 = 프로세스 하나)
    internal static string MakeGroup(string relativeFile, string className, IReadOnlySet<string> projectDirs)
    {
        var dir = relativeFile.Contains('/') ? relativeFile[..relativeFile.LastIndexOf('/')] : "";
        for (; dir.Length > 0; dir = dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : "")
        {
            if (projectDirs.Contains(dir))
            {
                return $"{dir.Split('/')[^1]} / {className}";
            }
        }
        return className;
    }
}
