using System.Text;
using System.Text.Json;
using LLMW.Writing.Application.Provider;
using LLMW.Writing.Domain.Prompt;
using LLMW.Writing.Domain.Provider;

namespace LLMW.Writing.Infrastructure.Providers;

public sealed class OpenAiCompatibleChatAdapter : IProviderProtocolAdapter
{
    public const string CurrentVersion = "wp14-openai-compatible-chat-v2";
    private readonly ProviderHttpTransport transport;

    public OpenAiCompatibleChatAdapter(ProviderHttpTransport transport)
    {
        this.transport = transport;
    }

    public string AdapterId => "openai_compatible_chat";

    public string AdapterVersion => CurrentVersion;

    public ProtocolKind ProtocolKind => ProtocolKind.OpenAiCompatibleChatCompletions;

    public ProviderPrepareResult Prepare(
        ProviderDefinitionV1 definition,
        ProviderEndpoint endpoint,
        ProviderInvokeRequest request)
    {
        _ = definition;
        if (request.AdapterExtensions.ContainsKey("thinking") && request.Prompt.Tools.Count > 0)
        {
            return new ProviderPrepareResult(null, "DEEPSEEK_THINKING_TOOLS_UNSUPPORTED");
        }

        var path = Combine(endpoint.CanonicalUri, "/v1/chat/completions").AbsolutePath;
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

        var uri = Combine(endpoint.CanonicalUri, "/v1/chat/completions");
        var headers = new Dictionary<string, string>(prepared.Request.NonSecretHeaders, StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer " + secret.Reveal()
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

        var uri = Combine(endpoint.CanonicalUri, "/v1/chat/completions");
        var headers = new Dictionary<string, string>(prepared.Request.NonSecretHeaders, StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer " + secret.Reveal()
        };
        var terminal = false;
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
                yield return new ModelRuntimeEvent(ModelRuntimeEventKind.Completed, null, null, null, null, null, null, null, true);
                terminal = true;
                yield break;
            }

            if (!SseJson.TryParse(frame.Data, out var document, out var parseError))
            {
                yield return parseError;
                yield break;
            }

            using (document)
            {
                if (!document.RootElement.TryGetProperty("choices", out var choices))
                {
                    continue;
                }

                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content))
                    {
                        yield return new ModelRuntimeEvent(ModelRuntimeEventKind.TextDelta, content.GetString(), null, null, null, null, null, null, false);
                    }

