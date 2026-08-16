using LLMW.Writing.Domain.Prompt;
using LLMW.Writing.Domain.Provider;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Provider;

public sealed class ResolvedProviderSecret : IDisposable
{
    private string? value;

    public ResolvedProviderSecret(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        value = secret;
    }

    public string Reveal() => value ?? throw new ObjectDisposedException(nameof(ResolvedProviderSecret));

    public override string ToString() => "[redacted]";

    public void Dispose()
    {
        value = null;
        GC.SuppressFinalize(this);
    }
}

public sealed record CredentialResolveResult(ResolvedProviderSecret? Secret, string? ErrorCode)
{
    public bool Succeeded => Secret is not null && ErrorCode is null;
}

public interface IProviderCredentialResolver
{
    CredentialResolveResult Resolve(CredentialRef credentialRef);

    void Store(CredentialRef credentialRef, string secret);
}

public sealed class MemoryProviderCredentialResolver : IProviderCredentialResolver
{
    private readonly object gate = new();
    private readonly Dictionary<string, string> secrets = new(StringComparer.Ordinal);

    public CredentialResolveResult Resolve(CredentialRef credentialRef)
    {
        lock (gate)
        {
            if (!secrets.TryGetValue(credentialRef.Value, out var secret))
            {
                return new CredentialResolveResult(null, "CREDENTIAL_UNAVAILABLE");
            }

            return new CredentialResolveResult(new ResolvedProviderSecret(secret), null);
        }
    }

    public void Store(CredentialRef credentialRef, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        lock (gate)
        {
            secrets[credentialRef.Value] = secret;
        }
    }
}

public sealed record ProviderDefinitionV1(
    ProviderDefinitionId ProviderDefinitionId,
    ProviderRevision Revision,
    string DisplayName,
    bool Enabled,
    ProtocolKind ProtocolKind,
    string Endpoint,
    CredentialRef CredentialRef,
    ModelId? DefaultModelId,
    IReadOnlyDictionary<string, string> ModelAliases,
    int TimeoutMs,
    ProviderDataBehavior DataPolicy,
    int RoutingPriority,
    string? PriceSnapshotId,
    IReadOnlyDictionary<string, string> AdapterExtensions,
    bool AllowInsecureLocalHttp)
{
    public const int CurrentSchemaVersion = 1;
}

public interface IProviderDefinitionStore
{
    IReadOnlyList<ProviderDefinitionV1> List();

    ProviderDefinitionV1? FindById(ProviderDefinitionId id);

    void Upsert(ProviderDefinitionV1 definition);
}

public sealed class MemoryProviderDefinitionStore : IProviderDefinitionStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, ProviderDefinitionV1> items = new(StringComparer.Ordinal);

    public IReadOnlyList<ProviderDefinitionV1> List()
    {
        lock (gate)
        {
            return items.Values.OrderBy(item => item.ProviderDefinitionId.Value, StringComparer.Ordinal).ToArray();
        }
    }

    public ProviderDefinitionV1? FindById(ProviderDefinitionId id)
    {
        lock (gate)
        {
            return items.TryGetValue(id.Value, out var value) ? value : null;
        }
    }

    public void Upsert(ProviderDefinitionV1 definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (gate)
        {
            items[definition.ProviderDefinitionId.Value] = definition;
        }
    }
}

public interface IModelCertificationStore
{
    ModelCertificationRecord? Find(ProviderDefinitionId provider, ModelId model);

    void Upsert(ModelCertificationRecord record);

    IReadOnlyList<ModelCertificationRecord> List();
}

public sealed class MemoryModelCertificationStore : IModelCertificationStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, ModelCertificationRecord> items = new(StringComparer.Ordinal);

    public ModelCertificationRecord? Find(ProviderDefinitionId provider, ModelId model)
    {
        lock (gate)
        {
            return items.TryGetValue(Key(provider, model), out var value) ? value : null;
        }
    }

    public void Upsert(ModelCertificationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (gate)
        {
            items[Key(record.ProviderDefinitionId, record.ModelId)] = record;
        }
    }

    public IReadOnlyList<ModelCertificationRecord> List()
    {
        lock (gate)
        {
            return items.Values.OrderBy(item => item.CertificationId, StringComparer.Ordinal).ToArray();
        }
    }

    private static string Key(ProviderDefinitionId provider, ModelId model) => provider.Value + "\u001f" + model.Value;
}

