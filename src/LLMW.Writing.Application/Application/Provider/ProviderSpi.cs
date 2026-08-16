using System.Text;
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

    int BindingGeneration(CredentialRef credentialRef);

    void Store(CredentialRef credentialRef, string secret);
}

public sealed class MemoryProviderCredentialResolver : IProviderCredentialResolver
{
    private readonly object gate = new();
    private readonly Dictionary<string, (string Secret, int Generation)> secrets = new(StringComparer.Ordinal);

    public CredentialResolveResult Resolve(CredentialRef credentialRef)
    {
        lock (gate)
        {
            if (!secrets.TryGetValue(credentialRef.Value, out var stored))
            {
                return new CredentialResolveResult(null, "CREDENTIAL_UNAVAILABLE");
            }

            return new CredentialResolveResult(new ResolvedProviderSecret(stored.Secret), null);
        }
    }

    public int BindingGeneration(CredentialRef credentialRef)
    {
        lock (gate)
        {
            return secrets.TryGetValue(credentialRef.Value, out var stored) ? stored.Generation : 0;
        }
    }

    public void Store(CredentialRef credentialRef, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        lock (gate)
        {
            var next = secrets.TryGetValue(credentialRef.Value, out var stored) ? stored.Generation + 1 : 1;
            secrets[credentialRef.Value] = (secret, next);
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
            items.TryGetValue(definition.ProviderDefinitionId.Value, out var existing);
            items[definition.ProviderDefinitionId.Value] = ProviderDefinitionRevision.Apply(existing, definition);
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

public sealed record ProviderNativeToolTurn(
    string CallId,
    string ToolName,
    string ArgumentsJson,
    string ResultJson,
    string? ErrorCode = null);

public sealed record ProviderInvokeRequest(
    PromptIr Prompt,
    ModelId ModelId,
    bool Stream,
    IReadOnlyDictionary<string, string> AdapterExtensions,
    string ClientRequestId = "",
    IReadOnlyList<ProviderNativeToolTurn>? ToolContinuation = null,
    ModelCertificationRecord? ProtocolProfile = null,
    ProviderContinuationState? ContinuationState = null);

public sealed record PreparedProviderRequest(
    string Method,
    string Path,
    string CanonicalSemanticBody,
    IReadOnlyDictionary<string, string> NonSecretHeaders,
    bool Stream);

public sealed record ProviderPrepareResult(PreparedProviderRequest? Request, string? ErrorCode)
{
    public bool Succeeded => Request is not null && ErrorCode is null;
}

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
    bool DuplicateExecutionRisk,
    string? NativeContinuationCaptureJson = null);

public sealed record ProviderStreamFrame(ModelRuntimeEvent Event, string? NativeContinuationCaptureJson = null)
{
    public static implicit operator ProviderStreamFrame(ModelRuntimeEvent runtimeEvent) => new(runtimeEvent);
}

public interface IProviderProtocolAdapter
{
    string AdapterId { get; }

    string AdapterVersion { get; }

    ProtocolKind ProtocolKind { get; }

    ProviderPrepareResult Prepare(
        ProviderDefinitionV1 definition,
        ProviderEndpoint endpoint,
        ProviderInvokeRequest request);

    Task<ProviderInvokeResult> InvokeAsync(
        ProviderDefinitionV1 definition,
        ProviderEndpoint endpoint,
        ResolvedProviderSecret secret,
        ProviderInvokeRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ProviderStreamFrame> StreamAsync(
        ProviderDefinitionV1 definition,
        ProviderEndpoint endpoint,
        ResolvedProviderSecret secret,
        ProviderInvokeRequest request,
        CancellationToken cancellationToken);

    ProviderContinuationState MergeContinuation(
        ProviderContinuationState? prior,
        string? captureJson,
        IReadOnlyList<LocalToolExecutionResult> toolResults)
        => ProviderContinuationState.AppendLegacyTurns(AdapterId, AdapterVersion, prior, toolResults);
}

public interface IProviderAdapterResolver
{
    IProviderProtocolAdapter Resolve(ProtocolKind kind);
}

public sealed record ToolProposalDecision(
    bool MayExecute,
    string? DenialCode,
    ToolCallRequest? Request);

public sealed record CoreToolAuthorizationResult(bool Allowed, string Status, string? DenialCode, string CapabilityName)
{
    public static CoreToolAuthorizationResult Unavailable(string capabilityName) =>
        new(false, "awaitingAuthorization", "CAPABILITY_UNAVAILABLE", capabilityName);

    public static CoreToolAuthorizationResult Denied(string capabilityName, string? code) =>
        new(false, "denied", code ?? "CAPABILITY_DENIED", capabilityName);

    public static CoreToolAuthorizationResult Authorized(string capabilityName) =>
        new(true, "authorized", null, capabilityName);
}

public static class ToolCapabilityMap
{
    public static Capability? Map(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        if (toolName.StartsWith("Shell.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolName, "Shell.Execute", StringComparison.OrdinalIgnoreCase))
        {
            return Capability.ShellExecute;
        }

        if (toolName.StartsWith("MCP.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolName, "MCP.Call", StringComparison.OrdinalIgnoreCase))
        {
            return Capability.McpCall;
        }

        if (toolName.StartsWith("Git.", StringComparison.OrdinalIgnoreCase))
        {
            return Capability.GitExecute;
        }

        if (toolName.StartsWith("Script.", StringComparison.OrdinalIgnoreCase))
        {
            return Capability.ScriptExecute;
        }

        if (string.Equals(toolName, "lookup", StringComparison.OrdinalIgnoreCase))
        {
            return Capability.RegistryQuery;
        }

        return null;
    }

    public static string CanonicalName(string toolName) =>
        Map(toolName) is { } capability ? CapabilityCodec.ToCanonicalName(capability) : toolName;
}

public static class ToolProposalGuard
{
    public static ToolProposalDecision Inspect(
        ToolCallRequest proposal,
        IReadOnlyList<AuthorizedToolSchema> authorizedTools,
        CoreToolAuthorizationResult? coreAuthorization)
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

        if (!StructuredOutputValidator.TryValidateObject(proposal.ArgumentsJson, schema.ParametersJson, schema.RequiredProperties, out _))
        {
            return new ToolProposalDecision(false, "MALFORMED_TOOL_ARGUMENTS", proposal);
        }

        if (coreAuthorization is null || !coreAuthorization.Allowed)
        {
            return new ToolProposalDecision(
                false,
                coreAuthorization?.DenialCode ?? "AWAITING_AUTHORIZATION",
                proposal);
        }

        return new ToolProposalDecision(true, null, proposal);
    }

    public static bool IsProviderHosted(string toolName)
    {
        if (string.Equals(toolName, "MCP.Call", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return toolName.StartsWith("web_search", StringComparison.OrdinalIgnoreCase) ||
               toolName.StartsWith("code_interpreter", StringComparison.OrdinalIgnoreCase) ||
               toolName.StartsWith("computer_use", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(toolName, "mcp", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("file_search", StringComparison.OrdinalIgnoreCase);
    }
}

public static class StructuredOutputValidator
{
    public static bool TryValidateObject(string? json, IReadOnlyList<string> requiredProperties, out string? error) =>
        TryValidateObject(json, null, requiredProperties, out error);

    public static bool TryValidateObject(
        string? json,
        string? schemaJson,
        IReadOnlyList<string> requiredProperties,
        out string? error)
    {
        if (!string.IsNullOrWhiteSpace(schemaJson))
        {
            return OutputSchemaSubset.TryValidateInstance(json, schemaJson, out error);
        }

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

public static class ProviderDefinitionRevision
{
    public static ProviderDefinitionV1 Apply(ProviderDefinitionV1? existing, ProviderDefinitionV1 incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (existing is null)
        {
            return incoming with { Revision = incoming.Revision.Value > 0 ? incoming.Revision : new ProviderRevision(1) };
        }

        if (string.Equals(SemanticIdentity(existing), SemanticIdentity(incoming), StringComparison.Ordinal))
        {
            return existing with { DisplayName = incoming.DisplayName };
        }

        return incoming with { Revision = new ProviderRevision(existing.Revision.Value + 1) };
    }

    public static string SemanticIdentity(ProviderDefinitionV1 definition)
    {
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("protocolKind", ProtocolKindCodec.ToDurableValue(definition.ProtocolKind));
            writer.WriteString("endpoint", definition.Endpoint);
            writer.WriteBoolean("enabled", definition.Enabled);
            writer.WriteString("credentialRef", definition.CredentialRef.Value);
            writer.WriteString("defaultModelId", definition.DefaultModelId?.Value ?? "");
            writer.WriteNumber("timeoutMs", definition.TimeoutMs);
            writer.WriteString("dataPolicy", ProviderDataBehaviorCodec.ToDurableValue(definition.DataPolicy));
            writer.WriteNumber("routingPriority", definition.RoutingPriority);
            writer.WriteString("priceSnapshotId", definition.PriceSnapshotId ?? "");
            writer.WriteBoolean("allowInsecureLocalHttp", definition.AllowInsecureLocalHttp);
            writer.WritePropertyName("modelAliases");
            writer.WriteStartObject();
            foreach (var alias in definition.ModelAliases.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                writer.WriteString(alias.Key, alias.Value);
            }

            writer.WriteEndObject();
            writer.WritePropertyName("adapterExtensions");
            writer.WriteStartObject();
            foreach (var ext in definition.AdapterExtensions.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                writer.WriteString(ext.Key, ext.Value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

public sealed record ProviderRetryPolicy(int MaxNetworkAttempts, bool AllowFallbackWhenPinned)
{
    public static ProviderRetryPolicy Default { get; } = new(2, false);
}

public sealed record LocalToolExecutionResult(string CallId, string ToolName, string ResultJson, string? ErrorCode);

public interface ILocalToolExecutor
{
    LocalToolExecutionResult Execute(string callId, string toolName, string argumentsJson);
}

public interface IModelCatalogStore
{
    IReadOnlyList<ModelCatalogEntry> List(ProviderDefinitionId? provider = null);

    void Upsert(ModelCatalogEntry entry);
}

public sealed class MemoryModelCatalogStore : IModelCatalogStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, ModelCatalogEntry> items = new(StringComparer.Ordinal);

    public IReadOnlyList<ModelCatalogEntry> List(ProviderDefinitionId? provider = null)
    {
        lock (gate)
        {
            var values = items.Values.AsEnumerable();
            if (provider is { } id)
            {
                values = values.Where(item => item.ProviderDefinitionId.Value == id.Value);
            }

            return values
                .OrderBy(item => item.ProviderDefinitionId.Value, StringComparer.Ordinal)
                .ThenBy(item => item.ModelId.Value, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public void Upsert(ModelCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (gate)
        {
            items[entry.ProviderDefinitionId.Value + "\u001f" + entry.ModelId.Value] = entry;
        }
    }
}

public interface ITaskCertificationStore
{
    TaskCapabilityCertification? Find(ProviderDefinitionId provider, ModelId model);

    IReadOnlyList<TaskCapabilityCertification> List();
}

public sealed class MemoryTaskCertificationStore : ITaskCertificationStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, TaskCapabilityCertification> items = new(StringComparer.Ordinal);

    public TaskCapabilityCertification? Find(ProviderDefinitionId provider, ModelId model)
    {
        lock (gate)
        {
            return items.TryGetValue(provider.Value + "\u001f" + model.Value, out var value) ? value : null;
        }
    }

    internal void UpsertUncheckedForTests(TaskCapabilityCertification record) => Write(record);

    internal void Write(TaskCapabilityCertification record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (gate)
        {
            items[record.ProviderDefinitionId.Value + "\u001f" + record.ModelId.Value] = record;
        }
    }

    public IReadOnlyList<TaskCapabilityCertification> List()
    {
        lock (gate)
        {
            return items.Values.OrderBy(item => item.CertificationId, StringComparer.Ordinal).ToArray();
        }
    }
}

public sealed class TaskCapabilityCertificationService
{
    private readonly ITaskCertificationStore store;
    private readonly MemoryTaskCertificationStore? writer;

    public TaskCapabilityCertificationService(ITaskCertificationStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        writer = store as MemoryTaskCertificationStore;
    }

    public TaskCapabilityCertification Issue(TaskCapabilityCertification candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (writer is null)
        {
            throw new InvalidOperationException("Certification Issue requires the application certification writer.");
        }

        if (candidate.State == CertificationState.Certified)
        {
            var profile = TaskCertificationPolicies.For(candidate.CertifiedTaskClasses, candidate.EvaluationSuiteVersion);
            var missingBaseline = string.IsNullOrWhiteSpace(candidate.PromptBaselineDigest);
            var missingIdentity = string.IsNullOrWhiteSpace(candidate.DatasetId) ||
                                  string.IsNullOrWhiteSpace(candidate.DatasetVersion) ||
                                  string.IsNullOrWhiteSpace(candidate.EvaluationSuiteVersion);
            var policyRejected = profile is null ||
                                 !profile.CandidatePolicyMatches(candidate.Thresholds) ||
                                 !profile.HasRequiredScores(candidate.Scores) ||
                                 !profile.ScoresPass(candidate.Scores) ||
                                 candidate.MaxReasoningCeiling > profile.MaxAllowedCeiling;

            if (policyRejected || missingIdentity || missingBaseline)
            {
                candidate = candidate with
                {
                    State = CertificationState.Uncertified,
                    MaxReasoningCeiling = ReasoningCeiling.Conservative,
                    Thresholds = profile?.Thresholds ?? candidate.Thresholds
                };
            }
            else if (profile is not null)
            {
                candidate = candidate with { Thresholds = profile.Thresholds };
            }
        }

        writer.Write(candidate);
        return candidate;
    }

    public TaskCapabilityCertification ResolveCurrent(
        ProviderDefinitionId provider,
        ModelId model,
        ProviderRevision currentRevision,
        string currentEndpointIdentity,
        string currentAdapterId,
        string currentAdapterVersion,
        string currentEvaluationSuiteVersion,
        string? currentPromptBaselineDigest)
    {
        var record = store.Find(provider, model) ??
                     TaskCapabilityCertification.Uncertified(
                         provider, currentRevision, currentEndpointIdentity, currentAdapterId, currentAdapterVersion, model);
        if (record.State is CertificationState.Certified or CertificationState.Partial &&
            record.IsStaleFor(
                currentRevision,
                currentEndpointIdentity,
                currentAdapterId,
                currentAdapterVersion,
                currentEvaluationSuiteVersion,
                currentPromptBaselineDigest))
        {
            record = record with { State = CertificationState.Stale };
        }

        return record;
    }
}