                    if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                    {
                        terminal = true;
                    }
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
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return new ProviderInvokeResult(InvocationLifecycle.Rejected, InvocationFailureClass.MalformedProtocol, [], http.ProviderRequestId, null, null, NormalizedUsage.Unknown, [], null, null, "missing-choices", false);
            }

            var message = choices[0].GetProperty("message");
            var content = message.TryGetProperty("content", out var c) ? c.GetString() : null;
            var events = new List<ModelRuntimeEvent>();
            var tools = new List<ToolCallRequest>();
            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in toolCalls.EnumerateArray())
                {
                    var id = call.TryGetProperty("id", out var cid) ? cid.GetString() ?? "" : "";
                    var fn = call.GetProperty("function");
                    var name = fn.GetProperty("name").GetString() ?? "";
                    var args = fn.TryGetProperty("arguments", out var ag) ? ag.GetString() ?? "{}" : "{}";
                    tools.Add(new ToolCallRequest(id, name, args));
                    events.Add(new ModelRuntimeEvent(ModelRuntimeEventKind.ToolCallCompleted, null, id, name, null, args, null, null, false));
                }
            }

            if (content is not null)
            {
                events.Add(new ModelRuntimeEvent(ModelRuntimeEventKind.TextCompleted, content, null, null, null, null, null, null, false));
            }

            var usage = UsageNormalizer.FromOpenAi(root);
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            var idResp = root.TryGetProperty("id", out var rid) ? rid.GetString() : null;
            var finish = choices[0].TryGetProperty("finish_reason", out var fr) ? fr.GetString() : "stop";
            if (finish == "length")
            {
                events.Add(new ModelRuntimeEvent(ModelRuntimeEventKind.Incomplete, content, null, null, null, null, usage, "length", true));
                return new ProviderInvokeResult(InvocationLifecycle.Incomplete, InvocationFailureClass.IncompleteGeneration, events, http.ProviderRequestId, idResp, model, usage, tools, null, null, "length", false);
            }

            events.Add(new ModelRuntimeEvent(ModelRuntimeEventKind.Completed, content, null, null, null, null, usage, null, true));
            return new ProviderInvokeResult(InvocationLifecycle.Completed, InvocationFailureClass.None, events, http.ProviderRequestId, idResp, model, usage, tools, null, null, null, false);
        }
    }

    private static string BuildRequest(ProviderInvokeRequest request, bool stream)
    {
        using var streamOut = new MemoryStream();
        using (var writer = new Utf8JsonWriter(streamOut))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.ModelId.Value);
            if (request.Prompt.ReservedOutputTokens > 0)
            {
                writer.WriteNumber("max_tokens", request.Prompt.ReservedOutputTokens);
            }

            writer.WriteBoolean("stream", stream);
            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", PromptWireMapping.JoinInstructions(request.Prompt));
            writer.WriteEndObject();
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
                    writer.WriteString("type", "function");
                    writer.WritePropertyName("function");
                    writer.WriteStartObject();
                    writer.WriteString("name", tool.ToolName);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters");
                    using var schema = JsonDocument.Parse(tool.ParametersJson);
                    schema.RootElement.WriteTo(writer);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            if (request.AdapterExtensions.TryGetValue("thinking", out var thinking))
            {
                writer.WritePropertyName("thinking");
                writer.WriteStartObject();
                writer.WriteString("type", thinking);
                writer.WriteEndObject();
            }

            if (request.AdapterExtensions.TryGetValue("reasoningEffort", out var effort))
            {
                writer.WriteString("reasoning_effort", effort);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(streamOut.ToArray());
    }

    private static Uri Combine(string endpoint, string path)
    {
        var trimmed = endpoint.TrimEnd('/');
        if (trimmed.EndsWith("/v1", StringComparison.Ordinal))
        {
            return new Uri(trimmed + path["/v1".Length..]);
        }

        return new Uri(trimmed + path);
    }
}

public sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> handler;
    public List<string> Requests { get; } = [];

    public ScriptedHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        this.handler = handler;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add(request.Method + " " + request.RequestUri + " " + body);
        foreach (var header in request.Headers)
        {
            if (HeaderRedaction.IsSensitive(header.Key) && header.Value.Any(value => !string.IsNullOrEmpty(value)))
            {
                // captured only as redacted diagnostics elsewhere; do not store values.
            }
        }

        return handler(request);
    }
}

public sealed class FileProviderDefinitionStore : IProviderDefinitionStore
{
    private readonly string path;
    private readonly object gate = new();

    public FileProviderDefinitionStore(string path)
    {
        this.path = path;
    }

    public IReadOnlyList<ProviderDefinitionV1> List()
    {
        lock (gate)
        {
            return Read().OrderBy(item => item.ProviderDefinitionId.Value, StringComparer.Ordinal).ToArray();
        }
    }

    public ProviderDefinitionV1? FindById(ProviderDefinitionId id)
    {
        lock (gate)
        {
            return Read().FirstOrDefault(item => item.ProviderDefinitionId.Value == id.Value);
        }
    }

    public void Upsert(ProviderDefinitionV1 definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (gate)
        {
            var items = Read().ToList();
            var existing = items.FirstOrDefault(item => item.ProviderDefinitionId.Value == definition.ProviderDefinitionId.Value);
            items.RemoveAll(item => item.ProviderDefinitionId.Value == definition.ProviderDefinitionId.Value);
            items.Add(ProviderDefinitionRevision.Apply(existing, definition));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicWrite(Serialize(items));
        }
    }

