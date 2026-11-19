using System.Text.Json;

namespace ReviewTool.Core;

public static class TraceStore
{
    public static void Save(TraceGraph graph, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, graph, TargetConfig.JsonOptions);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static TraceGraph? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        using var stream = File.OpenRead(path);
        var graph = JsonSerializer.Deserialize<TraceGraph>(stream, TargetConfig.JsonOptions);
        return graph?.SchemaVersion == TraceGraph.CurrentSchemaVersion ? graph : null;
    }
}
