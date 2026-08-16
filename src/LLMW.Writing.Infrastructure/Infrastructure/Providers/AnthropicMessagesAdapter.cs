using System.Text;
using System.Text.Json;
using LLMW.Writing.Application.Provider;
using LLMW.Writing.Domain.Prompt;
using LLMW.Writing.Domain.Provider;

namespace LLMW.Writing.Infrastructure.Providers;

public sealed class AnthropicMessagesAdapter : IProviderProtocolAdapter
{
    public const string CurrentVersion = "wp14-anthropic-messages-v1";
    public const string ApiVersion = "2023-06-01";
    private readonly ProviderHttpTransport transport;

    public AnthropicMessagesAdapter(ProviderHttpTransport transport)
    {
        this.transport = transport;
    }

    public string AdapterId => "anthropic_messages";

    public string AdapterVersion => CurrentVersion;

    public ProtocolKind ProtocolKind => ProtocolKind.AnthropicMessages;

    public async Task<ProviderInvokeResult> InvokeAsync(
        ProviderDefinitionV1 definition,
        ProviderEndpoint endpoint,
        ResolvedProviderSecret secret,
        ProviderInvokeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Prompt.ReservedOutputTokens <= 0)
        {
            return new ProviderInvokeResult(
                InvocationLifecycle.FailedBeforeSend,
                InvocationFailureClass.FailedBeforeSend,
                [], null, null, null, NormalizedUsage.Unknown, [], null, null, "MAX_OUTPUT_UNKNOWN", false);
        }

        var uri = Combine(endpoint.CanonicalUri, "/v1/messages");
        var body = BuildRequest(request, stream: false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-api-key"] = secret.Reveal(),
            ["anthropic-version"] = ApiVersion
        };
        var http = await transport.SendAsync(uri, HttpMethod.Post, headers, body, TimeSpan.FromMilliseconds(definition.TimeoutMs), cancellationToken)
            .ConfigureAwait(false);
        if (http.Lifecycle is InvocationLifecycle.FailedBeforeSend or InvocationLifecycle.OutcomeUnknown or InvocationLifecycle.CancelRequested ||
            http.StatusCode is < 200 or >= 300)
        {
            return new ProviderInvokeResult(http.Lifecycle, http.FailureClass, [], http.ProviderRequestId, null, null, NormalizedUsage.Unknown, [], null, null, http.RedactedDiagnostics, http.DuplicateExecutionRisk);
        }

