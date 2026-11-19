namespace ReviewTool.Core;

// 언어 어댑터가 구현한다. C++ 은 clangd, C# 은 나중에 Roslyn.
public interface ITraceAnalyzer
{
    string Language { get; }

    Task<TraceGraph> AnalyzeAsync(TargetConfig config, WorkPaths paths, IProgress<AnalysisProgress>? progress,
                                  CancellationToken cancellationToken);
}

public sealed record AnalysisProgress(string Stage, string Message, int Done = 0, int Total = 0);

// 이 도구가 대상마다 쓰는 폴더. 원본 저장소가 아니라 이 도구 쪽(data/<이름>/)에 둔다.
public sealed class WorkPaths
{
    public WorkPaths(string dataRoot, string targetName)
    {
        Directory = Path.Combine(dataRoot, targetName);
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }
    public string TraceFile => Path.Combine(Directory, "trace.json");
    // clangd 용 compile_commands.json 과 색인 캐시(.cache/clangd)가 여기 생긴다.
    public string CompileDbDirectory => System.IO.Directory.CreateDirectory(Path.Combine(Directory, "clangd")).FullName;
}
