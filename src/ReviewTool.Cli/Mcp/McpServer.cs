using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReviewTool.Cli.Mcp;

// MCP(Model Context Protocol) 서버, stdio 방식. 한 줄에 JSON-RPC 메시지 하나.
// Claude Code·Codex 가 이 프로세스를 띄우고 도구(McpTools)를 부른다. 표준 출력에는 프로토콜 메시지만 쓴다.
public sealed class McpServer(McpTools tools, TextReader input, TextWriter output, TextWriter log)
{
    public const string DefaultProtocolVersion = "2025-06-18";

    // 한글을 XXXX 로 바꾸지 않는다(크기·가독성). 줄바꿈은 그대로 이스케이프되어 한 줄 한 메시지가 유지된다.
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && await input.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            var reply = Handle(line);
            if (reply is not null)
            {
                await output.WriteLineAsync(reply.ToJsonString(Json));
                await output.FlushAsync(cancellationToken);
            }
        }
    }

    // 응답이 필요 없는 알림이면 null.
    public JsonObject? Handle(string line)
    {
        JsonObject? msg;
        try
        {
            msg = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return Error(null, -32700, "JSON 이 아닙니다");
        }
        if (msg is null)
        {
            return Error(null, -32600, "요청 형식이 아닙니다");
        }
        var id = msg["id"]?.DeepClone();
        var method = msg["method"] is JsonValue m && m.TryGetValue<string>(out var s) ? s : null;
        if (method is null)
        {
            return null;   // 응답(우리는 요청을 보내지 않는다)
        }
        var isRequest = msg.ContainsKey("id");
        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(msg["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = tools.List() },
                "tools/call" => Call(msg["params"] as JsonObject),
                _ when !isRequest => null,      // notifications/initialized 등
                _ => throw new MethodNotFound(method),
            };
            return isRequest ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() } : null;
        }
        catch (MethodNotFound ex)
        {
            return Error(id, -32601, $"없는 메서드: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            return Error(id, -32602, ex.Message);
        }
    }

    private static JsonObject Initialize(JsonObject? p)
    {
        var requested = p?["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return new JsonObject
        {
            ["protocolVersion"] = string.IsNullOrEmpty(requested) ? DefaultProtocolVersion : requested,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "review-tool", ["version"] = "0.2" },
            ["instructions"] =
                "C++ 서버 코드의 정적 분석 결과(진입점=패킷 핸들러, 호출 트리, 코드, 호출 근거)를 조회한다. " +
                "list_targets 로 대상을 고르고, get_entrypoints/find_functions 로 함수 id 를 찾은 뒤 " +
                "get_flow(트리) · get_source(코드) · get_flow_context(진입점부터의 경로 전체) · get_impact(영향받는 패킷) · compare(수정 전후)를 쓴다. " +
                "결과의 \"추정\" 연결과 \"멈춤\"(정의 없음·범위 밖 등)은 확인되지 않은 부분이다.",
        };
    }

    private JsonObject Call(JsonObject? p)
    {
        var name = p?["name"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException("name 이 없습니다");
        var args = p?["arguments"] as JsonObject ?? new JsonObject();
        string text;
        var isError = false;
        try
        {
            text = tools.Call(name, args);
        }
        catch (ToolError ex)
        {
            text = ex.Message;
            isError = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            log.WriteLine($"[mcp] {name} 실패: {ex}");
            text = $"실패: {ex.Message}";
            isError = true;
        }
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        };
    }

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private sealed class MethodNotFound(string method) : Exception(method);
}

// 도구 결과로 돌려줄 사용자 오류(대상 없음, 함수 못 찾음 등). 프로토콜 오류가 아니다.
public sealed class ToolError(string message) : Exception(message);
