using System.Runtime.CompilerServices;
using System.Text;
using LLMW.Writing.Domain.Prompt;
using LLMW.Writing.Domain.Provider;

namespace LLMW.Writing.Application.Provider;

public sealed class StaticProviderAdapterResolver : IProviderAdapterResolver
{
    private readonly Dictionary<ProtocolKind, IProviderProtocolAdapter> adapters;

    public StaticProviderAdapterResolver(params IProviderProtocolAdapter[] adapters)
    {
        this.adapters = adapters.ToDictionary(item => item.ProtocolKind);
    }

    public IProviderProtocolAdapter Resolve(ProtocolKind kind)
    {
        if (!adapters.TryGetValue(kind, out var adapter))
        {
            throw new InvalidOperationException("No protocol adapter registered for " + kind);
        }

        return adapter;
    }
}

public sealed class ScriptedProtocolAdapter : IProviderProtocolAdapter
{
    private readonly Func<ProviderInvokeRequest, ProviderInvokeResult> invoke;

    public ScriptedProtocolAdapter(
        ProtocolKind protocolKind,
        string adapterId,
        string adapterVersion,
        Func<ProviderInvokeRequest, ProviderInvokeResult> invoke)
    {
        ProtocolKind = protocolKind;
        AdapterId = adapterId;
        AdapterVersion = adapterVersion;
        this.invoke = invoke;
    }

    public string AdapterId { get; }

    public string AdapterVersion { get; }

    public ProtocolKind ProtocolKind { get; }

    public ProviderPrepareResult Prepare(
        ProviderDefinitionV1 definition,
        ProviderEndpoint endpoint,
        ProviderInvokeRequest request)
    {
        _ = definition;
        _ = endpoint;
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.ModelId.Value);
            writer.WriteBoolean("stream", request.Stream);
            writer.WriteNumber("maxTokens", request.Prompt.ReservedOutputTokens);
            writer.WriteString("promptDigest", request.Prompt.EffectivePromptDigest);
            writer.WriteString("toolSchemaDigest", PromptDigests.ToolSchemaDigest(request.Prompt.Tools));
            writer.WriteString("outputSchemaDigest", PromptDigests.OutputSchemaDigest(request.Prompt.OutputContract));
            writer.WritePropertyName("extensions");
            writer.WriteStartObject();
            foreach (var ext in request.AdapterExtensions.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                writer.WriteString(ext.Key, ext.Value);
            }

            writer.WriteEndObject();
            writer.WritePropertyName("toolContinuation");
            writer.WriteStartArray();
            foreach (var turn in request.ToolContinuation ?? [])
            {
                writer.WriteStartObject();
                writer.WriteString("callId", turn.CallId);
                writer.WriteString("toolName", turn.ToolName);
                writer.WriteString("arguments", turn.ArgumentsJson);
                writer.WriteString("resultDigest", Utf8Digest.Sha256Hex(turn.ResultJson));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (request.ContinuationState is not null)
            {
                writer.WriteString("continuationAdapter", request.ContinuationState.AdapterId);
                writer.WriteString("continuationOpaqueDigest", Utf8Digest.Sha256Hex(request.ContinuationState.OpaqueJson));
                writer.WriteNumber("continuationToolCalls", request.ContinuationState.NormalizedToolCallIds.Count);
            }

            writer.WriteEndObject();
        }

        return new ProviderPrepareResult(
            new PreparedProviderRequest(
                "POST",
                "/v1/scripted",
                Encoding.UTF8.GetString(stream.ToArray()),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                request.Stream),
            null);
    }

    public Task<ProviderInvokeResult> InvokeAsync(
        ProviderDefinitionV1 definition,
        ProviderEndpoint endpoint,
        ResolvedProviderSecret secret,
        ProviderInvokeRequest request,
        CancellationToken cancellationToken)
    {
        _ = definition;
        _ = endpoint;
        _ = secret.Reveal().Length;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(invoke(request));
    }

    public async IAsyncEnumerable<ModelRuntimeEvent> StreamAsync(
        ProviderDefinitionV1 definition,
        ProviderEndpoint endpoint,
        ResolvedProviderSecret secret,
        ProviderInvokeRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(definition, endpoint, secret, request, cancellationToken).ConfigureAwait(false);
        foreach (var item in result.Events)
        {
            yield return item;
        }
    }
}

public static class CertificationFactory
{
    public static ModelCertificationRecord Certified(
        ProviderDefinitionId provider,
        ProviderRevision revision,
        string endpoint,
        string adapterId,
        string adapterVersion,
        ModelId model,
        params (string Name, CapabilitySupport Support)[] capabilities)
    {
        var endpointIdentity = ProviderEndpoint.TryCreate(endpoint, allowInsecureLocalHttp: true, out _)?.CanonicalUri
            ?? endpoint;
        return new ModelCertificationRecord(
            "cert:" + provider.Value + ":" + model.Value,
            1,
            ModelCertificationRecord.CurrentProbeSuiteVersion,
            provider,
            revision,
            endpointIdentity,
            adapterId,
            adapterVersion,
            model,
            CertificationState.Certified,
            ReasoningCeiling.Conservative,
            ProviderDataBehavior.StatelessClientManaged,
            capabilities.Select(item => new CertifiedCapability(item.Name, item.Support, MetadataProvenance.CertifiedObserved)).ToArray(),
            1,
            null);
    }
}

public static class ProviderDefinitionFactory
{
    public static ProviderDefinitionV1 Create(
        string id,
        ProtocolKind kind,
        string endpoint,
        string credentialRef,
        string model,
        bool enabled = true,
        int revision = 1,
        int priority = 0,
        bool allowInsecureLocalHttp = true,
        IReadOnlyDictionary<string, string>? extensions = null) =>
        new(
            new ProviderDefinitionId(id),
            new ProviderRevision(revision),
            id,
            enabled,
            kind,
            endpoint,
            new CredentialRef(credentialRef),
            new ModelId(model),
            new Dictionary<string, string>(),
            30_000,
            ProviderDataBehavior.StatelessClientManaged,
            priority,
            null,
            extensions ?? new Dictionary<string, string>(),
            allowInsecureLocalHttp);
}
