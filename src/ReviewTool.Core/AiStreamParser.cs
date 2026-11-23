using System.Text.Json;

namespace ReviewTool.Core;

public enum AiEventKind
{
    Text,    // 답 글자(이어 붙인다)
    Tool,    // AI 가 도구를 씀(파일 읽기 등). 진행 표시용
    Done,
    Error,
}

public sealed record AiEvent(AiEventKind Kind, string Text);

// AI CLI 의 JSON 줄 출력을 화면용 이벤트로. 모르는 줄은 무시한다(CLI 버전이 바뀌어도 깨지지 않게).
public static class AiStreamParser
{
    // claude -p --output-format stream-json --verbose --include-partial-messages
    public static IEnumerable<AiEvent> ParseClaude(string line)
    {
        if (!TryParse(line, out var root))
        {
            yield break;
        }
        switch (Str(root, "type"))
        {
            case "stream_event":
                if (root.TryGetProperty("event", out var ev) && Str(ev, "type") == "content_block_delta"
                    && ev.TryGetProperty("delta", out var delta) && Str(delta, "type") == "text_delta")
                {
                    yield return new AiEvent(AiEventKind.Text, Str(delta, "text"));
                }
                break;
            case "assistant":
                // 완성된 메시지. 글자는 위의 조각으로 이미 받았으니 도구 사용만 본다.
                if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        if (Str(block, "type") == "tool_use")
                        {
                            yield return new AiEvent(AiEventKind.Tool, ToolText(Str(block, "name"), block));
                        }
                    }
                }
                break;
            case "result":
                yield return root.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True
                    ? new AiEvent(AiEventKind.Error, Str(root, "result"))
                    : new AiEvent(AiEventKind.Done, "");
                break;
        }
    }

    // codex exec --json
    public static IEnumerable<AiEvent> ParseCodex(string line)
    {
        if (!TryParse(line, out var root))
        {
            yield break;
        }
        switch (Str(root, "type"))
        {
            case "item.started" or "item.completed":
                if (!root.TryGetProperty("item", out var item))
                {
                    break;
                }
                var itemType = Str(item, "type");
                var completed = Str(root, "type") == "item.completed";
                if (completed && itemType == "agent_message")
                {
                    yield return new AiEvent(AiEventKind.Text, Str(item, "text") + "\n\n");
                }
                else if (!completed && itemType == "command_execution")
                {
                    yield return new AiEvent(AiEventKind.Tool, "명령: " + Str(item, "command"));
                }
                break;
            case "turn.completed":
                yield return new AiEvent(AiEventKind.Done, "");
                break;
            case "turn.failed":
                yield return new AiEvent(AiEventKind.Error,
                    root.TryGetProperty("error", out var fe) ? Str(fe, "message") : "실패");
                break;
            case "error":
                yield return new AiEvent(AiEventKind.Error, Str(root, "message"));
                break;
        }
    }

    private static string ToolText(string name, JsonElement block)
    {
        if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "file_path", "pattern", "path", "command" })
            {
                var v = Str(input, key);
                if (v.Length > 0)
                {
                    return $"{name}: {v}";
                }
            }
        }
        return name;
    }

    private static bool TryParse(string line, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
