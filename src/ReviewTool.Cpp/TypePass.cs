using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReviewTool.Core;

namespace ReviewTool.Cpp;

// 흐름에 나온 클래스(와 그 부모)의 선언: 멤버는 clangd documentSymbol, 부모는 선언 줄의 소스 텍스트.
// 호출 조회가 모두 끝난 뒤에 돈다. 파일을 열면 동적 색인이 호출 기록을 가릴 수 있어서(CLAUDE.md) 순서가 중요하다.
internal static partial class TypePass
{
    public const int MaxTypes = 400;

    public sealed record Result(int Collected, int Missing);

    private const int KindClass = 5, KindStruct = 23, KindMethod = 6, KindField = 8, KindConstructor = 9,
                      KindVariable = 13, KindProperty = 7, KindFunction = 12;

    public static async Task<Result> CollectAsync(TargetConfig config, TraceGraph graph, ClangdSession session,
                                                  Func<string, bool> filter, Func<string, string[]> readLines, CancellationToken ct)
    {
        graph.Types.Clear();
        var wanted = graph.Nodes.Values.Select(SystemMap.ClassOf)
            .Where(c => c.Length > 0 && !c.StartsWith('['))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var queue = new Queue<string>(wanted);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var located = new List<(string Name, string Path, string Rel, int Line0, string Kind)>();
        var missing = 0;
        while (queue.Count > 0 && located.Count < MaxTypes)
        {
            ct.ThrowIfCancellationRequested();
            var name = queue.Dequeue();
            if (!done.Add(name))
            {
                continue;
            }
            var found = await LocateAsync(config, session, filter, readLines, name, ct);
            if (found is null)
            {
                missing++;
                continue;
            }
            located.Add((name, found.Value.Path, found.Value.Rel, found.Value.Line0, found.Value.Kind));
            // 부모도 그린다(분석 범위 안에 있으면). 부모 이름은 짧은 이름이라 같은 이름공간을 먼저 본다.
            foreach (var b in Bases(readLines(found.Value.Path), found.Value.Line0, name.Split("::")[^1]))
            {
                var ns = name.Contains("::") ? name[..name.LastIndexOf("::", StringComparison.Ordinal)] : "";
                var candidate = ns.Length > 0 && !b.Contains("::") ? ns + "::" + b : b;
                if (!done.Contains(candidate) && !done.Contains(b))
                {
                    queue.Enqueue(candidate);
                }
            }
        }

        // 파일마다 한 번 열어 심볼을 받는다
        foreach (var byFile in located.GroupBy(l => l.Path, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            JsonArray symbols;
            try
            {
                await session.OpenAsync(byFile.Key, ct);
                symbols = await session.RawRequestAsync("textDocument/documentSymbol", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = PathUri.ToUri(byFile.Key) },
                }, ct) as JsonArray ?? [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                symbols = [];
            }
            finally
            {
                await session.CloseAsync(byFile.Key, ct);
            }
            var lines = readLines(byFile.Key);
            foreach (var (name, _, rel, line0, kind) in byFile)
            {
                var shortName = name.Split("::")[^1];
                var symbol = FindSymbol(symbols, shortName, line0);
                var type = new TypeInfo
                {
                    Name = name, Kind = kind, File = rel, Line = line0 + 1,
                    Bases = [.. Bases(lines, line0, shortName)],
                };
                if (symbol?["children"] is JsonArray children)
                {
                    foreach (var child in children.OfType<JsonObject>())
                    {
                        var memberKind = child["kind"]?.GetValue<int>() switch
                        {
                            KindField or KindVariable or KindProperty => "field",
                            KindMethod or KindFunction => "method",
                            KindConstructor => "ctor",
                            _ => null,
                        };
                        if (memberKind is null)
                        {
                            continue;
                        }
                        type.Members.Add(new TypeMember
                        {
                            Name = child["name"]?.GetValue<string>() ?? "",
                            Kind = memberKind,
                            Detail = child["detail"]?.GetValue<string>() ?? "",
                        });
                    }
                }
                graph.Types.Add(type);
            }
        }
        graph.Types = graph.Types.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        return new Result(graph.Types.Count, missing);
    }

    private static async Task<(string Path, string Rel, int Line0, string Kind)?> LocateAsync(
        TargetConfig config, ClangdSession session, Func<string, bool> filter, Func<string, string[]> readLines, string name, CancellationToken ct)
    {
        var parts = name.Split("::");
        var shortName = parts[^1];
        var container = string.Join("::", parts[..^1]);
        JsonArray symbols;
        try
        {
            symbols = await session.WorkspaceSymbolAsync(shortName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        foreach (var s in symbols.OfType<JsonObject>())
        {
            var kind = s["kind"]?.GetValue<int>();
            if (s["name"]?.GetValue<string>() != shortName || kind is not (KindClass or KindStruct))
            {
                continue;
            }
            var c = s["containerName"]?.GetValue<string>() ?? "";
            if (c != container && !(container.Length > 0 && c.EndsWith("::" + container, StringComparison.Ordinal)))
            {
                continue;
            }
            var loc = s["location"]!;
            var path = PathUri.ToPath(loc["uri"]!.GetValue<string>());
            var rel = Path.GetRelativePath(config.Root, path).Replace('\\', '/');
            if (rel.StartsWith("..", StringComparison.Ordinal) || !filter(rel))
            {
                continue;
            }
            var line0 = loc["range"]!["start"]!["line"]!.GetValue<int>();
            if (!IsDefinition(readLines(path), line0))
            {
                continue;
            }
            return (path, rel, line0, kind == KindStruct ? "struct" : "class");
        }
        return null;
    }

    // 전방 선언(class X;)이 아니라 본문이 있는 선언인지: '{' 가 ';' 보다 먼저 나온다
    private static bool IsDefinition(string[] lines, int line0)
    {
        for (var l = line0; l < lines.Length && l < line0 + 40; l++)
        {
            foreach (var ch in lines[l])
            {
                if (ch == '{')
                {
                    return true;
                }
                if (ch == ';')
                {
                    return false;
                }
            }
        }
        return false;
    }

    internal static IEnumerable<string> Bases(string[] lines, int line0, string shortName)
    {
        var text = string.Join('\n', lines.Skip(line0).Take(20));
        var m = Regex.Match(text, $@"\b(?:class|struct)\s+(?:\w+\s+)*?{Regex.Escape(shortName)}(?:\s+final)?\s*:(?!:)\s*(?<bases>[^{{;]+)\{{");
        return m.Success ? LinkPass.BaseNames(m.Groups["bases"].Value).Distinct() : [];
    }

    private static JsonObject? FindSymbol(JsonArray symbols, string name, int line0)
    {
        foreach (var s in symbols.OfType<JsonObject>())
        {
            var start = s["range"]?["start"]?["line"]?.GetValue<int>() ?? -1;
            var end = s["range"]?["end"]?["line"]?.GetValue<int>() ?? -1;
            if (s["name"]?.GetValue<string>() == name && s["kind"]?.GetValue<int>() is KindClass or KindStruct &&
                start <= line0 && line0 <= end)
            {
                return s;
            }
            if (s["children"] is JsonArray children && start <= line0 && line0 <= end && FindSymbol(children, name, line0) is { } inner)
            {
                return inner;
            }
        }
        return null;
    }
}
