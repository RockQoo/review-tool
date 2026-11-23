using System.Text.Json.Nodes;
using ReviewTool.Cpp.Lsp;

namespace ReviewTool.Cpp;

// clangd 하나를 띄워 두고 이 도구에 필요한 요청만 감싼다.
public sealed class ClangdSession : IAsyncDisposable
{
    private readonly LspClient client_;
    private readonly HashSet<string> openDocs_ = [];
    private readonly object indexLock_ = new();
    private TaskCompletionSource indexDone_ = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool indexStarted_;

    public event Action<string>? IndexProgress;
    public List<string> StderrTail { get; } = [];

    private ClangdSession(LspClient client)
    {
        client_ = client;
        client_.Notification += OnNotification;
        client_.StandardError += line =>
        {
            lock (StderrTail)
            {
                StderrTail.Add(line);
                if (StderrTail.Count > 50)
                {
                    StderrTail.RemoveAt(0);
                }
            }
        };
    }

    public static string FindClangd(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : throw new FileNotFoundException("clangd 경로가 잘못됨", configured);
        }
        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length > 0)
            {
                candidates.Add(Path.Combine(dir, OperatingSystem.IsWindows() ? "clangd.exe" : "clangd"));
            }
        }
        candidates.Add(@"C:\Program Files\LLVM\bin\clangd.exe");
        return candidates.FirstOrDefault(File.Exists)
               ?? throw new FileNotFoundException("clangd 를 찾지 못함. LLVM 을 설치하거나 설정의 cpp.clangd 에 경로를 적으세요.");
    }

    public static async Task<ClangdSession> StartAsync(string clangdPath, string rootDirectory, string compileDbDirectory,
                                                       CancellationToken cancellationToken)
    {
        var args = new[]
        {
            $"--compile-commands-dir={compileDbDirectory}",
            "--background-index",
            "--log=error",
            $"-j={Math.Max(2, Environment.ProcessorCount - 1)}",
            "--header-insertion=never",
            "--pch-storage=memory",
        };
        var client = LspClient.Start(clangdPath, args, compileDbDirectory);
        var session = new ClangdSession(client);

        var init = new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["rootUri"] = PathUri.ToUri(rootDirectory),
            ["capabilities"] = new JsonObject
            {
                ["window"] = new JsonObject { ["workDoneProgress"] = true },
                ["textDocument"] = new JsonObject
                {
                    ["callHierarchy"] = new JsonObject { ["dynamicRegistration"] = false },
                    // 클래스 멤버를 children 으로 받기 위해(TypePass)
                    ["documentSymbol"] = new JsonObject { ["hierarchicalDocumentSymbolSupport"] = true },
                },
            },
        };
        try
        {
            await client.RequestAsync("initialize", init, cancellationToken, TimeSpan.FromSeconds(30));
            await client.NotifyAsync("initialized", new JsonObject(), cancellationToken);
            return session;
        }
        catch
        {
            // 호출자의 await using 은 반환이 성공해야 성립하므로 여기서 clangd 를 직접 끝낸다
            await session.DisposeAsync();
            throw;
        }
    }

    private void OnNotification(string method, JsonNode? parameters)
    {
        if (method != "$/progress" || parameters?["token"]?.ToString() != "backgroundIndexProgress")
        {
            return;
        }
        var value = parameters["value"];
        var kind = value?["kind"]?.GetValue<string>();
        lock (indexLock_)
        {
            if (kind == "begin")
            {
                indexStarted_ = true;
                if (indexDone_.Task.IsCompleted)
                {
                    indexDone_ = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            else if (kind == "end")
            {
                indexDone_.TrySetResult();
            }
        }
        var message = value?["message"]?.GetValue<string>() ?? value?["title"]?.GetValue<string>();
        if (message is not null)
        {
            IndexProgress?.Invoke(message);
        }
    }

    // 백그라운드 색인이 끝날 때까지 기다린다. outgoingCalls 는 색인으로 답하므로 끝나기 전에는 빈 답이 온다.
    // 캐시가 이미 최신이면 시작 알림조차 없으므로, quietPeriod 동안 시작이 없으면 끝난 것으로 본다.
    public async Task<bool> WaitForIndexAsync(TimeSpan quietPeriod, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var waitedQuiet = TimeSpan.Zero;
        while (DateTime.UtcNow < deadline)
        {
            Task done;
            bool started;
            lock (indexLock_)
            {
                done = indexDone_.Task;
                started = indexStarted_;
            }
            if (started)
            {
                var remain = deadline - DateTime.UtcNow;
                var finished = await Task.WhenAny(done, Task.Delay(remain, cancellationToken)) == done;
                if (!finished)
                {
                    return false;
                }
                // 끝난 뒤 곧바로 다시 시작하는 경우(헤더 변경 등)를 잠깐 지켜본다
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                lock (indexLock_)
                {
                    if (indexDone_.Task.IsCompleted)
                    {
                        return true;
                    }
                }
                continue;
            }
            if (waitedQuiet >= quietPeriod)
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            waitedQuiet += TimeSpan.FromMilliseconds(250);
        }
        return false;
    }

    public async Task OpenAsync(string path, CancellationToken cancellationToken)
    {
        if (!openDocs_.Add(path))
        {
            return;
        }
        await client_.NotifyAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = PathUri.ToUri(path),
                ["languageId"] = "cpp",
                ["version"] = 1,
                ["text"] = await File.ReadAllTextAsync(path, cancellationToken),
            },
        }, cancellationToken);
    }

    public async Task CloseAsync(string path, CancellationToken cancellationToken)
    {
        if (!openDocs_.Remove(path))
        {
            return;
        }
        await client_.NotifyAsync("textDocument/didClose", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = PathUri.ToUri(path) },
        }, cancellationToken);
    }

    public async Task<JsonObject?> PrepareCallHierarchyAsync(string path, int line0, int character, CancellationToken cancellationToken)
    {
        var result = await client_.RequestAsync("textDocument/prepareCallHierarchy", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = PathUri.ToUri(path) },
            ["position"] = new JsonObject { ["line"] = line0, ["character"] = character },
        }, cancellationToken, TimeSpan.FromMinutes(2));
        return (result as JsonArray)?.FirstOrDefault() as JsonObject;
    }

    // 그 위치의 이름(함수 호출, 연산자 포함)이 가리키는 정의 위치. 여러 개면 첫 번째. 연산자(<<)처럼 prepareCallHierarchy 가
    // 답하지 않는 자리도 정의로는 갈 수 있다(실측).
    public async Task<(string Path, int Line0, int Col)?> DefinitionAsync(string path, int line0, int character,
                                                                          CancellationToken cancellationToken)
    {
        var result = await client_.RequestAsync("textDocument/definition", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = PathUri.ToUri(path) },
            ["position"] = new JsonObject { ["line"] = line0, ["character"] = character },
        }, cancellationToken, TimeSpan.FromSeconds(30));
        var first = result switch
        {
            JsonArray array => array.FirstOrDefault() as JsonObject,
            JsonObject single => single,
            _ => null,
        };
        // Location(uri, range) 또는 LocationLink(targetUri, targetSelectionRange)
        var uri = (first?["uri"] ?? first?["targetUri"])?.GetValue<string>();
        var start = (first?["range"] ?? first?["targetSelectionRange"])?["start"];
        if (uri is null || start is null)
        {
            return null;
        }
        return (PathUri.ToPath(uri), start["line"]!.GetValue<int>(), start["character"]!.GetValue<int>());
    }

    public async Task<JsonArray> OutgoingCallsAsync(JsonObject item, CancellationToken cancellationToken)
    {
        var result = await client_.RequestAsync("callHierarchy/outgoingCalls", new JsonObject
        {
            ["item"] = item.DeepClone(),
        }, cancellationToken, TimeSpan.FromMinutes(1));
        return result as JsonArray ?? [];
    }

    // 색인에서 이름으로 심볼을 찾는다. clangd 는 정의가 있으면 정의 위치를 준다(파일을 열 필요 없음).
    public async Task<JsonArray> WorkspaceSymbolAsync(string query, CancellationToken cancellationToken)
    {
        var result = await client_.RequestAsync("workspace/symbol", new JsonObject
        {
            ["query"] = query,
        }, cancellationToken, TimeSpan.FromSeconds(30));
        return result as JsonArray ?? [];
    }

    public Task<JsonNode?> RawRequestAsync(string method, JsonNode parameters, CancellationToken cancellationToken) =>
        client_.RequestAsync(method, parameters, cancellationToken);

    public async ValueTask DisposeAsync() => await client_.DisposeAsync();
}

public static class PathUri
{
    public static string ToUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    public static string ToPath(string uri) => Path.GetFullPath(new Uri(uri).LocalPath);
}
