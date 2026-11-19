namespace ReviewTool.Core;

// 함수가 어느 프로세스(서버) 코드인지. 설정 없이 진입점 묶음 이름의 앞부분(GatewayServer / C2GHandler 의
// GatewayServer)을 프로세스로 보고, 파일 경로에 그 폴더 이름이 있으면 그 프로세스로 본다.
// 어느 프로세스 폴더에도 없는 코드(Common, Core 같은 공용 라이브러리)는 "" 이다.
public sealed class ProcessMap
{
    private readonly List<string> names_;
    private readonly Dictionary<string, string> byFile_ = new(StringComparer.OrdinalIgnoreCase);
    private readonly TraceGraph graph_;

    public ProcessMap(TraceGraph graph)
    {
        graph_ = graph;
        names_ = graph.EntryPoints
            .Select(e => e.Group.Split(" / ", 2))
            .Where(p => p.Length == 2 && p[0].Length > 0)
            .Select(p => p[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> Names => names_;

    public string OfFile(string relativeFile)
    {
        if (names_.Count == 0 || relativeFile.Length == 0)
        {
            return "";
        }
        if (byFile_.TryGetValue(relativeFile, out var cached))
        {
            return cached;
        }
        var segments = relativeFile.Split('/');
        var found = names_.FirstOrDefault(n => segments.Contains(n, StringComparer.OrdinalIgnoreCase)) ?? "";
        return byFile_[relativeFile] = found;
    }

    public string Of(string nodeId) => graph_.Nodes.TryGetValue(nodeId, out var n) ? OfFile(n.File) : "";

    // 화면용 이름: 공용 코드는 "공용"
    public static string Display(string process) => process.Length == 0 ? "공용" : process;
}
