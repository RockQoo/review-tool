using System.Text.RegularExpressions;

namespace ReviewTool.Core;

// 저장소 폴더 순회. 빌드 산출물(bin/obj 등, 수 GB 가 될 수 있음)과 제외 폴더는 들어가기 전에 가지치기한다.
public static class SourceFiles
{
    public static IEnumerable<string> Enumerate(string root, Func<string, bool> fileFilter,
                                                IReadOnlyList<string>? exclude = null, int maxDepth = int.MaxValue)
    {
        var excludeRegex = (exclude ?? []).Select(GlobMatcher.ToRegex).ToArray();
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            string[] files;
            string[] subdirs;
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = depth < maxDepth ? Directory.GetDirectories(dir) : [];
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            foreach (var f in files)
            {
                if (fileFilter(f))
                {
                    yield return f;
                }
            }
            foreach (var d in subdirs.Reverse())
            {
                var rel = Path.GetRelativePath(root, d).Replace('\\', '/');
                if (IsPruned(Path.GetFileName(d), rel, excludeRegex))
                {
                    continue;
                }
                stack.Push((d, depth + 1));
            }
        }
    }

    public static IEnumerable<string> Directories(string root, int maxDepth, IReadOnlyList<string>? exclude = null)
    {
        var excludeRegex = (exclude ?? []).Select(GlobMatcher.ToRegex).ToArray();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            if (depth >= maxDepth)
            {
                continue;
            }
            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            foreach (var d in subdirs)
            {
                var rel = Path.GetRelativePath(root, d).Replace('\\', '/');
                if (IsPruned(Path.GetFileName(d), rel, excludeRegex))
                {
                    continue;
                }
                yield return d;
                queue.Enqueue((d, depth + 1));
            }
        }
    }

    private static bool IsPruned(string name, string rel, Regex[] exclude) =>
        name.StartsWith('.') || IsBuildOutput(name) ||
        // "3rd/**" 같은 제외 glob 은 폴더 안의 아무 파일 경로로 시험한다
        exclude.Any(r => r.IsMatch(rel + "/_"));

    // 빌드 산출물·도구 폴더(bin, obj, .vs, .git, x64, node_modules)가 들어간 경로
    public static bool IsBuildOutput(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/');
        return parts.Any(p => p is "bin" or "obj" or ".vs" or ".git" or "x64" or "node_modules");
    }

    // C++ 소스·헤더
    public static bool IsCode(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cpp" or ".cc" or ".cxx" or ".h" or ".hpp";
}
