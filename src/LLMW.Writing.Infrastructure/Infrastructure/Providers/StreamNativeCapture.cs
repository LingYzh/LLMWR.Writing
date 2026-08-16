using System.Text;
using System.Text.Json;
using LLMW.Writing.Domain.Provider;

namespace LLMW.Writing.Infrastructure.Providers;

internal sealed class OpenAiStreamCapture
{
    private readonly Dictionary<string, FunctionCall> byItemId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> rawById = new(StringComparer.Ordinal);
    private readonly List<string> order = [];
    private readonly List<string> anonymous = [];

    public string? OutputJson { get; private set; }

    public void OnOutputItem(JsonElement item)
    {
        var raw = item.GetRawText();
        var id = Prop(item, "id");
        if (string.IsNullOrEmpty(id))
        {
            anonymous.Add(raw);
            return;
        }

        if (!rawById.ContainsKey(id))
        {
            order.Add(id);
        }

        rawById[id] = raw;
        if ((Prop(item, "type") ?? "") == "function_call")
        {
            byItemId[id] = new FunctionCall(Prop(item, "call_id"), Prop(item, "name"), Prop(item, "arguments") ?? "");
        }
    }

    public void ArgumentsDelta(string? itemId, string? delta)
    {
        if (string.IsNullOrEmpty(itemId) || !byItemId.TryGetValue(itemId, out var call))
        {
            return;
        }

        byItemId[itemId] = call with { Arguments = call.Arguments + (delta ?? "") };
    }

    public string? CallIdFor(string? itemId, JsonElement done)
    {
        var explicitId = Prop(done, "call_id");
        if (!string.IsNullOrEmpty(explicitId))
        {
            return explicitId;
        }

        if (!string.IsNullOrEmpty(itemId) && byItemId.TryGetValue(itemId, out var call) && !string.IsNullOrEmpty(call.CallId))
        {
            return call.CallId;
        }

        return null;
    }

    public string? NameFor(string? itemId, JsonElement done)
    {
        var name = Prop(done, "name");
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        return !string.IsNullOrEmpty(itemId) && byItemId.TryGetValue(itemId, out var call) ? call.Name : null;
    }

    public void OnCompleted(JsonElement root)
    {
        var response = root.TryGetProperty("response", out var nested) ? nested : root;
        if (response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            OutputJson = output.GetRawText();
            foreach (var item in output.EnumerateArray())
            {
                OnOutputItem(item);
            }
        }
    }

    public string CaptureJson() => OutputJson ?? "[" + string.Join(",", order.Select(id => rawById[id]).Concat(anonymous)) + "]";

    private static string? Prop(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record FunctionCall(string? CallId, string? Name, string Arguments);
}

internal sealed class AnthropicStreamCapture
{
    private readonly Dictionary<int, Block> blocks = [];

    public bool HasToolUse => blocks.Values.Any(item => item.Type == "tool_use");

    public void Start(int index, JsonElement contentBlock)
    {
        var type = contentBlock.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        blocks[index] = new Block(
            type,
            contentBlock.TryGetProperty("id", out var id) ? id.GetString() : null,
            contentBlock.TryGetProperty("name", out var name) ? name.GetString() : null,
            contentBlock.TryGetProperty("thinking", out var thinking) ? thinking.GetString() ?? "" : "",
            contentBlock.TryGetProperty("signature", out var signature) ? signature.GetString() : null,
            contentBlock.TryGetProperty("data", out var data) ? data.GetString() : null,
            contentBlock.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "",
            contentBlock.TryGetProperty("input", out var input) ? input.GetRawText() : "{}");
    }

    public void Delta(int index, JsonElement delta)
    {
        if (!blocks.TryGetValue(index, out var block))
        {
            return;
        }

        var type = delta.TryGetProperty("type", out var dt) ? dt.GetString() : "";
        if (type == "text_delta" && delta.TryGetProperty("text", out var text))
        {
            blocks[index] = block with { Text = block.Text + (text.GetString() ?? "") };
        }
        else if (type == "thinking_delta" && delta.TryGetProperty("thinking", out var thinking))
        {
            blocks[index] = block with { Thinking = block.Thinking + (thinking.GetString() ?? "") };
        }
        else if (type == "signature_delta" && delta.TryGetProperty("signature", out var signature))
        {
            blocks[index] = block with { Signature = signature.GetString() };
        }
        else if (type == "input_json_delta" && delta.TryGetProperty("partial_json", out var partial))
        {
            var current = block.InputJson == "{}" ? "" : block.InputJson;
            blocks[index] = block with { InputJson = current + (partial.GetString() ?? "") };
        }
    }

