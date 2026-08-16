using System.Globalization;
using System.Text;
using System.Text.Json;
using LLMW.Writing.Application.Provider;
using LLMW.Writing.Domain.Prompt;
using LLMW.Writing.Domain.Provider;

namespace LLMW.Writing.Infrastructure.Providers;

public sealed class AnthropicMessagesAdapter : IProviderProtocolAdapter
{
    public const string CurrentVersion = "wp14-anthropic-messages-v2";
    public const string ApiVersion = "2023-06-01";
    private readonly ProviderHttpTransport transport;

    public AnthropicMessagesAdapter(ProviderHttpTransport transport)
    {
        this.transport = transport;
    }

    public string AdapterId => "anthropic_messages";

    public string AdapterVersion => CurrentVersion;

    public ProtocolKind ProtocolKind => ProtocolKind.AnthropicMessages;

    public ProviderPrepareResult Prepare(
        ProviderDefinitionV1 definition,
        ProviderEndpoint endpoint,
        ProviderInvokeRequest request)
    {
        _ = definition;
        if (request.Prompt.ReservedOutputTokens <= 0)
        {
            return new ProviderPrepareResult(null, "MAX_OUTPUT_UNKNOWN");
        }

        if (ValidateThinking(request) is { } thinkingError)
        {
            return thinkingError;
        }

        var path = Combine(endpoint.CanonicalUri, "/v1/messages").AbsolutePath;
        var body = BuildRequest(request, request.Stream);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic-version"] = ApiVersion
        };
        return new ProviderPrepareResult(new PreparedProviderRequest("POST", path, body, headers, request.Stream), null);
    }

    public async Task<ProviderInvokeResult> InvokeAsync(
        ProviderDefinitionV1 definition,
        ProviderEndpoint endpoint,
        ResolvedProviderSecret secret,
        ProviderInvokeRequest request,
        CancellationToken cancellationToken)
    {
        var prepared = Prepare(definition, endpoint, request with { Stream = false });
        if (!prepared.Succeeded || prepared.Request is null)
        {
            return new ProviderInvokeResult(
                InvocationLifecycle.FailedBeforeSend, InvocationFailureClass.FailedBeforeSend,
                [], null, null, null, NormalizedUsage.Unknown, [], null, null, prepared.ErrorCode, false);
        }

        var uri = Combine(endpoint.CanonicalUri, "/v1/messages");
        var headers = new Dictionary<string, string>(prepared.Request.NonSecretHeaders, StringComparer.OrdinalIgnoreCase)
        {
            ["x-api-key"] = secret.Reveal()
        };
        var http = await transport.SendAsync(uri, HttpMethod.Post, headers, prepared.Request.CanonicalSemanticBody, TimeSpan.FromMilliseconds(definition.TimeoutMs), cancellationToken)
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
        var prepared = Prepare(definition, endpoint, request with { Stream = true });
        if (!prepared.Succeeded || prepared.Request is null)
        {
            yield return new ModelRuntimeEvent(ModelRuntimeEventKind.Error, null, null, null, null, null, null, prepared.ErrorCode, true);
            yield break;
        }

        var uri = Combine(endpoint.CanonicalUri, "/v1/messages");
        var headers = new Dictionary<string, string>(prepared.Request.NonSecretHeaders, StringComparer.OrdinalIgnoreCase)
        {
            ["x-api-key"] = secret.Reveal()
        };
        var terminal = false;
        var toolArgs = new StringBuilder();
        string? toolId = null;
        string? toolName = null;
        string? stopReason = null;
        NormalizedUsage? streamUsage = null;
        await foreach (var item in transport.ReadSseAsync(uri, headers, prepared.Request.CanonicalSemanticBody, TimeSpan.FromMilliseconds(definition.TimeoutMs), cancellationToken))
        {
            if (item.Failure is { } failure)
            {
                yield return new ModelRuntimeEvent(ModelRuntimeEventKind.Error, null, null, null, null, null, null, failure.FailureClass.ToString(), true);
                yield break;
            }

            if (item.Frame is null)
            {
                continue;
            }

            var frame = item.Frame;
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
                    case "message_start":
                        if (document.RootElement.TryGetProperty("message", out var started) &&
                            started.TryGetProperty("usage", out var startUsage))
                        {
                            streamUsage = NormalizedUsage.Merge(streamUsage, UsageNormalizer.FromAnthropicUsage(startUsage));
                        }

                        break;
                    case "message_delta":
                        if (document.RootElement.TryGetProperty("delta", out var messageDelta) &&
                            messageDelta.TryGetProperty("stop_reason", out var streamedStop) &&
                            streamedStop.ValueKind == JsonValueKind.String)
                        {
                            stopReason = streamedStop.GetString();
                        }

                        if (document.RootElement.TryGetProperty("usage", out var usageEl))
                        {
                            streamUsage = NormalizedUsage.Merge(streamUsage, UsageNormalizer.FromAnthropicUsage(usageEl));
                        }

                        break;
                    case "message_stop":
                        foreach (var terminalEvent in AnthropicStop.ToEvents(stopReason, streamUsage, toolId is not null))
                        {
                            yield return terminalEvent;
                        }

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

            var stop = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
            if (string.IsNullOrEmpty(stop) && tools.Count > 0)
            {
                stop = "tool_use";
            }

            events.AddRange(AnthropicStop.ToEvents(stop, usage, tools.Count > 0));
            var mapped = AnthropicStop.ToResult(stop, usage, http, idResp, model, events, tools, completed);
            return mapped;
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
            foreach (var turn in request.ToolContinuation ?? [])
            {
                writer.WriteStartObject();
                writer.WriteString("role", "assistant");
                writer.WritePropertyName("content");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("type", "tool_use");
                writer.WriteString("id", turn.CallId);
                writer.WriteString("name", turn.ToolName);
                writer.WritePropertyName("input");
                WriteJsonOrString(writer, turn.ArgumentsJson);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WritePropertyName("content");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("type", "tool_result");
                writer.WriteString("tool_use_id", turn.CallId);
                writer.WriteString("content", turn.ResultJson);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

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

            if (request.AdapterExtensions.TryGetValue("thinking", out var thinking))
            {
                writer.WritePropertyName("thinking");
                writer.WriteStartObject();
                if (thinking == "enabled")
                {
                    writer.WriteString("type", "enabled");
                    writer.WriteNumber("budget_tokens", int.Parse(request.AdapterExtensions["thinkingBudgetTokens"], CultureInfo.InvariantCulture));
                }
                else if (thinking == "adaptive")
                {
                    writer.WriteString("type", "adaptive");
                }

                writer.WriteEndObject();
                if (thinking == "adaptive" &&
                    request.AdapterExtensions.TryGetValue("thinkingEffort", out var effort))
                {
                    writer.WritePropertyName("output_config");
                    writer.WriteStartObject();
                    writer.WriteString("effort", effort);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(streamOut.ToArray());
    }

    private static ProviderPrepareResult? ValidateThinking(ProviderInvokeRequest request)
    {
        if (!request.AdapterExtensions.TryGetValue("thinking", out var thinking))
        {
            return null;
        }

        var profile = request.ProtocolProfile;
        if (thinking == "enabled")
        {
            if (profile is null ||
                !CapabilitySupportCodec.IsUsableAsSupported(profile.SupportFor(ProtocolCapabilityNames.ThinkingManual)))
            {
                return new ProviderPrepareResult(null, "THINKING_UNSUPPORTED");
            }

            if (!request.AdapterExtensions.TryGetValue("thinkingBudgetTokens", out var budgetText) ||
                !int.TryParse(budgetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var budget) ||
                budget < 1024 ||
                budget >= request.Prompt.ReservedOutputTokens)
            {
                return new ProviderPrepareResult(null, "THINKING_BUDGET_INVALID");
            }

            return null;
        }

        if (thinking == "adaptive")
        {
            if (profile is null ||
                !CapabilitySupportCodec.IsUsableAsSupported(profile.SupportFor(ProtocolCapabilityNames.ThinkingAdaptive)))
            {
                return new ProviderPrepareResult(null, "THINKING_UNSUPPORTED");
            }

            if (request.AdapterExtensions.TryGetValue("thinkingEffort", out _) &&
                !CapabilitySupportCodec.IsUsableAsSupported(profile.SupportFor(ProtocolCapabilityNames.ThinkingEffort)))
            {
                return new ProviderPrepareResult(null, "THINKING_UNSUPPORTED");
            }

            if (!request.AdapterExtensions.TryGetValue("thinkingEffort", out _))
            {
                return new ProviderPrepareResult(null, "THINKING_UNSUPPORTED");
            }

            return null;
        }

        return new ProviderPrepareResult(null, "THINKING_UNSUPPORTED");
    }

    private static void WriteJsonOrString(Utf8JsonWriter writer, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            document.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(json);
        }
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
}

internal static class AnthropicStop
{
    public static IReadOnlyList<ModelRuntimeEvent> ToEvents(string? stopReason, NormalizedUsage? usage, bool hasTools)
    {
        return stopReason switch
        {
            "max_tokens" or "model_context_window_exceeded" =>
                [new ModelRuntimeEvent(ModelRuntimeEventKind.Incomplete, null, null, null, null, null, usage, stopReason, true)],
            "refusal" =>
                [new ModelRuntimeEvent(ModelRuntimeEventKind.Refusal, null, null, null, null, null, usage, "refusal", true)],
            "pause_turn" =>
                [new ModelRuntimeEvent(ModelRuntimeEventKind.Incomplete, null, null, null, null, null, usage, "pause_turn", true)],
            "tool_use" =>
                [new ModelRuntimeEvent(ModelRuntimeEventKind.Completed, null, null, null, null, null, usage, "tool_use", true)],
            _ =>
                hasTools
                    ? [new ModelRuntimeEvent(ModelRuntimeEventKind.Completed, null, null, null, null, null, usage, stopReason ?? "tool_use", true)]
                    : [new ModelRuntimeEvent(ModelRuntimeEventKind.Completed, null, null, null, null, null, usage, null, true)]
        };
    }

    public static ProviderInvokeResult ToResult(
        string? stopReason,
        NormalizedUsage usage,
        ProviderHttpResult http,
        string? responseId,
        string? model,
        IReadOnlyList<ModelRuntimeEvent> events,
        IReadOnlyList<ToolCallRequest> tools,
        string text)
    {
        return stopReason switch
        {
            "max_tokens" or "model_context_window_exceeded" =>
                new ProviderInvokeResult(InvocationLifecycle.Incomplete, InvocationFailureClass.IncompleteGeneration, events, http.ProviderRequestId, responseId, model, usage, tools, null, null, stopReason, false),
            "refusal" =>
                new ProviderInvokeResult(InvocationLifecycle.Rejected, InvocationFailureClass.ProviderRefusal, events, http.ProviderRequestId, responseId, model, usage, tools, null, text, "refusal", false),
            "pause_turn" =>
                new ProviderInvokeResult(InvocationLifecycle.Incomplete, InvocationFailureClass.IncompleteGeneration, events, http.ProviderRequestId, responseId, model, usage, tools, null, null, "pause_turn", false),
            "tool_use" =>
                new ProviderInvokeResult(InvocationLifecycle.Completed, InvocationFailureClass.None, events, http.ProviderRequestId, responseId, model, usage, tools, null, null, "tool_use", false),
            _ =>
                tools.Count > 0
                    ? new ProviderInvokeResult(InvocationLifecycle.Completed, InvocationFailureClass.None, events, http.ProviderRequestId, responseId, model, usage, tools, null, null, "tool_use", false)
                    : new ProviderInvokeResult(InvocationLifecycle.Completed, InvocationFailureClass.None, events, http.ProviderRequestId, responseId, model, usage, tools, null, null, null, false)
        };
    }
}
