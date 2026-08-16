using System.Text;
using System.Text.Json;
using LLMW.Writing.Application.Provider;
using LLMW.Writing.Domain.Prompt;
using LLMW.Writing.Domain.Provider;

namespace LLMW.Writing.Infrastructure.Providers;

public sealed class OpenAiResponsesAdapter : IProviderProtocolAdapter
{
    public const string CurrentVersion = "wp14-openai-responses-v2";
    private readonly ProviderHttpTransport transport;

    public OpenAiResponsesAdapter(ProviderHttpTransport transport)
    {
        this.transport = transport;
    }

    public string AdapterId => "openai_responses";

    public string AdapterVersion => CurrentVersion;

    public ProtocolKind ProtocolKind => ProtocolKind.OpenAiResponses;

    public ProviderPrepareResult Prepare(
        ProviderDefinitionV1 definition,
        ProviderEndpoint endpoint,
        ProviderInvokeRequest request)
    {
        _ = definition;
        if (ValidateContinuation(request) is { } continuationError)
        {
            return continuationError;
        }

        var path = Combine(endpoint.CanonicalUri, "/v1/responses").AbsolutePath;
        var body = BuildRequest(request, request.Stream);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(request.ClientRequestId))
        {
            headers["X-Client-Request-Id"] = request.ClientRequestId;
        }

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

        var uri = Combine(endpoint.CanonicalUri, "/v1/responses");
        var headers = new Dictionary<string, string>(prepared.Request.NonSecretHeaders, StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer " + secret.Reveal()
        };
        var http = await transport.SendAsync(uri, HttpMethod.Post, headers, prepared.Request.CanonicalSemanticBody, TimeSpan.FromMilliseconds(definition.TimeoutMs), cancellationToken)
            .ConfigureAwait(false);
        if (http.Lifecycle is InvocationLifecycle.FailedBeforeSend or InvocationLifecycle.OutcomeUnknown or InvocationLifecycle.CancelRequested)
        {
            return Empty(http);
        }

