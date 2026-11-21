using System.Diagnostics;
using ReviewTool.Core;

namespace ReviewTool.Tests;

// 7단계 ④: git 변경 대조(작업 폴더 vs HEAD). 바뀐 줄이 어느 함수에 걸리는지, 어떤 흐름에도 안 걸린 변경
public class GitChangesTests
{
    [Fact]
    public void diff_조각을_줄_범위로()
    {
        var diff = string.Join('\n',
            "diff --git a/src/Zone.cpp b/src/Zone.cpp", "--- a/src/Zone.cpp", "+++ b/src/Zone.cpp",
            "@@ -60 +60 @@ void Zone::Fanout", "-old", "+new",
            "@@ -70,0 +71,2 @@", "+a", "+b",
            "@@ -80,3 +81,0 @@", "-x", "-y", "-z",
            "diff --git a/gone.h b/gone.h", "--- a/gone.h", "+++ /dev/null", "@@ -1,2 +0,0 @@",
            "diff --git a/\"한글 파일.cpp\" b/\"한글 파일.cpp\"", "--- \"a/한글 파일.cpp\"", "+++ \"b/한글 파일.cpp\"", "@@ -5 +5 @@");

        var hunks = GitChanges.ParseDiff(diff);

        Assert.Equal(["src/Zone.cpp:60", "src/Zone.cpp:71-72", "src/Zone.cpp:82 (지운 줄)", "gone.h:1 (지운 줄)", "한글 파일.cpp:5"],
                     hunks.Select(h => h.Where));
    }

    [Fact]
    public void 바뀐_줄을_함수에_맞추고_안_걸린_변경은_이유와_함께()
    {
        var g = new TraceGraph();
        g.Nodes["F"] = new TraceNode { Id = "F", Name = "Zone::Fanout", File = "src/Zone.cpp", StartLine = 56, EndLine = 76 };
        g.Nodes["G"] = new TraceNode { Id = "G", Name = "Zone::Other", File = "src/Zone.cpp", StartLine = 90, EndLine = 95 };
        g.Nodes["X"] = new TraceNode { Id = "X", Name = "std::x", Kind = NodeKind.External };
        g.Inputs.Add(new InputFile { File = "src/Handlers.cpp" });
        var changes = new GitChanges.Result(null,
            [new("src/Zone.cpp", 60, 60, false), new("src\\Zone.cpp", 75, 92, false), new("src/Zone.cpp", 10, 12, false),
             new("src/Handlers.cpp", 3, 3, false), new("config/server.json", 1, 1, false)],
            ["src/New.cpp"]);

        var map = GitChanges.Build(g, changes);

        Assert.Equal(["src/Zone.cpp:60=F", "src\\Zone.cpp:75-92=F,G"], map.Hits.Select(h => $"{h.Hunk.Where}={string.Join(",", h.NodeIds)}"));
        Assert.Equal(3, map.Misses.Count);
        Assert.Contains("함수 본문 밖", map.Misses[0].Reason);       // 분석한 파일인데 함수 밖(10-12)
        Assert.Contains("함수 본문 밖", map.Misses[1].Reason);       // 분석에 쓴 파일(Handlers.cpp)
        Assert.Contains("분석에 쓰지 않은 파일", map.Misses[2].Reason);
        Assert.Equal(["src/New.cpp"], map.Untracked);
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "-C", dir, "-c", "user.name=test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false" }.Concat(args))
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public void 실제_git_저장소에서_읽고_색인을_건드리지_않는다()
    {
        var repo = Directory.CreateTempSubdirectory("rt-git").FullName;
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(repo, "server")).FullName;   // 분석 폴더 = 저장소 하위 폴더
            File.WriteAllText(Path.Combine(root, "a.cpp"), "void A()\n{\n    One();\n}\n");
            File.WriteAllText(Path.Combine(repo, "outside.txt"), "x\n");
            Git(repo, "init", "-q");
            Git(repo, "add", "-A");
            Git(repo, "commit", "-q", "-m", "init");

            Assert.Contains("커밋이 없습니다", GitChanges.Read(Directory.CreateTempSubdirectory("rt-nogit").FullName).Error ?? "아님");

            File.WriteAllText(Path.Combine(root, "a.cpp"), "void A()\n{\n    One();\n    Two();\n}\n");
            File.WriteAllText(Path.Combine(root, "new.cpp"), "int n;\n");
            File.WriteAllText(Path.Combine(repo, "outside.txt"), "y\n");
            var index = Path.Combine(repo, ".git", "index");
            var before = File.GetLastWriteTimeUtc(index);

            var result = GitChanges.Read(root);

            Assert.Null(result.Error);
            Assert.Equal(["a.cpp:4"], result.Hunks.Select(h => h.Where));          // 분석 폴더 밖(outside.txt)은 빠짐
            Assert.Equal(["new.cpp"], result.Untracked);
            Assert.Equal(before, File.GetLastWriteTimeUtc(index));                 // 읽기만(색인을 고쳐 쓰지 않음)
        }
        finally
        {
            foreach (var f in Directory.GetFiles(repo, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);   // .git 안 읽기 전용 파일
            }
            Directory.Delete(repo, recursive: true);
        }
    }
}
