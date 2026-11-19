using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReviewTool.Core;

namespace ReviewTool.Cpp;

// C++ 어댑터. 진입점을 정규식으로 찾고, clangd 의 호출 계층(callHierarchy)으로 아래를 따라간다.
public sealed class CppTraceAnalyzer : ITraceAnalyzer
{
    public string Language => "cpp";

    // 분석 범위의 C++ 소스·헤더 + 컴파일 옵션을 정하는 파일(vcxproj, props, compile_flags.txt).
    // 범위 밖 헤더(3rd 등)는 "외부"로만 쓰므로 넣지 않는다.
    public IEnumerable<string> EnumerateInputs(TargetConfig config)
    {
        var filter = GlobMatcher.Build(config.Include, config.Exclude);
        foreach (var file in SourceFiles.Enumerate(config.Root, f => SourceFiles.IsCode(f) || IsBuildSetting(f), config.Exclude))
        {
            var rel = Path.GetRelativePath(config.Root, file).Replace('\\', '/');
            if (IsBuildSetting(file) || filter(rel))
            {
                yield return file;
            }
        }
    }

    private static bool IsBuildSetting(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("compile_flags.txt", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<TraceGraph> AnalyzeAsync(TargetConfig config, WorkPaths paths, IProgress<AnalysisProgress>? progress,
                                               CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var graph = new TraceGraph
        {
            TargetName = config.Name,
            RootDirectory = config.Root,
            Language = Language,
            AnalyzedAt = DateTimeOffset.Now,
            AnalyzerVersion = typeof(CppTraceAnalyzer).Assembly.GetName().Version?.ToString() ?? "",
        };

        var filter = GlobMatcher.Build(config.Include, config.Exclude);

        // 신선도용 입력 지문은 분석 "전"에 찍는다. 분석 도중 바뀐 파일은 다음 확인에서 바뀐 것으로 잡힌다.
        progress?.Report(new AnalysisProgress("prepare", "입력 파일 기록 중"));
        graph.Inputs = EnumerateInputs(config).Select(f => InputFile.Read(config.Root, f)).ToList();
        graph.ConfigHash = config.ContentHash();

        progress?.Report(new AnalysisProgress("prepare", "컴파일 옵션 정리 중"));
        var entries = CompileDatabase.Build(config, filter, graph.Diagnostics);
        CompileDatabase.Write(entries, paths.CompileDbDirectory);
        progress?.Report(new AnalysisProgress("prepare", $"분석할 .cpp {entries.Count}개"));
        if (entries.Count == 0)
        {
            graph.Diagnostics.Add("분석할 .cpp 가 없습니다. include/exclude 설정을 확인하세요.");
            return graph;
        }

        var clangd = ClangdSession.FindClangd(config.Cpp.Clangd);
        await using var session = await ClangdSession.StartAsync(clangd, config.Root, paths.CompileDbDirectory, cancellationToken);
        session.IndexProgress += m => progress?.Report(new AnalysisProgress("index", m));

        // 색인은 파일을 하나 열어야 시작하는 경우가 있어 먼저 하나 연다.
        var first = entries[0].File;
        await session.OpenAsync(first, cancellationToken);
        progress?.Report(new AnalysisProgress("index", "clangd 색인 중 (처음 한 번은 오래 걸림)"));
        var indexed = await session.WaitForIndexAsync(TimeSpan.FromSeconds(8),
                                                      TimeSpan.FromSeconds(config.Cpp.IndexTimeoutSeconds), cancellationToken);
        await session.CloseAsync(first, cancellationToken);
        if (!indexed)
        {
            graph.Diagnostics.Add($"clangd 색인이 {config.Cpp.IndexTimeoutSeconds}초 안에 끝나지 않아 일부 호출이 빠졌을 수 있습니다.");
        }

        var walker = new CallTreeWalker(config, graph, session, filter);
        var found = FindEntryPoints(config, filter);
        progress?.Report(new AnalysisProgress("entry", $"진입점 후보 {found.Count}개"));
        var groupPrefix = CommonDirectoryPrefix(found.Select(f => f.RelativeFile));

        var done = 0;
        foreach (var candidate in found)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AnalysisProgress("trace", candidate.Method, done++, found.Count));
            var root = await walker.ResolveEntryAsync(candidate, cancellationToken);
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
                Group = MakeGroup(candidate, groupPrefix),
                RegisteredAt = new SourceRef { File = candidate.RelativeFile, Line = candidate.Line, Column = candidate.Column },
                Detector = candidate.Rule.Name,
            });
            await walker.WalkAsync(root, cancellationToken);
        }

        if (walker.NodeLimitHit)
        {
            graph.Diagnostics.Add($"노드 수 제한({config.MaxNodes})에 걸려 일부를 펼치지 않았습니다.");
        }
        graph.Diagnostics.AddRange(walker.Failures.Take(30));
        if (walker.Failures.Count > 30)
        {
            graph.Diagnostics.Add($"... 외 실패 {walker.Failures.Count - 30}건");
        }

        graph.EntryPoints = graph.EntryPoints.OrderBy(e => e.Group).ThenBy(e => e.Label).ToList();
        graph.DurationMs = stopwatch.ElapsedMilliseconds;
        progress?.Report(new AnalysisProgress("done", $"노드 {graph.Nodes.Count}개, 호출 {graph.Edges.Count}개, {graph.DurationMs / 1000.0:F1}초"));
        return graph;
    }

    public sealed record EntryCandidate(EntryPointRule Rule, string File, string RelativeFile, int Line, int Column,
                                          string Method, string? ClassName, string? Label);

    // 파일을 한 번만 읽고 모든 규칙을 적용한다. 결과는 규칙 순서대로.
    internal static List<EntryCandidate> FindEntryPoints(TargetConfig config, Func<string, bool> filter)
    {
        var rules = config.EntryPoints
            .Select(rule => (Rule: rule, Regex: new Regex(rule.Pattern, RegexOptions.Multiline),
                             Files: rule.Files.Count > 0 ? GlobMatcher.Build(rule.Files, []) : (_ => true)))
            .ToList();
        var perRule = rules.Select(_ => new List<EntryCandidate>()).ToList();
        foreach (var file in SourceFiles.Enumerate(config.Root, SourceFiles.IsCode, config.Exclude))
        {
            var rel = Path.GetRelativePath(config.Root, file).Replace('\\', '/');
            if (!filter(rel))
            {
                continue;
            }
            string? text = null;
            int[]? lineStarts = null;
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
                    if (!method.Success || IsInComment(text, m.Index))
                    {
                        continue;
                    }
                    lineStarts ??= LineStarts(text);
                    var (line, col) = ToLineColumn(lineStarts, method.Index);
                    perRule[r].Add(new EntryCandidate(rule, file, rel, line, col, method.Value,
                        m.Groups["class"].Success ? m.Groups["class"].Value : null,
                        m.Groups["label"].Success ? m.Groups["label"].Value : null));
                }
            }
        }
        return perRule.SelectMany(x => x).ToList();
    }

    internal static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return [.. starts];
    }

    internal static (int Line, int Column) ToLineColumn(int[] lineStarts, int index)
    {
        var i = Array.BinarySearch(lineStarts, index);
        var line = i >= 0 ? i : ~i - 1;
        return (line + 1, index - lineStarts[line]);
    }

    // 주석 속 예시 코드(`Register(this, &X::Handle)` 같은)를 진입점으로 잡지 않기 위한 근사 판정:
    // 같은 줄 앞에 // 가 있거나, 줄이 * 로 시작하는(블록 주석 안) 경우.
    internal static bool IsInComment(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var before = text[lineStart..index];
        return before.Contains("//", StringComparison.Ordinal) || before.TrimStart().StartsWith('*') ||
               before.TrimStart().StartsWith("/*", StringComparison.Ordinal);
    }

    // 1부터 세는 줄, 0부터 세는 열(UTF-16 코드 단위, LSP 기본과 같음)
    internal static (int Line, int Column) ToLineColumn(string text, int index) => ToLineColumn(LineStarts(text), index);

    // 패킷 핸들러는 두 번째 인자 타입이 패킷이라, 시그니처에서 C2ZMove 같은 이름을 뽑는다.
    private static string MakeLabel(EntryCandidate candidate, TraceNode node)
    {
        if (!string.IsNullOrEmpty(candidate.Label))
        {
            return candidate.Label;
        }
        var signatureEnd = node.Code.IndexOf('{');
        var signature = signatureEnd > 0 ? node.Code[..signatureEnd] : node.Code;
        var packet = PacketParam.Match(signature);
        return packet.Success ? packet.Groups[1].Value : candidate.Method;
    }

    private static readonly Regex PacketParam = new(@"const\s+(?:\w+::)*([A-Z]\d?2[A-Z]\w+|[A-Z]2[A-Z]\w+)\s*&", RegexOptions.Compiled);

    private static string MakeGroup(EntryCandidate candidate, string prefix)
    {
        var rel = candidate.RelativeFile[prefix.Length..];
        var top = rel.Split('/')[0];
        var cls = candidate.ClassName ?? Path.GetFileNameWithoutExtension(candidate.File);
        return top == rel ? cls : $"{top} / {cls}";
    }

    private static string CommonDirectoryPrefix(IEnumerable<string> files)
    {
        string? prefix = null;
        foreach (var f in files)
        {
            var dir = f.Contains('/') ? f[..(f.LastIndexOf('/') + 1)] : "";
            if (prefix is null)
            {
                prefix = dir;
                continue;
            }
            var n = 0;
            while (n < prefix.Length && n < dir.Length && char.ToLowerInvariant(prefix[n]) == char.ToLowerInvariant(dir[n]))
            {
                n++;
            }
            prefix = prefix[..n];
        }
        prefix ??= "";
        var slash = prefix.LastIndexOf('/');
        return slash >= 0 ? prefix[..(slash + 1)] : "";
    }

}
