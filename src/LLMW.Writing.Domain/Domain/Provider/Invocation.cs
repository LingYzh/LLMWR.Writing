using System.Text;
using System.Text.Json;

namespace LLMW.Writing.Domain.Provider;

public enum InvocationLifecycle
{
    Prepared,
    Dispatching,
    PossiblySent,
    ProviderAccepted,
    Streaming,
    Completed,
    Incomplete,
    Rejected,
    FailedBeforeSend,
    FailedAfterPossibleSend,
    OutcomeUnknown,
    CancelRequested,
    CancelConfirmed
}

public enum InvocationFailureClass
{
    None,
    FailedBeforeSend,
    TransportOutcomeUnknown,
    HttpClientError,
    HttpUnauthorized,
    HttpRateLimited,
    HttpServerError,
    ProviderRefusal,
    IncompleteGeneration,
    StreamBroken,
    TimeoutOutcomeUnknown,
    LocalCancelUnknownRemote,
    MalformedProtocol,
    CredentialUnavailable
}

public sealed record ProviderInvocationSnapshot(
    InvocationId InvocationId,
    string RunId,
    string TaskId,
    string? AttemptId,
    ProviderDefinitionId ProviderDefinitionId,
    ProviderRevision ProviderDefinitionRevision,
    string ProtocolAdapterId,
    string ProtocolAdapterVersion,
    ModelId RequestedModelId,
    ModelId EffectiveModelId,
    string? CertificationId,
    string? PromptConfigId,
    string EffectivePromptDigest,
    string WireRequestDigest,
    string GenerationParametersDigest,
    string ToolSchemaDigest,
    string OutputSchemaDigest,
    ProviderDataBehavior DataControlMode,
    string? PriceSnapshotId,
    CredentialRef? CredentialRevisionRef,
    long CreatedAtMs,
    string? FallbackFromInvocationId,
    string? FallbackReason,
    string? EndpointIdentity = null,
    string? EndpointProfileDigest = null,
    int AuthBindingGeneration = 0,
    string? ParentInvocationId = null,
    string? ContinuationKind = null,
    string? CompiledSnapshotGeneration = null)
{
    public ModelId EffectiveRoutedModelId => EffectiveModelId;

    public string CanonicalJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("invocationId", InvocationId.Value);
            writer.WriteString("runId", RunId);
            writer.WriteString("taskId", TaskId);
            WriteOptional(writer, "attemptId", AttemptId);
            writer.WriteString("providerDefinitionId", ProviderDefinitionId.Value);
            writer.WriteNumber("providerDefinitionRevision", ProviderDefinitionRevision.Value);
            writer.WriteString("protocolAdapterId", ProtocolAdapterId);
            writer.WriteString("protocolAdapterVersion", ProtocolAdapterVersion);
            writer.WriteString("requestedModelId", RequestedModelId.Value);
            writer.WriteString("effectiveModelId", EffectiveModelId.Value);
            WriteOptional(writer, "certificationId", CertificationId);
            WriteOptional(writer, "promptConfigId", PromptConfigId);
            writer.WriteString("effectivePromptDigest", EffectivePromptDigest);
            writer.WriteString("wireRequestDigest", WireRequestDigest);
            writer.WriteString("generationParametersDigest", GenerationParametersDigest);
            writer.WriteString("toolSchemaDigest", ToolSchemaDigest);
            writer.WriteString("outputSchemaDigest", OutputSchemaDigest);
            writer.WriteString("dataControlMode", ProviderDataBehaviorCodec.ToDurableValue(DataControlMode));
            WriteOptional(writer, "priceSnapshotId", PriceSnapshotId);
            WriteOptional(writer, "authBindingId", CredentialRevisionRef?.Value);
            writer.WriteNumber("createdAtMs", CreatedAtMs);
            WriteOptional(writer, "fallbackFromInvocationId", FallbackFromInvocationId);
            WriteOptional(writer, "fallbackReason", FallbackReason);
            WriteOptional(writer, "endpointIdentity", EndpointIdentity);
            WriteOptional(writer, "endpointProfileDigest", EndpointProfileDigest);
            if (AuthBindingGeneration > 0)
            {
                writer.WriteNumber("authBindingGeneration", AuthBindingGeneration);
            }

            WriteOptional(writer, "parentInvocationId", ParentInvocationId);
            WriteOptional(writer, "continuationKind", ContinuationKind);
            WriteOptional(writer, "compiledSnapshotGeneration", CompiledSnapshotGeneration);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static ProviderInvocationSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new ProviderInvocationSnapshot(
            new InvocationId(root.GetProperty("invocationId").GetString() ?? ""),
            root.GetProperty("runId").GetString() ?? "",
            root.GetProperty("taskId").GetString() ?? "",
            Optional(root, "attemptId"),
            new ProviderDefinitionId(root.GetProperty("providerDefinitionId").GetString() ?? ""),
            new ProviderRevision(root.GetProperty("providerDefinitionRevision").GetInt32()),
            root.GetProperty("protocolAdapterId").GetString() ?? "",
            root.GetProperty("protocolAdapterVersion").GetString() ?? "",
            new ModelId(root.GetProperty("requestedModelId").GetString() ?? ""),
            new ModelId(root.GetProperty("effectiveModelId").GetString() ?? ""),
            Optional(root, "certificationId"),
            Optional(root, "promptConfigId"),
            root.GetProperty("effectivePromptDigest").GetString() ?? "",
            root.GetProperty("wireRequestDigest").GetString() ?? "",
            root.GetProperty("generationParametersDigest").GetString() ?? "",
            root.GetProperty("toolSchemaDigest").GetString() ?? "",
            root.GetProperty("outputSchemaDigest").GetString() ?? "",
            ParseData(root.GetProperty("dataControlMode").GetString()),
            Optional(root, "priceSnapshotId"),
            Optional(root, "authBindingId") is { } cred ? new CredentialRef(cred) : null,
            root.GetProperty("createdAtMs").GetInt64(),
            Optional(root, "fallbackFromInvocationId"),
            Optional(root, "fallbackReason"),
            Optional(root, "endpointIdentity"),
            Optional(root, "endpointProfileDigest"),
            root.TryGetProperty("authBindingGeneration", out var gen) && gen.TryGetInt32(out var generation)
                ? generation
                : 0,
            Optional(root, "parentInvocationId"),
            Optional(root, "continuationKind"),
            Optional(root, "compiledSnapshotGeneration"));
    }

    private static ProviderDataBehavior ParseData(string? value) => value switch
    {
        "stateless_client_managed" => ProviderDataBehavior.StatelessClientManaged,
        "provider_stored" => ProviderDataBehavior.ProviderStored,
        "provider_background_state" => ProviderDataBehavior.ProviderBackgroundState,
        _ => ProviderDataBehavior.Unknown
    };

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteString(name, value);
        }
    }

    private static string? Optional(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) ? property.GetString() : null;
}

