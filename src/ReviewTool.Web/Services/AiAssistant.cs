using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ReviewTool.Core;

namespace ReviewTool.Web.Services;

// 화면에서 받은 질문을 내 PC 의 AI CLI(Claude Code / Codex)로 헤드리스 실행하고 답을 흘려 보낸다.
// 사용자가 "보내기"를 누를 때만 실행한다. 보내는 내용은 화면에 보이는 문맥 + 질문뿐이고,
// AI 는 대상 폴더를 작업 폴더로 받아 읽기만 한다(Claude: 읽기 도구만, Codex: read-only 샌드박스).
public sealed class AiAssistant(ILogger<AiAssistant> logger)
{
    public sealed record Provider(string Id, string Name, string Executable, IReadOnlyList<string> Args,
                                  Func<string, IEnumerable<AiEvent>> Parse);

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);
    private IReadOnlyList<Provider>? providers_;

    public IReadOnlyList<Provider> Available => providers_ ??= Detect();

    public async IAsyncEnumerable<AiEvent> AskAsync(string providerId, string prompt, string workingDirectory,
                                                    [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var provider = Available.FirstOrDefault(p => p.Id == providerId);
        if (provider is null)
        {
            yield return new AiEvent(AiEventKind.Error, "AI CLI 를 찾지 못했습니다(claude 또는 codex 가 PATH 에 있어야 합니다).");
            yield break;
        }
        var psi = new ProcessStartInfo(provider.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Path.GetTempPath(),
        };
        // 인자는 고정값만. 질문·코드는 표준 입력으로 넘긴다(명령줄로 해석될 일이 없게).
        foreach (var a in provider.Args)
        {
            psi.ArgumentList.Add(a);
        }

        var channel = Channel.CreateUnbounded<AiEvent>();
        var stderr = new Queue<string>();
        Process? process = null;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("프로세스를 시작하지 못함");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI CLI 실행 실패 {Provider}", provider.Name);
            channel.Writer.TryWrite(new AiEvent(AiEventKind.Error, $"{provider.Name} 실행 실패: {ex.Message}"));
            channel.Writer.TryComplete();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        Task? pump = null;
        if (process is not null)
        {
            pump = PumpAsync(process, prompt, provider, channel.Writer, stderr, timeout.Token);
        }

        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                yield return ev;
            }
        }
        finally
        {
            if (process is not null)
            {
                Kill(process);
                if (pump is not null)
                {
                    await pump;
                }
                process.Dispose();
            }
        }
    }

    private async Task PumpAsync(Process process, string prompt, Provider provider, ChannelWriter<AiEvent> writer,
                                 Queue<string> stderr, CancellationToken ct)
    {
        using var reg = ct.Register(() => Kill(process));
        var errTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                lock (stderr)
                {
                    stderr.Enqueue(line);
                    if (stderr.Count > 20)
                    {
                        stderr.Dequeue();
                    }
                }
            }
        });
        var finished = false;
        // 입력 쓰기와 출력 읽기를 함께 진행한다. 입력(문맥)이 파이프보다 크고 CLI 가 먼저 출력을 채우면
        // 순서대로 하면 서로 기다리며 멈춘다.
        var inTask = Task.Run(async () =>
        {
            try
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // 프로세스가 먼저 끝났거나 취소: 출력 쪽에서 결과로 알린다
            }
            finally
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                }
            }
        });
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                foreach (var ev in provider.Parse(line))
                {
                    finished |= ev.Kind is AiEventKind.Done or AiEventKind.Error;
                    writer.TryWrite(ev);
                }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await errTask;
            await inTask;
            if (ct.IsCancellationRequested)
            {
                writer.TryWrite(new AiEvent(AiEventKind.Error, "중단했습니다."));
            }
            else if (!finished)
            {
                string tail;
                lock (stderr)
                {
                    tail = string.Join("\n", stderr);
                }
                writer.TryWrite(process.ExitCode == 0
                    ? new AiEvent(AiEventKind.Done, "")
                    : new AiEvent(AiEventKind.Error, $"{provider.Name} 종료 코드 {process.ExitCode}\n{tail}".Trim()));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            writer.TryWrite(new AiEvent(AiEventKind.Error, ct.IsCancellationRequested ? "중단했습니다." : ex.Message));
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static List<Provider> Detect()
    {
        var list = new List<Provider>();
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(d => d.Length > 0).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        path.Add(Path.Combine(home, ".local", "bin"));

        string? Find(string file) => path.Select(d => Path.Combine(d, file)).FirstOrDefault(File.Exists);

        var claude = Find(OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        if (claude is not null)
        {
            list.Add(new Provider("claude", "Claude Code", claude,
            [
                "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                "--tools", "Read,Grep,Glob",          // 읽기 도구만
                "--strict-mcp-config",                // 사용자 MCP 서버는 붙이지 않는다
                "--no-session-persistence",
            ], AiStream.ParseClaude));
        }

        // codex 는 npm 래퍼(codex.cmd)라 cmd 를 거치지 않게 node 로 스크립트를 직접 실행한다.
        var codexCmd = Find(OperatingSystem.IsWindows() ? "codex.cmd" : "codex");
        if (codexCmd is not null)
        {
            var dir = Path.GetDirectoryName(codexCmd)!;
            var script = Path.Combine(dir, "node_modules", "@openai", "codex", "bin", "codex.js");
            var node = File.Exists(Path.Combine(dir, "node.exe")) ? Path.Combine(dir, "node.exe") : Find(OperatingSystem.IsWindows() ? "node.exe" : "node");
            if (OperatingSystem.IsWindows() && File.Exists(script) && node is not null)
            {
                list.Add(new Provider("codex", "Codex", node,
                    [script, "exec", "--json", "--sandbox", "read-only", "--skip-git-repo-check", "-"], AiStream.ParseCodex));
            }
            else if (!OperatingSystem.IsWindows())
            {
                list.Add(new Provider("codex", "Codex", codexCmd,
                    ["exec", "--json", "--sandbox", "read-only", "--skip-git-repo-check", "-"], AiStream.ParseCodex));
            }
        }
        return list;
    }
}