        return Parse(http);
    }

    public async IAsyncEnumerable<ModelRuntimeEvent> StreamAsync(
        ProviderDefinitionV1 definition,
        ProviderEndpoint endpoint,
        ResolvedProviderSecret secret,
        ProviderInvokeRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var uri = Combine(endpoint.CanonicalUri, "/v1/messages");
        var body = BuildRequest(request, stream: true);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-api-key"] = secret.Reveal(),
            ["anthropic-version"] = ApiVersion
        };
        var terminal = false;
        var toolArgs = new StringBuilder();
        string? toolId = null;
        string? toolName = null;
        await foreach (var (frame, _) in transport.ReadSseAsync(uri, headers, body, TimeSpan.FromMilliseconds(definition.TimeoutMs), cancellationToken))
        {
            if (!SseJson.TryParse(string.IsNullOrWhiteSpace(frame.Data) ? "{}" : frame.Data, out var document, out var parseError))
            {
                yield return parseError;
                yield break;
            }

            using (document)
            {
                var type = frame.EventType;
                if (document.RootElement.TryGetProperty("type", out var typed))
                {
                    type = typed.GetString() ?? type;
                }

                switch (type)
                {
                    case "content_block_start":
                        if (document.RootElement.TryGetProperty("content_block", out var block) &&
                            block.TryGetProperty("type", out var bt) && bt.GetString() == "tool_use")
                        {
                            toolId = block.TryGetProperty("id", out var id) ? id.GetString() : null;
                            toolName = block.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                            toolArgs.Clear();
                            yield return new ModelRuntimeEvent(ModelRuntimeEventKind.ToolCallStarted, null, toolId, toolName, null, null, null, null, false);
                        }

                        break;
                    case "content_block_delta":
                        if (document.RootElement.TryGetProperty("delta", out var delta))
                        {
                            var dt = delta.TryGetProperty("type", out var dtt) ? dtt.GetString() : "";
                            if (dt == "text_delta")
                            {
                                yield return new ModelRuntimeEvent(ModelRuntimeEventKind.TextDelta, delta.GetProperty("text").GetString(), null, null, null, null, null, null, false);
                            }
                            else if (dt == "input_json_delta")
                            {
                                var partial = delta.TryGetProperty("partial_json", out var pj) ? pj.GetString() ?? "" : "";
                                toolArgs.Append(partial);
                                yield return new ModelRuntimeEvent(ModelRuntimeEventKind.ToolCallArgumentsDelta, null, toolId, toolName, partial, null, null, null, false);
                            }
                        }

                        break;
                    case "content_block_stop":
                        if (toolId is not null)
                        {
                            yield return new ModelRuntimeEvent(ModelRuntimeEventKind.ToolCallCompleted, null, toolId, toolName, null, toolArgs.ToString(), null, null, false);
                            toolId = null;
                            toolName = null;
                            toolArgs.Clear();
                        }

                        break;
                    case "message_stop":
                        yield return new ModelRuntimeEvent(ModelRuntimeEventKind.Completed, null, null, null, null, null, null, null, true);
                        terminal = true;
                        yield break;
                    case "error":
                        yield return new ModelRuntimeEvent(ModelRuntimeEventKind.Error, null, null, null, null, null, null, "provider-error", true);
                        terminal = true;
                        yield break;
                }
            }
        }

        if (!terminal)
        {
            yield return new ModelRuntimeEvent(ModelRuntimeEventKind.Incomplete, null, null, null, null, null, NormalizedUsage.Unknown, "eof-before-terminal", true);
        }
    }

    internal static ProviderInvokeResult Parse(ProviderHttpResult http)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(http.BodyText);
        }
        catch (JsonException)
        {
            return new ProviderInvokeResult(InvocationLifecycle.Rejected, InvocationFailureClass.MalformedProtocol, [], http.ProviderRequestId, null, null, NormalizedUsage.Unknown, [], null, null, "malformed", false);
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("content", out var content))
            {
                return new ProviderInvokeResult(InvocationLifecycle.Rejected, InvocationFailureClass.MalformedProtocol, [], http.ProviderRequestId, null, null, NormalizedUsage.Unknown, [], null, null, "missing-content", false);
            }

            var events = new List<ModelRuntimeEvent>();
            var tools = new List<ToolCallRequest>();
            var text = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var t) ? t.GetString() : "";
                if (type == "text")
                {
                    text.Append(block.GetProperty("text").GetString());
                }
                else if (type == "tool_use")
                {
                    var id = block.GetProperty("id").GetString() ?? "";
                    var name = block.GetProperty("name").GetString() ?? "";
                    var input = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                    tools.Add(new ToolCallRequest(id, name, input));
                    events.Add(new ModelRuntimeEvent(ModelRuntimeEventKind.ToolCallCompleted, null, id, name, null, input, null, null, false));
                }
                else if (type == "thinking")
                {
                    // Hidden/raw thinking is not a Result Artifact and is not persisted here.
                }
            }

            var usage = UsageNormalizer.FromAnthropic(root);
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            var idResp = root.TryGetProperty("id", out var rid) ? rid.GetString() : null;
            var completed = text.ToString();
            if (completed.Length > 0)
            {
                events.Add(new ModelRuntimeEvent(ModelRuntimeEventKind.TextCompleted, completed, null, null, null, null, null, null, false));
            }

            events.Add(new ModelRuntimeEvent(ModelRuntimeEventKind.Completed, completed, null, null, null, null, usage, null, true));
            return new ProviderInvokeResult(InvocationLifecycle.Completed, InvocationFailureClass.None, events, http.ProviderRequestId, idResp, model, usage, tools, LooksJson(completed) ? completed : null, null, null, false);
        }
    }

    private static string BuildRequest(ProviderInvokeRequest request, bool stream)
    {
        using var streamOut = new MemoryStream();
        using (var writer = new Utf8JsonWriter(streamOut))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.ModelId.Value);
            writer.WriteNumber("max_tokens", request.Prompt.ReservedOutputTokens);
            writer.WriteBoolean("stream", stream);
            writer.WritePropertyName("system");
            writer.WriteStartArray();
            foreach (var block in PromptWireMapping.InstructionBlocks(request.Prompt))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", block.Content);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", PromptWireMapping.JoinContext(request.Prompt));
            writer.WriteEndObject();
            writer.WriteEndArray();
            if (request.Prompt.Tools.Count > 0)
            {
                writer.WritePropertyName("tools");
                writer.WriteStartArray();
                foreach (var tool in request.Prompt.Tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", tool.ToolName);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("input_schema");
                    using var schema = JsonDocument.Parse(tool.ParametersJson);
                    schema.RootElement.WriteTo(writer);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            if (request.AdapterExtensions.TryGetValue("thinking", out var thinking) && thinking == "enabled")
            {
                writer.WritePropertyName("thinking");
                writer.WriteStartObject();
                writer.WriteString("type", "enabled");
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(streamOut.ToArray());
    }

    private static Uri Combine(string endpoint, string path)
    {
        var trimmed = endpoint.TrimEnd('/');
        if (trimmed.EndsWith("/v1", StringComparison.Ordinal) || trimmed.EndsWith("/anthropic", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.EndsWith("/anthropic", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri(trimmed + path);
            }

            return new Uri(trimmed + path["/v1".Length..]);
        }

        return new Uri(trimmed + path);
    }

    private static bool LooksJson(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }
}