    private void AtomicWrite(string json)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(path))
        {
            File.Replace(temp, path, path + ".bak");
            return;
        }

        File.Move(temp, path);
    }

    private List<ProviderDefinitionV1> Read()
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<ProviderDefinitionV1>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            list.Add(Parse(item));
        }

        return list;
    }

    private static ProviderDefinitionV1 Parse(JsonElement item)
    {
        if (!ProtocolKindCodec.TryParse(item.GetProperty("protocolKind").GetString(), out var kind))
        {
            throw new InvalidOperationException("Unknown protocolKind in provider definition store.");
        }
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        if (item.TryGetProperty("modelAliases", out var aliasEl))
        {
            foreach (var alias in aliasEl.EnumerateObject())
            {
                aliases[alias.Name] = alias.Value.GetString() ?? "";
            }
        }

        var extensions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (item.TryGetProperty("adapterExtensions", out var ext))
        {
            foreach (var property in ext.EnumerateObject())
            {
                extensions[property.Name] = property.Value.GetString() ?? "";
            }
        }

        return new ProviderDefinitionV1(
            new ProviderDefinitionId(item.GetProperty("providerDefinitionId").GetString() ?? ""),
            new ProviderRevision(item.GetProperty("revision").GetInt32()),
            item.GetProperty("displayName").GetString() ?? "",
            item.GetProperty("enabled").GetBoolean(),
            kind,
            item.GetProperty("endpoint").GetString() ?? "",
            new CredentialRef(item.GetProperty("credentialRef").GetString() ?? ""),
            item.TryGetProperty("defaultModelId", out var model) ? new ModelId(model.GetString() ?? "") : null,
            aliases,
            item.GetProperty("timeoutMs").GetInt32(),
            item.TryGetProperty("dataPolicy", out var dp) && dp.GetString() == "provider_stored"
                ? ProviderDataBehavior.ProviderStored
                : ProviderDataBehavior.StatelessClientManaged,
            item.GetProperty("routingPriority").GetInt32(),
            item.TryGetProperty("priceSnapshotId", out var price) ? price.GetString() : null,
            extensions,
            item.TryGetProperty("allowInsecureLocalHttp", out var insecure) && insecure.GetBoolean());
    }

    private static string Serialize(IReadOnlyList<ProviderDefinitionV1> items)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var item in items.OrderBy(value => value.ProviderDefinitionId.Value, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", ProviderDefinitionV1.CurrentSchemaVersion);
                writer.WriteString("providerDefinitionId", item.ProviderDefinitionId.Value);
                writer.WriteNumber("revision", item.Revision.Value);
                writer.WriteString("displayName", item.DisplayName);
                writer.WriteBoolean("enabled", item.Enabled);
                writer.WriteString("protocolKind", ProtocolKindCodec.ToDurableValue(item.ProtocolKind));
                writer.WriteString("endpoint", item.Endpoint);
                writer.WriteString("credentialRef", item.CredentialRef.Value);
                if (item.DefaultModelId is { } model)
                {
                    writer.WriteString("defaultModelId", model.Value);
                }

                writer.WritePropertyName("modelAliases");
                writer.WriteStartObject();
                foreach (var alias in item.ModelAliases.OrderBy(value => value.Key, StringComparer.Ordinal))
                {
                    writer.WriteString(alias.Key, alias.Value);
                }

                writer.WriteEndObject();
                writer.WriteNumber("timeoutMs", item.TimeoutMs);
                writer.WriteString("dataPolicy", ProviderDataBehaviorCodec.ToDurableValue(item.DataPolicy));
                writer.WriteNumber("routingPriority", item.RoutingPriority);
                if (!string.IsNullOrEmpty(item.PriceSnapshotId))
                {
                    writer.WriteString("priceSnapshotId", item.PriceSnapshotId);
                }
                writer.WriteBoolean("allowInsecureLocalHttp", item.AllowInsecureLocalHttp);
                writer.WritePropertyName("adapterExtensions");
                writer.WriteStartObject();
                foreach (var ext in item.AdapterExtensions.OrderBy(value => value.Key, StringComparer.Ordinal))
                {
                    writer.WriteString(ext.Key, ext.Value);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