    public Block? Get(int index) => blocks.TryGetValue(index, out var block) ? block : null;

    public string CaptureJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var pair in blocks.OrderBy(item => item.Key))
            {
                var block = pair.Value;
                writer.WriteStartObject();
                writer.WriteString("type", block.Type);
                if (block.Type == "thinking")
                {
                    writer.WriteString("thinking", block.Thinking);
                    if (!string.IsNullOrEmpty(block.Signature))
                    {
                        writer.WriteString("signature", block.Signature);
                    }
                }
                else if (block.Type == "redacted_thinking")
                {
                    writer.WriteString("data", block.Data ?? "");
                }
                else if (block.Type == "tool_use")
                {
                    writer.WriteString("id", block.Id ?? "");
                    writer.WriteString("name", block.Name ?? "");
                    writer.WritePropertyName("input");
                    try
                    {
                        using var input = JsonDocument.Parse(string.IsNullOrWhiteSpace(block.InputJson) ? "{}" : block.InputJson);
                        input.RootElement.WriteTo(writer);
                    }
                    catch (JsonException)
                    {
                        writer.WriteStartObject();
                        writer.WriteEndObject();
                    }
                }
                else
                {
                    writer.WriteString("text", block.Text);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal sealed record Block(
        string Type,
        string? Id,
        string? Name,
        string Thinking,
        string? Signature,
        string? Data,
        string Text,
        string InputJson);
}

internal sealed class ChatStreamCapture
{
    private readonly SortedDictionary<int, ChatTool> tools = [];
    private readonly StringBuilder content = new();
    private readonly StringBuilder reasoning = new();

    public void ContentDelta(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            content.Append(text);
        }
    }

    public void ReasoningDelta(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            reasoning.Append(text);
        }
    }

    public void ToolDelta(JsonElement call)
    {
        var index = call.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var value) ? value : tools.Count;
        if (!tools.TryGetValue(index, out var current))
        {
            current = new ChatTool(null, null, new StringBuilder());
        }

        var id = call.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        string? name = null;
        string? args = null;
        if (call.TryGetProperty("function", out var fn))
        {
            name = fn.TryGetProperty("name", out var n) ? n.GetString() : null;
            args = fn.TryGetProperty("arguments", out var a) ? a.GetString() : null;
        }

        if (!string.IsNullOrEmpty(args))
        {
            current.Arguments.Append(args);
        }

        tools[index] = current with
        {
            Id = id ?? current.Id,
            Name = name ?? current.Name
        };
    }

    public IReadOnlyList<ModelRuntimeEvent> CompletedToolEvents()
    {
        var events = new List<ModelRuntimeEvent>();
        foreach (var tool in tools.Values)
        {
            if (string.IsNullOrEmpty(tool.Id) || string.IsNullOrEmpty(tool.Name))
            {
                continue;
            }

            events.Add(new ModelRuntimeEvent(
                ModelRuntimeEventKind.ToolCallCompleted,
                null,
                tool.Id,
                tool.Name,
                null,
                tool.Arguments.ToString(),
                null,
                null,
                false));
        }

        return events;
    }

    public string CaptureJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("role", "assistant");
            if (content.Length == 0)
            {
                writer.WriteNull("content");
            }
            else
            {
                writer.WriteString("content", content.ToString());
            }

            if (reasoning.Length > 0)
            {
                writer.WriteString("reasoning_content", reasoning.ToString());
            }

            if (tools.Count > 0)
            {
                writer.WritePropertyName("tool_calls");
                writer.WriteStartArray();
                foreach (var tool in tools.Values)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", tool.Id ?? "");
                    writer.WriteString("type", "function");
                    writer.WritePropertyName("function");
                    writer.WriteStartObject();
                    writer.WriteString("name", tool.Name ?? "");
                    writer.WriteString("arguments", tool.Arguments.ToString());
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed record ChatTool(string? Id, string? Name, StringBuilder Arguments);
}
