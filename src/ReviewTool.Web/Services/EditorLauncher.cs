using System.Diagnostics;

namespace ReviewTool.Web.Services;

// 보던 줄을 편집기에서 연다. 이 도구는 읽기 전용이라 고치는 일은 편집기에서 한다.
// 서버가 내 PC 에서 도는 로컬 도구라 편집기를 직접 실행할 수 있다(127.0.0.1 전용).
public sealed class EditorLauncher(ILogger<EditorLauncher> logger)
{
    public sealed record Editor(string Id, string Name, string Executable);

    private IReadOnlyList<Editor>? editors_;

    public IReadOnlyList<Editor> Available => editors_ ??= Detect();

    public string? Open(string editorId, string file, int line)
    {
        var editor = Available.FirstOrDefault(e => e.Id == editorId) ?? Available.FirstOrDefault();
        if (editor is null)
        {
            return "Visual Studio 나 VS Code 를 찾지 못했습니다.";
        }
        if (!File.Exists(file))
        {
            return $"파일이 없습니다: {file}";
        }
        var psi = new ProcessStartInfo(editor.Executable) { UseShellExecute = false, CreateNoWindow = true };
        ClearHostEnvironment(psi);
        switch (editor.Id)
        {
            case "vs":
                // 열려 있는 VS 인스턴스가 있으면 거기서 연다
                psi.ArgumentList.Add("/edit");
                psi.ArgumentList.Add(file);
                psi.ArgumentList.Add("/command");
                psi.ArgumentList.Add($"Edit.GoTo {Math.Max(1, line)}");
                break;
            default:
                // 셸(cmd)을 거치지 않는다: 경로에 & 같은 문자가 있으면 명령으로 해석될 수 있다
                psi.ArgumentList.Add("--goto");
                psi.ArgumentList.Add($"{file}:{Math.Max(1, line)}");
                break;
        }
        try
        {
            Process.Start(psi)?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "편집기 실행 실패 {Editor}", editor.Name);
            return $"{editor.Name} 실행 실패: {ex.Message}";
        }
    }

    // 이 서버를 VS Code 확장(Claude Code 등) 안에서 띄우면 ELECTRON_RUN_AS_NODE=1 과 VSCODE_* 를 물려받는다.
    // 그대로 Code.exe 를 실행하면 창이 아니라 Node 로 돌아 아무 일 없이 끝난다(실측 2026-11-21). 편집기에는 넘기지 않는다.
    public static void ClearHostEnvironment(ProcessStartInfo psi)
    {
        foreach (var key in psi.Environment.Keys.ToList())
        {
            if (key.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase))
            {
                psi.Environment.Remove(key);
            }
        }
    }

    private static List<Editor> Detect()
    {
        var list = new List<Editor>();
        if (OperatingSystem.IsWindows())
        {
            var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                       @"Microsoft Visual Studio\Installer\vswhere.exe");
            if (File.Exists(vswhere))
            {
                var devenv = RunFirstLine(vswhere, "-latest", "-prerelease", "-products", "*", "-property", "productPath");
                if (devenv.EndsWith("devenv.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(devenv))
                {
                    list.Add(new Editor("vs", "Visual Studio", devenv));
                }
            }
        }
        // VS Code: PATH 의 <설치>\bin\code.cmd 옆 상위 폴더의 Code.exe 를 직접 실행한다
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0 || !File.Exists(Path.Combine(dir, "code.cmd")))
            {
                continue;
            }
            var exe = Path.GetFullPath(Path.Combine(dir, "..", "Code.exe"));
            if (File.Exists(exe))
            {
                list.Add(new Editor("vscode", "VS Code", exe));
                break;
            }
        }
        return list;
    }

    private static string RunFirstLine(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Split('\n').FirstOrDefault()?.Trim() ?? "";
    }
}