public static class InvocationContinuationKinds
{
    public const string Retry = "retry";
    public const string Fallback = "fallback";
    public const string ToolContinuation = "tool_continuation";
}

public sealed record InvocationRecord(
    ProviderInvocationSnapshot Snapshot,
    InvocationLifecycle Lifecycle,
    InvocationFailureClass FailureClass,
    string? ProviderRequestId,
    string? ProviderResponseId,
    string? ProviderReportedModel,
    NormalizedUsage Usage,
    CostEstimate Cost,
    bool DuplicateExecutionRisk,
    string? RefusalText,
    bool HostedToolsRejected)
{
    public InvocationRecord WithLifecycle(InvocationLifecycle lifecycle, InvocationFailureClass failure) =>
        this with { Lifecycle = lifecycle, FailureClass = failure };

    public bool IsTerminal => Lifecycle is
        InvocationLifecycle.Completed or
        InvocationLifecycle.Incomplete or
        InvocationLifecycle.Rejected or
        InvocationLifecycle.FailedBeforeSend or
        InvocationLifecycle.FailedAfterPossibleSend or
        InvocationLifecycle.OutcomeUnknown or
        InvocationLifecycle.CancelConfirmed;
}

public static class InvocationStateMachine
{
    public static InvocationLifecycle AfterSendAttempted(InvocationLifecycle current) => current switch
    {
        InvocationLifecycle.Prepared or InvocationLifecycle.Dispatching => InvocationLifecycle.PossiblySent,
        _ => current
    };

    public static InvocationLifecycle AfterHttpStatus(int statusCode) => statusCode switch
    {
        >= 200 and < 300 => InvocationLifecycle.ProviderAccepted,
        >= 400 and < 500 => InvocationLifecycle.Rejected,
        _ => InvocationLifecycle.FailedAfterPossibleSend
    };

    public static InvocationFailureClass ClassifyHttp(int statusCode) => statusCode switch
    {
        401 or 403 => InvocationFailureClass.HttpUnauthorized,
        429 => InvocationFailureClass.HttpRateLimited,
        >= 400 and < 500 => InvocationFailureClass.HttpClientError,
        >= 500 => InvocationFailureClass.HttpServerError,
        _ => InvocationFailureClass.None
    };

    public static InvocationLifecycle TimeoutAfterPossibleSend() => InvocationLifecycle.OutcomeUnknown;

    public static InvocationLifecycle LocalCancelWithoutRemoteProof() => InvocationLifecycle.CancelRequested;

    public static InvocationLifecycle StreamBreakBeforeTerminal() => InvocationLifecycle.Incomplete;

    public static bool MayAutoRetry(InvocationFailureClass failure, InvocationLifecycle lifecycle) =>
        failure is InvocationFailureClass.HttpRateLimited or InvocationFailureClass.HttpServerError &&
        lifecycle is not InvocationLifecycle.OutcomeUnknown;
}

public enum ModelRuntimeEventKind
{
    TextDelta,
    TextCompleted,
    ToolCallStarted,
    ToolCallArgumentsDelta,
    ToolCallCompleted,
    StructuredOutput,
    Refusal,
    Incomplete,
    Error,
    UsageUpdate,
    Completed,
    ReasoningSummary
}

public sealed record ModelRuntimeEvent(
    ModelRuntimeEventKind Kind,
    string? Text,
    string? ProviderCallId,
    string? ToolName,
    string? ArgumentsDelta,
    string? ArgumentsJson,
    NormalizedUsage? Usage,
    string? ErrorCode,
    bool Terminal,
    string? ContinuationCaptureJson = null);

public sealed record ToolCallRequest(
    string ProviderCallId,
    string ToolName,
    string ArgumentsJson);

public enum StreamingParserState
{
    Created,
    Accepted,
    Streaming,
    Completed,
    Rejected,
    StreamBroken,
    OutcomeUnknown,
    CancelRequested
}
