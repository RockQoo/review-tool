using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReviewTool.Cpp.Lsp;

// 표준 입출력으로 언어 서버와 JSON-RPC(LSP)로 대화한다. 필요한 만큼만 구현했다.
public sealed class LspClient : IAsyncDisposable
{
    private readonly Process process_;
    private readonly Stream input_;
    private readonly Stream output_;
    private readonly SemaphoreSlim writeLock_ = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> pending_ = new();
    private readonly CancellationTokenSource readerCts_ = new();
    private readonly Task readerTask_;
    private long nextId_;

    // 서버가 보내는 알림($/progress 등). 읽기 스레드에서 불린다.
    public event Action<string, JsonNode?>? Notification;
    public event Action<string>? StandardError;

    private LspClient(Process process)
    {
        process_ = process;
        input_ = process.StandardInput.BaseStream;
        output_ = process.StandardOutput.BaseStream;
        readerTask_ = Task.Run(ReadLoopAsync);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                StandardError?.Invoke(e.Data);
            }
        };
        process.BeginErrorReadLine();
    }

    public static LspClient Start(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"실행 실패: {executable}");
        return new LspClient(process);
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken,
                                              TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref nextId_);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending_[id] = tcs;
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };
        await WriteAsync(message, cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        await using (cts.Token.Register(() => tcs.TrySetCanceled()))
        {
            try
            {
                return await tcs.Task;
            }
            finally
            {
                pending_.TryRemove(id, out _);
            }
        }
    }

    public Task NotifyAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
        };
        return WriteAsync(message, cancellationToken);
    }

    private async Task WriteAsync(JsonNode message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await writeLock_.WaitAsync(cancellationToken);
        try
        {
            await input_.WriteAsync(header, cancellationToken);
            await input_.WriteAsync(body, cancellationToken);
            await input_.FlushAsync(cancellationToken);
        }
        finally
        {
            writeLock_.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var token = readerCts_.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var length = await ReadHeaderAsync(token);
                if (length < 0)
                {
                    break;
                }
                var buffer = new byte[length];
                await output_.ReadExactlyAsync(buffer, token);
                Dispatch(JsonNode.Parse(buffer));
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException)
        {
        }
        finally
        {
            foreach (var tcs in pending_.Values)
            {
                tcs.TrySetException(new IOException("언어 서버 연결이 끊김"));
            }
        }
    }

    private async Task<int> ReadHeaderAsync(CancellationToken token)
    {
        var length = -1;
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var read = await output_.ReadAsync(one, token);
            if (read == 0)
            {
                return -1;
            }
            var c = (char)one[0];
            if (c == '\n')
            {
                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.Length == 0)
                {
                    if (length >= 0)
                    {
                        return length;
                    }
                    continue;
                }
                const string prefix = "Content-Length:";
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    length = int.Parse(text[prefix.Length..].Trim());
                }
            }
            else
            {
                line.Append(c);
            }
        }
    }

    private void Dispatch(JsonNode? message)
    {
        if (message is not JsonObject obj)
        {
            return;
        }
        var hasId = obj.TryGetPropertyValue("id", out var idNode) && idNode is not null;
        var method = obj["method"]?.GetValue<string>();

        if (method is null && hasId)
        {
            // 응답
            var id = idNode!.GetValue<long>();
            if (pending_.TryGetValue(id, out var tcs))
            {
                if (obj["error"] is JsonNode error)
                {
                    tcs.TrySetException(new LspException(error["message"]?.GetValue<string>() ?? error.ToJsonString()));
                }
                else
                {
                    tcs.TrySetResult(obj["result"]);
                }
            }
            return;
        }

        if (method is not null && hasId)
        {
            // 서버가 보낸 요청: 진행률 토큰 생성, 설정 조회 등. 답하지 않으면 서버가 기다린다.
            var reply = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idNode!.DeepClone(),
                ["result"] = null,
            };
            _ = WriteAsync(reply, CancellationToken.None);
            return;
        }

        if (method is not null)
        {
            Notification?.Invoke(method, obj["params"]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process_.HasExited)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await RequestAsync("shutdown", null, cts.Token, TimeSpan.FromSeconds(5));
                    await NotifyAsync("exit", null, cts.Token);
                    await process_.WaitForExitAsync(cts.Token);
                }
                catch (Exception)
                {
                    // 정상 종료를 못 하면 아래에서 끝낸다
                }
            }
        }
        finally
        {
            if (!process_.HasExited)
            {
                process_.Kill(entireProcessTree: true);
            }
            await readerCts_.CancelAsync();
            try
            {
                await readerTask_;
            }
            catch (Exception)
            {
            }
            process_.Dispose();
            readerCts_.Dispose();
            writeLock_.Dispose();
        }
    }
}

public sealed class LspException(string message) : Exception(message);