        if (http.StatusCode is < 200 or >= 300)
        {
            return Empty(http);
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

        var uri = Combine(endpoint.CanonicalUri, "/v1/responses");
        var headers = new Dictionary<string, string>(prepared.Request.NonSecretHeaders, StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer " + secret.Reveal()
        };
        var terminal = false;
        var capture = new OpenAiStreamCapture();
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
            if (string.Equals(frame.Data, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            if (!SseJson.TryParse(frame.Data, out var document, out var parseError))
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
                    case "response.output_text.delta":
                        yield return new ModelRuntimeEvent(ModelRuntimeEventKind.TextDelta, Delta(document.RootElement), null, null, null, null, null, null, false);
                        break;
                    case "response.output_item.added":
                    case "response.output_item.done":
                        if (document.RootElement.TryGetProperty("item", out var outputItem))
                        {
                            capture.OnOutputItem(outputItem);
                        }

                        break;
                    case "response.function_call_arguments.delta":
                        var deltaItemId = Id(document.RootElement, "item_id");
                        var deltaText = Delta(document.RootElement);
                        capture.ArgumentsDelta(deltaItemId, deltaText);
                        yield return new ModelRuntimeEvent(
                            ModelRuntimeEventKind.ToolCallArgumentsDelta,
                            null,
                            capture.CallIdFor(deltaItemId, document.RootElement),
                            null,
                            deltaText,
                            null, null, null, false);
                        break;
                    case "response.function_call_arguments.done":
                        var doneItemId = Id(document.RootElement, "item_id");
                        yield return new ModelRuntimeEvent(
                            ModelRuntimeEventKind.ToolCallCompleted,
                            null,
                            capture.CallIdFor(doneItemId, document.RootElement),
                            capture.NameFor(doneItemId, document.RootElement),
                            null,
                            document.RootElement.TryGetProperty("arguments", out var a) ? a.GetString() : "{}",
                            null, null, false);
                        break;
                    case "response.incomplete":
                        yield return new ModelRuntimeEvent(ModelRuntimeEventKind.Incomplete, null, null, null, null, null, null, "incomplete", true, capture.CaptureJson());
                        terminal = true;
                        yield break;
                    case "response.failed":
                    case "error":
                        yield return new ModelRuntimeEvent(ModelRuntimeEventKind.Error, null, null, null, null, null, null, "provider-error", true);
                        terminal = true;
                        yield break;
                    case "response.completed":
                        capture.OnCompleted(document.RootElement);
                        var usageRoot = document.RootElement.TryGetProperty("response", out var resp) ? resp : document.RootElement;
                        yield return new ModelRuntimeEvent(
                            ModelRuntimeEventKind.Completed,
                            null, null, null, null, null,
                            UsageNormalizer.FromOpenAi(usageRoot),
                            null,
                            true,
                            capture.CaptureJson());
                        terminal = true;
                        yield break;
                    default:
                        break;
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
            return new ProviderInvokeResult(InvocationLifecycle.Rejected, InvocationFailureClass.MalformedProtocol, [], http.ProviderRequestId, null, null, NormalizedUsage.Unknown, [], null, null, "malformed", http.DuplicateExecutionRisk);
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("output", out var output) && !root.TryGetProperty("status", out _))
            {
                return new ProviderInvokeResult(InvocationLifecycle.Rejected, InvocationFailureClass.MalformedProtocol, [], http.ProviderRequestId, null, null, NormalizedUsage.Unknown, [], null, null, "missing-output", false);
            }

            var status = root.TryGetProperty("status", out var st) ? st.GetString() : "completed";
            var events = new List<ModelRuntimeEvent>();
            var tools = new List<ToolCallRequest>();
            string? text = null;
            string? refusal = null;
            if (root.TryGetProperty("output", out output))
            {
                foreach (var item in output.EnumerateArray())
                {
                    var type = item.TryGetProperty("type", out var t) ? t.GetString() : "";
                    if (type is "message")
                    {
                        if (item.TryGetProperty("content", out var content))
                        {
                            foreach (var part in content.EnumerateArray())
                            {
                                var partType = part.TryGetProperty("type", out var pt) ? pt.GetString() : "";
                                if (partType is "output_text")
                                {
                                    text = (text ?? "") + (part.GetProperty("text").GetString() ?? "");
                                }
                                else if (partType is "refusal")
                                {
                                    refusal = part.TryGetProperty("refusal", out var r) ? r.GetString() : "refusal";
                                }
                            }
                        }
                    }
                    else if (type is "function_call")
                    {
                        var callId = item.TryGetProperty("call_id", out var cid) ? cid.GetString() ?? "" : "";
                        var name = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                        var args = item.TryGetProperty("arguments", out var ag) ? ag.GetString() ?? "{}" : "{}";
                        tools.Add(new ToolCallRequest(callId, name, args));
                        events.Add(new ModelRuntimeEvent(ModelRuntimeEventKind.ToolCallCompleted, null, callId, name, null, args, null, null, false));
                    }
                }
            }

            if (text is not null)
            {
                events.Add(new ModelRuntimeEvent(ModelRuntimeEventKind.TextCompleted, text, null, null, null, null, null, null, false));
            }

            var usage = UsageNormalizer.FromOpenAi(root);
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            var id = root.TryGetProperty("id", out var rid) ? rid.GetString() : null;
            var capture = root.TryGetProperty("output", out var captured) ? captured.GetRawText() : null;
            if (status == "incomplete")
            {
                events.Add(new ModelRuntimeEvent(ModelRuntimeEventKind.Incomplete, text, null, null, null, null, usage, "incomplete", true));
                return new ProviderInvokeResult(InvocationLifecycle.Incomplete, InvocationFailureClass.IncompleteGeneration, events, http.ProviderRequestId, id, model, usage, tools, null, refusal, "incomplete", false, capture);
            }

            if (refusal is not null)
            {
                events.Add(new ModelRuntimeEvent(ModelRuntimeEventKind.Refusal, refusal, null, null, null, null, usage, "refusal", true));
                return new ProviderInvokeResult(InvocationLifecycle.Rejected, InvocationFailureClass.ProviderRefusal, events, http.ProviderRequestId, id, model, usage, tools, null, refusal, "refusal", false, capture);
            }

            events.Add(new ModelRuntimeEvent(ModelRuntimeEventKind.Completed, text, null, null, null, null, usage, null, true));
            return new ProviderInvokeResult(InvocationLifecycle.Completed, InvocationFailureClass.None, events, http.ProviderRequestId, id, model, usage, tools, null, null, null, false, capture);
        }
    }

