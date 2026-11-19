using ReviewTool.Core;
using ReviewTool.Cpp;

namespace ReviewTool.Web.Services;

// 대상 목록, 저장된 분석 결과, 진행 중인 재분석을 들고 있는 싱글턴.
// 분석은 한 번에 하나만 돈다(clangd 가 무겁다).
public sealed class TraceWorkspace(IConfiguration configuration, IWebHostEnvironment environment, ILogger<TraceWorkspace> logger)
{
    private readonly object lock_ = new();
    private readonly Dictionary<string, TraceGraph> graphs_ = [];
    private readonly Dictionary<string, TraceIndex> indexes_ = [];
    private CancellationTokenSource? running_;

    public event Action? Changed;

    public string RepoRoot { get; } = FindRepoRoot(configuration["ReviewTool:Root"], environment.ContentRootPath);
    public string TargetsDirectory => Path.Combine(RepoRoot, "targets");
    public string DataDirectory => Path.Combine(RepoRoot, "data");

    public string? RunningTarget { get; private set; }
    public string StatusMessage { get; private set; } = "";
    public string? LastError { get; private set; }

    public IReadOnlyList<string> ListTargets() =>
        Directory.Exists(TargetsDirectory)
            ? Directory.EnumerateFiles(TargetsDirectory, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Order().ToList()
            : [];

    public TargetConfig LoadConfig(string name) => TargetConfig.Load(Path.Combine(TargetsDirectory, name + ".json"));

    public string ConfigPath(string name) => Path.Combine(TargetsDirectory, name + ".json");

    // ---- 대상 추가 (폴더 고르기 → 설정 제안 → 저장) ----

    public TargetSuggestion Suggest(string folder) => CppTargetDetector.Suggest(folder);

    public string SaveTarget(TargetConfig config, bool overwrite)
    {
        config.Name = CppTargetDetector.SanitizeName(config.Name);
        var path = config.Save(TargetsDirectory, overwrite);
        lock (lock_)
        {
            graphs_.Remove(config.Name);
            indexes_.Remove(config.Name);
        }
        Changed?.Invoke();
        return path;
    }

    public bool TargetExists(string name) => File.Exists(ConfigPath(CppTargetDetector.SanitizeName(name)));

    // 브라우저는 내 PC 폴더를 못 보므로 서버가 목록을 대신 읽어 준다(이 서버는 127.0.0.1 전용).
    public static IReadOnlyList<FolderEntry> ListFolders(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DriveInfo.GetDrives().Where(d => d.IsReady)
                .Select(d => new FolderEntry(d.Name.TrimEnd('\\'), d.RootDirectory.FullName, false)).ToList();
        }
        var dir = new DirectoryInfo(path);
        if (!dir.Exists)
        {
            return [];
        }
        try
        {
            return dir.EnumerateDirectories()
                .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden) && !d.Name.StartsWith('.') &&
                            d.Name is not ("bin" or "obj" or "node_modules"))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new FolderEntry(d.Name, d.FullName, LooksLikeProject(d)))
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool LooksLikeProject(DirectoryInfo dir)
    {
        try
        {
            return dir.EnumerateFiles().Any(f => f.Extension is ".sln" or ".slnx" or ".vcxproj" ||
                                                 f.Name is "CMakeLists.txt" or "compile_flags.txt" or "compile_commands.json");
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public (TraceGraph Graph, TraceIndex Index)? Get(string name)
    {
        lock (lock_)
        {
            if (graphs_.TryGetValue(name, out var cached))
            {
                return (cached, indexes_[name]);
            }
        }
        var graph = TraceStore.TryLoad(new WorkPaths(DataDirectory, name).TraceFile);
        if (graph is null)
        {
            return null;
        }
        var index = new TraceIndex(graph);
        lock (lock_)
        {
            graphs_[name] = graph;
            indexes_[name] = index;
        }
        return (graph, index);
    }

    public bool StartAnalysis(string name)
    {
        lock (lock_)
        {
            if (RunningTarget is not null)
            {
                return false;
            }
            RunningTarget = name;
            LastError = null;
            StatusMessage = "시작";
            running_ = new CancellationTokenSource();
        }
        var token = running_.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var config = LoadConfig(name);
                var paths = new WorkPaths(DataDirectory, config.Name);
                ITraceAnalyzer analyzer = config.Language switch
                {
                    "cpp" => new CppTraceAnalyzer(),
                    _ => throw new NotSupportedException($"지원하지 않는 언어: {config.Language}"),
                };
                var progress = new Progress<AnalysisProgress>(p =>
                {
                    StatusMessage = p.Total > 0 ? $"{p.Message} ({p.Done}/{p.Total})" : p.Message;
                    Changed?.Invoke();
                });
                var graph = await analyzer.AnalyzeAsync(config, paths, progress, token);
                TraceStore.Save(graph, paths.TraceFile);
                lock (lock_)
                {
                    graphs_[name] = graph;
                    indexes_[name] = new TraceIndex(graph);
                }
                StatusMessage = $"완료: 함수 {graph.Nodes.Count}개, 호출 {graph.Edges.Count}개, {graph.DurationMs / 1000.0:F1}초";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "취소됨";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "분석 실패 {Target}", name);
                LastError = ex.Message;
                StatusMessage = "실패";
            }
            finally
            {
                lock (lock_)
                {
                    RunningTarget = null;
                    running_?.Dispose();
                    running_ = null;
                }
                Changed?.Invoke();
            }
        });
        Changed?.Invoke();
        return true;
    }

    public void CancelAnalysis()
    {
        lock (lock_)
        {
            running_?.Cancel();
        }
    }

    // targets/ 폴더가 있는 곳을 저장소 루트로 본다. dotnet run 이면 src/ReviewTool.Web 에서 위로 올라간다.
    private static string FindRepoRoot(string? configured, string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }
        foreach (var start in new[] { contentRoot, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "targets")))
                {
                    return dir.FullName;
                }
            }
        }
        return contentRoot;
    }
}

public sealed record FolderEntry(string Name, string FullPath, bool IsProject);

// 화면에서 자주 쓰는 조회를 미리 만들어 둔다.
public sealed class TraceIndex
{
    private static readonly List<TraceEdge> None = [];
    private readonly Dictionary<string, List<TraceEdge>> children_;

    public TraceIndex(TraceGraph graph)
    {
        // 엣지는 분석기가 호출자 본문의 소스 순서대로 넣었다
        children_ = graph.Edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.ToList());
        Callers = graph.Edges.GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.Select(e => e.From).Distinct().ToList());
    }

    public Dictionary<string, List<string>> Callers { get; }

    public List<TraceEdge> ChildrenOf(string id) => children_.GetValueOrDefault(id) ?? None;
}
