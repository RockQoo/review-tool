using ReviewTool.Core;
using ReviewTool.Cpp;

// 개발용: clangd 가 실제로 무엇을 돌려주는지 본다.
//   review-tool probe <targets/이름.json> <상대경로> <줄(1부터)> <열(0부터)> [workspace 심볼 질의]
internal static class Probe
{
    public static async Task<int> RunAsync(TargetConfig config, WorkPaths paths, string[] args)
    {
        var file = Path.Combine(config.Root, args[2]);
        var line = int.Parse(args[3]) - 1;
        var col = int.Parse(args[4]);
        await using var session = await ClangdSession.StartAsync(ClangdSession.FindClangd(config.Cpp.Clangd), config.Root,
                                                                 paths.CompileDbDirectory, CancellationToken.None);
        await session.OpenAsync(file, CancellationToken.None);
        await session.WaitForIndexAsync(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), CancellationToken.None);
        var item = await session.PrepareCallHierarchyAsync(file, line, col, CancellationToken.None);
        Console.WriteLine("prepare: " + item?.ToJsonString());
        if (item is not null)
        {
            var calls = await session.OutgoingCallsAsync(item, CancellationToken.None);
            Console.WriteLine($"outgoing ({calls.Count}):");
            foreach (var c in calls)
            {
                Console.WriteLine("  " + c?["to"]?.ToJsonString());
            }
        }
        if (args.Length > 5)
        {
            var symbols = await session.WorkspaceSymbolAsync(args[5], CancellationToken.None);
            Console.WriteLine($"symbols ({symbols.Count}):");
            foreach (var s in symbols.Take(10))
            {
                Console.WriteLine("  " + s?.ToJsonString());
            }
        }
        return 0;
    }
}