    private static string BuildRequest(ProviderInvokeRequest request, bool stream)
    {
        using var streamOut = new MemoryStream();
        using (var writer = new Utf8JsonWriter(streamOut))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.ModelId.Value);
            writer.WriteBoolean("store", false);
            writer.WriteBoolean("stream", stream);
            if (request.Prompt.ReservedOutputTokens > 0)
            {
                writer.WriteNumber("max_output_tokens", request.Prompt.ReservedOutputTokens);
            }
            writer.WriteString("instructions", PromptWireMapping.JoinInstructions(request.Prompt));
            writer.WritePropertyName("input");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", PromptWireMapping.JoinContext(request.Prompt));
            writer.WriteEndObject();
            WriteContinuation(writer, request);
            writer.WriteEndArray();
            if (request.Prompt.Tools.Count > 0)
            {
                writer.WritePropertyName("tools");
                writer.WriteStartArray();
                foreach (var tool in request.Prompt.Tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WriteString("name", tool.ToolName);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters");
                    using var schema = JsonDocument.Parse(tool.ParametersJson);
                    schema.RootElement.WriteTo(writer);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            if (request.Prompt.OutputContract.Kind == OutputContractKind.StructuredJson &&
                request.Prompt.OutputContract.SchemaJson is not null)
            {
                writer.WritePropertyName("text");
                writer.WriteStartObject();
                writer.WritePropertyName("format");
                writer.WriteStartObject();
                writer.WriteString("type", "json_schema");
                writer.WriteString("name", "task_output");
                if (request.AdapterExtensions.TryGetValue("strictStructuredOutput", out var strict) &&
                    strict == "true")
                {
                    writer.WriteBoolean("strict", true);
                }

                writer.WritePropertyName("schema");
                using var schema = JsonDocument.Parse(request.Prompt.OutputContract.SchemaJson);
                schema.RootElement.WriteTo(writer);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            if (request.AdapterExtensions.TryGetValue("reasoningEffort", out var effort))
            {
                writer.WritePropertyName("reasoning");
                writer.WriteStartObject();
                writer.WriteString("effort", effort);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(streamOut.ToArray());
    }

    public ProviderContinuationState MergeContinuation(
        ProviderContinuationState? prior,
        string? captureJson,
        IReadOnlyList<LocalToolExecutionResult> toolResults)
    {
        return AdapterContinuation.Append(
            AdapterId,
            AdapterVersion,
            prior,
            writer =>
            {
                writer.WritePropertyName("items");
                if (!string.IsNullOrWhiteSpace(captureJson))
                {
                    using var document = JsonDocument.Parse(captureJson);
                    document.RootElement.WriteTo(writer);
                }
                else
                {
                    writer.WriteStartArray();
                    writer.WriteEndArray();
                }

                AdapterContinuation.WriteResults(writer, toolResults, "call_id", "output");
            },
            toolResults.Select(item => item.CallId));
    }

    private ProviderPrepareResult? ValidateContinuation(ProviderInvokeRequest request)
    {
        if (request.ContinuationState is { } state &&
            (!string.Equals(state.AdapterId, AdapterId, StringComparison.Ordinal) ||
             !string.Equals(state.AdapterVersion, AdapterVersion, StringComparison.Ordinal)))
        {
            return new ProviderPrepareResult(null, "CONTINUATION_ADAPTER_MISMATCH");
        }

        if (request.AdapterExtensions.ContainsKey("reasoningEffort") &&
            request.Prompt.Tools.Count > 0 &&
            request.ContinuationState is { } continuation &&
            AdapterContinuation.HasTurns(continuation.OpaqueJson) &&
            !AdapterContinuation.HasItemType(continuation.OpaqueJson, "items", "reasoning"))
        {
            return new ProviderPrepareResult(null, "THINKING_WITH_TOOLS_UNSUPPORTED");
        }

        return null;
    }

    private static void WriteContinuation(Utf8JsonWriter writer, ProviderInvokeRequest request)
    {
        if (request.ContinuationState is { } state && AdapterContinuation.HasTurns(state.OpaqueJson))
        {
            using var document = JsonDocument.Parse(state.OpaqueJson);
            foreach (var turn in document.RootElement.GetProperty("turns").EnumerateArray())
            {
                if (turn.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        item.WriteTo(writer);
                    }
                }

                if (turn.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var result in results.EnumerateArray())
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "function_call_output");
                        writer.WriteString("call_id", result.GetProperty("call_id").GetString() ?? "");
                        writer.WriteString("output", result.TryGetProperty("output", out var output) ? output.GetString() ?? "" : "");
                        writer.WriteEndObject();
                    }
                }
            }

            return;
        }

        foreach (var turn in request.ToolContinuation ?? [])
        {
            writer.WriteStartObject();
            writer.WriteString("type", "function_call");
            writer.WriteString("call_id", turn.CallId);
            writer.WriteString("name", turn.ToolName);
            writer.WriteString("arguments", turn.ArgumentsJson);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("type", "function_call_output");
            writer.WriteString("call_id", turn.CallId);
            writer.WriteString("output", string.IsNullOrEmpty(turn.ErrorCode) ? turn.ResultJson : "{\"error\":true,\"code\":\"" + turn.ErrorCode + "\"}");
            writer.WriteEndObject();
        }
    }

    private static ProviderInvokeResult Empty(ProviderHttpResult http) =>
        new(http.Lifecycle, http.FailureClass, [], http.ProviderRequestId, null, null, NormalizedUsage.Unknown, [], null, null, http.RedactedDiagnostics, http.DuplicateExecutionRisk);

    private static Uri Combine(string endpoint, string path)
    {
        var trimmed = endpoint.TrimEnd('/');
        if (trimmed.EndsWith("/v1", StringComparison.Ordinal))
        {
            return new Uri(trimmed + path["/v1".Length..]);
        }

        return new Uri(trimmed + path);
    }

    private static string? Delta(JsonElement element) =>
        element.TryGetProperty("delta", out var delta) ? delta.GetString() : null;

    private static string? Id(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() : null;
}
