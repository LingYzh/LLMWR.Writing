using System.Runtime.CompilerServices;
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
        return new ModelCertificationRecord(
            "cert:" + provider.Value + ":" + model.Value,
            1,
            ModelCertificationRecord.CurrentProbeSuiteVersion,
            provider,
            revision,
            endpoint,
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
