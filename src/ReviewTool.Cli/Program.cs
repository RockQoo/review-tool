using ReviewTool.Core;
using ReviewTool.Cpp;

// 사용법:
//   review-tool analyze <targets/이름.json> [--data <폴더>]
//   review-tool show    <targets/이름.json> [--data <폴더>]   저장된 결과 요약
//   review-tool init    <분석할 폴더> [--name 이름] [--force]   폴더를 보고 targets/<이름>.json 을 만든다
//   review-tool mcp     [--targets <폴더>] [--data <폴더>]      MCP 서버(stdio). Claude Code·Codex 에서 분석 결과 조회
return await CliMain.RunAsync(args);

internal static class CliMain
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length >= 2 && args[0] == "init")
        {
            return Init(args);
        }
        if (args.Length >= 1 && args[0] == "mcp")
        {
            return await RunMcpAsync(args);
        }
        if (args.Length < 2 || args[0] is not ("analyze" or "show" or "probe"))
        {
            Console.Error.WriteLine("사용법: review-tool init <폴더> [--name 이름] [--force]");
            Console.Error.WriteLine("        review-tool analyze|show <targets/이름.json> [--data <폴더>]");
            return 2;
        }

        var config = TargetConfig.Load(args[1]);
        var dataRoot = Option(args, "--data") ?? DefaultDataRoot(config);
        var paths = new WorkPaths(dataRoot, config.Name);

        if (args[0] == "probe")
        {
            return await Probe.RunAsync(config, paths, args);
        }

        if (args[0] == "show")
        {
            var saved = TraceStore.TryLoad(paths.TraceFile);
            if (saved is null)
            {
                Console.Error.WriteLine($"저장된 결과가 없음: {paths.TraceFile}");
                return 1;
            }
            PrintSummary(saved);
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var progress = new Progress<AnalysisProgress>(p =>
        {
            var count = p.Total > 0 ? $" ({p.Done}/{p.Total})" : "";
            Console.WriteLine($"[{p.Stage}]{count} {p.Message}");
        });
        ITraceAnalyzer analyzer = config.Language switch
        {
            "cpp" => new CppTraceAnalyzer(),
            _ => throw new NotSupportedException($"지원하지 않는 언어: {config.Language}"),
        };
        var graph = await analyzer.AnalyzeAsync(config, paths, progress, cts.Token);
        SnapshotStore.KeepPrevious(paths);
        TraceStore.Save(graph, paths.TraceFile);
        PrintSummary(graph);
        Console.WriteLine($"저장: {paths.TraceFile}");
        return 0;
    }

    // MCP 서버(stdio). 표준 출력은 프로토콜 전용이라 안내·오류는 표준 오류로만 쓴다.
    private static async Task<int> RunMcpAsync(string[] args)
    {
        var repo = FindRepoRoot();
        var targets = Option(args, "--targets") ?? Path.Combine(repo, "targets");
        var data = Option(args, "--data") ?? Path.Combine(repo, "data");
        var utf8 = new System.Text.UTF8Encoding(false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        await using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false };
        Console.Error.WriteLine($"[review-tool mcp] 대상 {targets} · 결과 {data}");
        var server = new ReviewTool.Cli.Mcp.McpServer(new ReviewTool.Cli.Mcp.McpTools(targets, data), input, output, Console.Error);
        await server.RunAsync(CancellationToken.None);
        return 0;
    }

    private static void PrintSummary(TraceGraph graph)
    {
        Console.WriteLine($"대상 {graph.TargetName} · 진입점 {graph.EntryPoints.Count} · 노드 {graph.Nodes.Count} · 호출 {graph.Edges.Count} · {graph.DurationMs / 1000.0:F1}초");
        foreach (var group in graph.EntryPoints.GroupBy(e => e.Group))
        {
            Console.WriteLine($"  {group.Key}: {string.Join(", ", group.Select(e => e.Label))}");
        }
        foreach (var d in graph.Diagnostics)
        {
            Console.WriteLine($"  ! {d}");
        }
    }

    private static int Init(string[] args)
    {
        var suggestion = CppTargetDetector.Suggest(args[1], Option(args, "--name"));
        var config = suggestion.Config;
        var targets = Path.Combine(FindRepoRoot(), "targets");
        string path;
        try
        {
            path = config.Save(targets, overwrite: args.Contains("--force"));
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"{ex.Message} (덮어쓰려면 --force, 다른 이름은 --name)");
            return 1;
        }
        Console.WriteLine($"대상 {config.Name}: {config.Root}");
        Console.WriteLine($"  컴파일 옵션: {config.Cpp.CompileFrom} · 범위 {(config.Include.Count == 0 ? "전체" : string.Join(", ", config.Include))}" +
                          $"{(config.Exclude.Count > 0 ? " 제외 " + string.Join(", ", config.Exclude) : "")} · .cpp {suggestion.SourceCount}개");
        foreach (var p in suggestion.Presets)
        {
            Console.WriteLine($"  [{(p.Enabled ? "x" : " ")}] {p.Preset.Name}: {p.Matches}개 {string.Join(", ", p.Samples)}");
        }
        foreach (var n in suggestion.Notes)
        {
            Console.WriteLine($"  - {n}");
        }
        Console.WriteLine($"저장: {path}");
        Console.WriteLine($"분석: review-tool analyze \"{path}\"");
        return 0;
    }

    // ReviewTool.slnx 가 있는 폴더. 없으면 현재 폴더.
    private static string FindRepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ReviewTool.slnx")))
                {
                    return dir.FullName;
                }
            }
        }
        return Environment.CurrentDirectory;
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // 설정 파일이 targets/ 아래에 있으면 그 옆의 data/ 를 쓴다.
    public static string DefaultDataRoot(TargetConfig config)
    {
        var dir = Path.GetDirectoryName(config.SourcePath!)!;
        var repo = Path.GetFileName(dir) == "targets" ? Path.GetDirectoryName(dir)! : dir;
        return Path.Combine(repo, "data");
    }
}