public interface IPriceSnapshotStore
{
    PriceSnapshot? FindById(string priceSnapshotId);

    void Upsert(PriceSnapshot snapshot);
}

public sealed class MemoryPriceSnapshotStore : IPriceSnapshotStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, PriceSnapshot> items = new(StringComparer.Ordinal);

    public PriceSnapshot? FindById(string priceSnapshotId)
    {
        lock (gate)
        {
            return items.TryGetValue(priceSnapshotId, out var value) ? value : null;
        }
    }

    public void Upsert(PriceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            items[snapshot.PriceSnapshotId] = snapshot;
        }
    }
}

public sealed record ProviderInvokeRequest(
    PromptIr Prompt,
    ModelId ModelId,
    bool Stream,
    IReadOnlyDictionary<string, string> AdapterExtensions);

public sealed record ProviderInvokeResult(
    InvocationLifecycle Lifecycle,
    InvocationFailureClass FailureClass,
    IReadOnlyList<ModelRuntimeEvent> Events,
    string? ProviderRequestId,
    string? ProviderResponseId,
    string? ProviderReportedModel,
    NormalizedUsage Usage,
    IReadOnlyList<ToolCallRequest> CompletedToolCalls,
    string? StructuredOutputJson,
    string? RefusalText,
    string? ErrorCode,
    bool DuplicateExecutionRisk);

public interface IProviderProtocolAdapter
{
    string AdapterId { get; }

    string AdapterVersion { get; }

    ProtocolKind ProtocolKind { get; }

    Task<ProviderInvokeResult> InvokeAsync(
        ProviderDefinitionV1 definition,
        ProviderEndpoint endpoint,
        ResolvedProviderSecret secret,
        ProviderInvokeRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ModelRuntimeEvent> StreamAsync(
        ProviderDefinitionV1 definition,
        ProviderEndpoint endpoint,
        ResolvedProviderSecret secret,
        ProviderInvokeRequest request,
        CancellationToken cancellationToken);
}

public interface IProviderAdapterResolver
{
    IProviderProtocolAdapter Resolve(ProtocolKind kind);
}

public sealed record ToolProposalDecision(
    bool MayExecute,
    string? DenialCode,
    ToolCallRequest? Request);

public static class ToolProposalGuard
{
    public static ToolProposalDecision Inspect(
        ToolCallRequest proposal,
        IReadOnlyList<AuthorizedToolSchema> authorizedTools,
        CapabilityEvaluationRequest? capabilityInputs)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (IsProviderHosted(proposal.ToolName))
        {
            return new ToolProposalDecision(false, "PROVIDER_HOSTED_TOOL_UNSUPPORTED", proposal);
        }

        var schema = authorizedTools.FirstOrDefault(item =>
            string.Equals(item.ToolName, proposal.ToolName, StringComparison.Ordinal));
        if (schema is null)
        {
            return new ToolProposalDecision(false, "UNKNOWN_TOOL", proposal);
        }

        if (!StructuredOutputValidator.TryValidateObject(proposal.ArgumentsJson, schema.RequiredProperties, out _))
        {
            return new ToolProposalDecision(false, "MALFORMED_TOOL_ARGUMENTS", proposal);
        }

        if (capabilityInputs is not null)
        {
            var decision = CapabilityEvaluator.Evaluate(capabilityInputs);
            if (!decision.IsAllowed)
            {
                return new ToolProposalDecision(false, "CAPABILITY_DENIED", proposal);
            }
        }

        return new ToolProposalDecision(true, null, proposal);
    }

    public static bool IsProviderHosted(string toolName) =>
        toolName.StartsWith("web_search", StringComparison.OrdinalIgnoreCase) ||
        toolName.StartsWith("code_interpreter", StringComparison.OrdinalIgnoreCase) ||
        toolName.StartsWith("computer_use", StringComparison.OrdinalIgnoreCase) ||
        toolName.StartsWith("mcp", StringComparison.OrdinalIgnoreCase) ||
        toolName.Contains("file_search", StringComparison.OrdinalIgnoreCase);
}

public static class StructuredOutputValidator
{
    public static bool TryValidateObject(string? json, IReadOnlyList<string> requiredProperties, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "json-missing";
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                error = "json-not-object";
                return false;
            }

            foreach (var required in requiredProperties)
            {
                if (!document.RootElement.TryGetProperty(required, out _))
                {
                    error = "missing:" + required;
                    return false;
                }
            }

            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            error = "json-malformed";
            return false;
        }
    }
}
