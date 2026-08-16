using System.Text.Json;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Provider;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;
using RuntimeTaskStatus = LLMW.Writing.Domain.Runtime.TaskStatus;

namespace LLMW.Writing.Application.Provider;

public interface IProviderInvocationStatePort
{
    GetTaskExecutionSnapshotResponse GetSnapshot(GetTaskExecutionSnapshotRequest request);

    PersistProviderInvocationResponse Persist(PersistProviderInvocationRequest request);

    AuthorizeToolProposalResponse Authorize(AuthorizeToolProposalRequest request);
}

public sealed class DirectProviderInvocationStatePort : IProviderInvocationStatePort
{
    private readonly ProviderInvocationStateHandler handler;
    private readonly Func<string, CallerPrincipal?> principalForRun;
    private readonly AuthenticatedChannelContext? channel;

    public DirectProviderInvocationStatePort(
        ProviderInvocationStateHandler handler,
        Func<string, CallerPrincipal?>? principalForRun = null,
        AuthenticatedChannelContext? channel = null)
    {
        this.handler = handler;
        this.principalForRun = principalForRun ?? (_ => null);
        this.channel = channel;
    }

    public DirectProviderInvocationStatePort(
        ProviderInvocationStateHandler handler,
        DirectProviderInvocationIdentity identity)
        : this(handler, identity.PrincipalFor, identity.Channel)
    {
    }

    public GetTaskExecutionSnapshotResponse GetSnapshot(GetTaskExecutionSnapshotRequest request) =>
        handler.GetSnapshot(request, principalForRun(request.RunId), channel);

    public PersistProviderInvocationResponse Persist(PersistProviderInvocationRequest request) =>
        handler.Persist(request, principalForRun(request.RunId), channel);

    public AuthorizeToolProposalResponse Authorize(AuthorizeToolProposalRequest request) =>
        handler.Authorize(request, principalForRun(request.RunId), channel);
}

public sealed class ProviderInvocationStateHandler
{
    public const int InvocationLogRetention = CheckpointV1.RetainedInvocationLogLimit;

    private readonly IRuntimePersistence store;
    private readonly RuntimeSchedulerService scheduler;
    private readonly IAuthorizationService? authorization;
    private readonly TimeProvider clock;
    private readonly IRuntimeLinearizationBarrier linearization;

    public ProviderInvocationStateHandler(
        IRuntimePersistence store,
        RuntimeSchedulerService scheduler,
        IAuthorizationService? authorization = null,
        TimeProvider? clock = null,
        IRuntimeLinearizationBarrier? linearization = null)
    {
        this.store = store;
        this.scheduler = scheduler;
        this.authorization = authorization;
        this.clock = clock ?? TimeProvider.System;
        this.linearization = linearization ?? NoRuntimeLinearizationBarrier.Instance;
    }

    public GetTaskExecutionSnapshotResponse GetSnapshot(
        GetTaskExecutionSnapshotRequest request,
        CallerPrincipal? principal,
        AuthenticatedChannelContext? channel = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAgentBinding(request.RunId, principal, channel);
        var run = store.GetRun(request.RunId);
        var task = store.GetTask(request.TaskId);
        if (task is not null && !string.Equals(task.RunId, request.RunId, StringComparison.Ordinal))
        {
            throw new ProviderInvocationDeniedException(
                IpcErrorCodes.TaskOwnershipDenied,
                "Task does not belong to the authenticated Run.");
        }

        var ownership = run is not null && task is not null &&
                        string.Equals(task.RunId, request.RunId, StringComparison.Ordinal);
        var attemptLegal = ownership && EvaluateAttempt(request.AttemptId, request.TaskId, request.RunId);
        var required = new List<FrozenRequiredResultDto>();
        if (ownership)
        {
            foreach (var dependency in store.DependenciesForConsumer(request.TaskId))
            {
                if (!ResultDependencyKindCodec.IsRequired(dependency.DependencyKind))
                {
                    continue;
                }

                var evaluation = ResultDependencyPolicy.Evaluate(dependency);
                DurableResultArtifactRecord? artifact = null;
                if (!string.IsNullOrWhiteSpace(dependency.ResultArtifactId))
                {
                    artifact = store.GetResultArtifact(dependency.ResultArtifactId);
                }

                var stale = evaluation.EffectiveStatus is ResultDependencyStatus.Stale or ResultDependencyStatus.Invalid;
                var missing = evaluation.EffectiveStatus == ResultDependencyStatus.Missing || artifact is null;
                var text = artifact?.ConclusionJson;
                var digest = artifact is null ? null : Utf8Digest.Sha256Hex(artifact.FreshnessJson + "\u001f" + (text ?? ""));
                required.Add(new FrozenRequiredResultDto(
                    dependency.ResultArtifactId ?? dependency.DependencyId,
                    true,
                    stale,
                    missing,
                    digest,
                    text));
            }
        }

        var packet = task?.CompletionContractJson;
        var generation = Utf8Digest.Sha256Hex(
            (request.RunId ?? "") + "\u001f" +
            (request.TaskId ?? "") + "\u001f" +
            (request.AttemptId ?? "") + "\u001f" +
            (packet ?? "") + "\u001f" +
            string.Join('\u001f', required.Select(item =>
                item.ResultId + ":" + item.Stale + ":" + item.Missing + ":" + (item.Digest ?? ""))));
        return new GetTaskExecutionSnapshotResponse(
            generation,
            ownership,
            attemptLegal,
            string.IsNullOrWhiteSpace(packet) ? null : Utf8Digest.Sha256Hex(packet),
            required.ToArray());
    }

    public PersistProviderInvocationResponse Persist(
        PersistProviderInvocationRequest request,
        CallerPrincipal? principal,
        AuthenticatedChannelContext? channel = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAgentBinding(request.RunId, principal, channel);
        linearization.Enter(RuntimeLinearizationGate.BeforeProviderInvocationPersist);
        PersistProviderInvocationResponse? response = null;
        store.InTransaction(() => response = PersistLocked(request));
        return response ?? throw new InvalidOperationException("checkpoint-persist-failed");
    }

    private PersistProviderInvocationResponse PersistLocked(PersistProviderInvocationRequest request)
    {
        if (SecretRedaction.ContainsSecretMaterial(request.SnapshotJson) ||
            (!string.IsNullOrWhiteSpace(request.RecordJson) && SecretRedaction.ContainsSecretMaterial(request.RecordJson)))
        {
            throw new ProviderInvocationDeniedException(
                IpcErrorCodes.ProviderSecretForbidden,
                "Provider secrets must not cross Core IPC.");
        }

        var task = store.GetTask(request.TaskId);
        if (task is not null && !string.Equals(task.RunId, request.RunId, StringComparison.Ordinal))
        {
            throw new ProviderInvocationDeniedException(
                IpcErrorCodes.TaskOwnershipDenied,
                "Task does not belong to the authenticated Run.");
        }

        if (!string.IsNullOrWhiteSpace(request.AttemptId) &&
            !EvaluateAttempt(request.AttemptId, request.TaskId, request.RunId))
        {
            throw new ProviderInvocationDeniedException(
                IpcErrorCodes.IllegalCompletionLifecycle,
                "Attempt is not legal for this Task.");
        }

        ProviderInvocationSnapshot? snapshot = null;
        try
        {
            snapshot = ProviderInvocationSnapshot.Parse(request.SnapshotJson);
        }
        catch (JsonException)
        {
            // Persistence still records the opaque safe payload.
        }

        if (!string.IsNullOrWhiteSpace(request.SnapshotGeneration) &&
            !string.IsNullOrWhiteSpace(snapshot?.CompiledSnapshotGeneration) &&
            !string.Equals(request.SnapshotGeneration, snapshot.CompiledSnapshotGeneration, StringComparison.Ordinal))
        {
            throw new ProviderInvocationDeniedException(
                IpcErrorCodes.InvocationProvenanceConflict,
                "Persist SnapshotGeneration does not match the compiled snapshot generation.");
        }

        var payload = string.IsNullOrWhiteSpace(request.RecordJson) ? request.SnapshotJson : request.RecordJson;
        var incomingIdentity = snapshot?.CanonicalJson() ?? TrySnapshotIdentity(request.SnapshotJson);
        var history = ReconstructHistory(request.RunId);
        history.LatestById.TryGetValue(request.InvocationId, out var historical);
        var decision = InvocationPersistClassifier.Classify(
            historical?.Identity,
            incomingIdentity,
            historical?.Payload,
            payload);
        switch (decision)
        {
            case InvocationPersistDecision.IdempotentReplay:
                return new PersistProviderInvocationResponse(historical?.CheckpointId ?? "", true);
            case InvocationPersistDecision.IdentityConflict:
                throw new ProviderInvocationDeniedException(
                    IpcErrorCodes.InvocationIdentityConflict,
                    "InvocationId was reused with a different immutable snapshot identity.");
            case InvocationPersistDecision.LifecycleConflict:
                throw new ProviderInvocationDeniedException(
                    IpcErrorCodes.InvocationLifecycleConflict,
                    "Invocation record refinement is not a legal forward lifecycle/provenance update.");
        }

        var log = new List<string>();
        if (history.LatestCheckpoint is not null)
        {
            log.AddRange(history.LatestCheckpoint.InvocationLog);
        }

        var replaced = false;
        for (var i = 0; i < log.Count; i++)
        {
            if (!ContainsInvocation(log[i], request.InvocationId))
            {
                continue;
            }

            log[i] = payload;
            replaced = true;
            break;
        }

        if (!replaced)
        {
            log.Add(payload);
        }

        log = CheckpointV1.RetainLatestInvocations(log).ToList();
        var isNewIdentity = decision == InvocationPersistDecision.NewIdentity;
        var currentInvocationId = CurrentIdentityInvocationId(history, isNewIdentity ? request.InvocationId : null);
        var run = store.GetRun(request.RunId);
        if (run is not null &&
            snapshot is not null &&
            string.Equals(currentInvocationId, request.InvocationId, StringComparison.Ordinal))
        {
            store.UpdateRun(run with
            {
                ProviderId = snapshot.ProviderDefinitionId.Value,
                ModelId = snapshot.EffectiveRoutedModelId.Value,
                PromptConfigId = snapshot.PromptConfigId,
                EffectivePromptDigest = snapshot.EffectivePromptDigest,
                UpdatedAtMs = clock.GetUtcNow().ToUnixTimeMilliseconds()
            });
        }

        var claimedGeneration = request.SnapshotGeneration;
        var inputDigests = (history.LatestCheckpoint?.InputDigestSet ?? []).Where(item =>
            !item.StartsWith("snapshotGeneration:", StringComparison.Ordinal)).ToList();
        if (!string.IsNullOrWhiteSpace(claimedGeneration))
        {
            inputDigests.Add("snapshotGeneration:" + claimedGeneration);
        }

        var digestJson = string.IsNullOrWhiteSpace(request.InputDigestSetJson) ? "{}" : request.InputDigestSetJson;
        if (!string.IsNullOrWhiteSpace(claimedGeneration) &&
            !digestJson.Contains("\"snapshotGeneration\"", StringComparison.Ordinal))
        {
            digestJson = MergeSnapshotGeneration(digestJson, claimedGeneration);
        }

        var latest = history.LatestCheckpoint;
        var checkpointV1 = CheckpointV1.Create(
            latest?.ApprovedPlanReference ?? "provider-invocation",
            latest?.ApprovedPlanDigest,
            latest?.DagTaskStateJson ?? "{}",
            latest?.AgentStateJson ?? "{}",
            "provider_invocation",
            latest?.CriticalMessages ?? [],
            latest?.ToolReferences ?? [],
            latest?.ApprovalReferences ?? [],
            latest?.ContextPointers ?? [],
            latest?.ArtifactEvidenceReferences ?? [],
            inputDigests,
            snapshot?.PromptConfigId ?? latest?.PromptConfigId,
            snapshot?.ProviderDefinitionId.Value ?? latest?.ProviderId,
            snapshot?.EffectiveRoutedModelId.Value ?? latest?.ModelId,
            snapshot?.EffectivePromptDigest ?? latest?.EffectivePromptDigest,
            log);
        var persisted = scheduler.PersistCheckpoint(
            request.RunId,
            request.TaskId,
            CheckpointV1.CurrentSchemaVersion,
            CanonicalJson.WriteCheckpoint(checkpointV1),
            digestJson);
        if (!persisted.Succeeded || persisted.Value is null)
        {
            throw new InvalidOperationException(persisted.Failure?.Detail ?? "checkpoint-persist-failed");
        }

        return new PersistProviderInvocationResponse(persisted.Value, false);
    }

    private InvocationHistory ReconstructHistory(string runId)
    {
        var ordered = store.CheckpointsForRun(runId)
            .OrderBy(item => item.CreatedAtMs)
            .ThenBy(item => item.CheckpointId, StringComparer.Ordinal)
            .ToArray();
        var latestById = new Dictionary<string, HistoricalInvocation>(StringComparer.Ordinal);
        var firstSeen = new Dictionary<string, (long CreatedAtMs, string CheckpointId)>(StringComparer.Ordinal);
        CheckpointV1? latest = null;
        foreach (var checkpoint in ordered)
        {
            try
            {
                var parsed = CanonicalJson.Parse(checkpoint.PayloadJson, checkpoint.SchemaVersion);
                latest = parsed;
                foreach (var item in parsed.InvocationLog)
                {
                    var invocationId = TryReadInvocationId(item);
                    if (invocationId is null)
                    {
                        continue;
                    }

                    if (!firstSeen.ContainsKey(invocationId))
                    {
                        firstSeen[invocationId] = (checkpoint.CreatedAtMs, checkpoint.CheckpointId);
                    }

                    latestById[invocationId] = new HistoricalInvocation(
                        invocationId,
                        TryParseSnapshot(item)?.CanonicalJson() ?? TrySnapshotIdentity(item),
                        item,
                        checkpoint.CheckpointId);
                }
            }
            catch (CheckpointSchemaException)
            {
                // Skip corrupt historical rows; Core remains the writer of a new bounded checkpoint.
            }
        }

        return new InvocationHistory(latest, latestById, firstSeen);
    }

    private static string? CurrentIdentityInvocationId(InvocationHistory history, string? incomingNewInvocationId)
    {
        string? current = null;
        var best = (CreatedAtMs: long.MinValue, CheckpointId: "");
        foreach (var pair in history.FirstSeen)
        {
            if (pair.Value.CreatedAtMs > best.CreatedAtMs ||
                (pair.Value.CreatedAtMs == best.CreatedAtMs &&
                 string.CompareOrdinal(pair.Value.CheckpointId, best.CheckpointId) > 0))
            {
                best = pair.Value;
                current = pair.Key;
            }
        }

        return incomingNewInvocationId ?? current;
    }

    private static string? TryReadInvocationId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("invocationId", out var property)
                ? property.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record HistoricalInvocation(
        string InvocationId,
        string? Identity,
        string Payload,
        string CheckpointId);

    private sealed record InvocationHistory(
        CheckpointV1? LatestCheckpoint,
        Dictionary<string, HistoricalInvocation> LatestById,
        Dictionary<string, (long CreatedAtMs, string CheckpointId)> FirstSeen);

    public AuthorizeToolProposalResponse Authorize(
        AuthorizeToolProposalRequest request,
        CallerPrincipal? principal,
        AuthenticatedChannelContext? channel = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAgentBinding(request.RunId, principal, channel);
        var task = store.GetTask(request.TaskId);
        if (task is not null && !string.Equals(task.RunId, request.RunId, StringComparison.Ordinal))
        {
            throw new ProviderInvocationDeniedException(
                IpcErrorCodes.TaskOwnershipDenied,
                "Task does not belong to the authenticated Run.");
        }

        Capability capability;
        if (ToolCapabilityMap.Map(request.ToolName) is { } mapped)
        {
            capability = mapped;
        }
        else if (!TryParseCapabilityName(request.CapabilityName, out capability))
        {
            return new AuthorizeToolProposalResponse(false, "awaitingAuthorization", "CAPABILITY_UNAVAILABLE", request.CapabilityName);
        }

        var name = CapabilityCodec.ToCanonicalName(capability);
        if (authorization is null)
        {
            return new AuthorizeToolProposalResponse(false, "awaitingAuthorization", "CAPABILITY_UNAVAILABLE", name);
        }

        var decision = authorization.Authorize(principal!, new AuthorizationRequest(capability));
        if (!decision.IsAllowed)
        {
            return new AuthorizeToolProposalResponse(false, "denied", "CAPABILITY_DENIED", name);
        }

        return new AuthorizeToolProposalResponse(true, "authorized", null, name);
    }

    public static void SeedInferenceAttempt(IRuntimePersistence store, string taskId, string attemptId, long startedAtMs = 1)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.GetAttempt(attemptId) is not null)
        {
            return;
        }

        store.InsertAttempt(new DurableAttemptRecord(
            attemptId,
            taskId,
            store.MaxAttemptNo(taskId) + 1,
            AttemptStatusCodec.ToDurableValue(AttemptStatus.Starting),
            startedAtMs,
            null));
    }

    private bool EvaluateAttempt(string? attemptId, string taskId, string runId)
    {
        if (string.IsNullOrWhiteSpace(attemptId))
        {
            return false;
        }

        var attempt = store.GetAttempt(attemptId);
        if (attempt is null || !string.Equals(attempt.TaskId, taskId, StringComparison.Ordinal))
        {
            return false;
        }

        var task = store.GetTask(taskId);
        if (task is null || !string.Equals(task.RunId, runId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TaskStatusCodec.TryParse(task.Status, out var taskStatus) ||
            RuntimeLifecycle.IsTerminal(taskStatus) ||
            taskStatus == RuntimeTaskStatus.Failed)
        {
            return false;
        }

        if (!AttemptStatusCodec.TryParse(attempt.Status, out var status) ||
            status is not (AttemptStatus.Starting or AttemptStatus.Running))
        {
            return false;
        }

        return true;
    }

    private static void EnsureAgentBinding(string requestRunId, CallerPrincipal? principal, AuthenticatedChannelContext? channel)
    {
        if (principal is null || principal.Kind != PrincipalKind.AgentRun || string.IsNullOrWhiteSpace(principal.RunId))
        {
            throw new ProviderInvocationDeniedException(
                IpcErrorCodes.InvalidSession,
                "Agent commands require a Core-issued RunSession.");
        }

        if (!string.Equals(principal.RunId, requestRunId, StringComparison.Ordinal))
        {
            throw new ProviderInvocationDeniedException(
                IpcErrorCodes.TaskOwnershipDenied,
                "Authenticated RunSession does not match the requested Run.");
        }

        if (channel is not null &&
            !string.Equals(principal.TrustedInstanceId, channel.ChannelInstanceId, StringComparison.Ordinal))
        {
            throw new ProviderInvocationDeniedException(
                IpcErrorCodes.BindingMismatch,
                "CallerPrincipal is not bound to the authenticated channel.");
        }
    }

    private static bool TryParseCapabilityName(string name, out Capability capability)
    {
        foreach (var value in Enum.GetValues<Capability>())
        {
            if (string.Equals(CapabilityCodec.ToCanonicalName(value), name, StringComparison.Ordinal))
            {
                capability = value;
                return true;
            }
        }

        capability = default;
        return false;
    }

    private static bool ContainsInvocation(string json, string invocationId) =>
        string.Equals(TryReadInvocationId(json), invocationId, StringComparison.Ordinal);

    private static ProviderInvocationSnapshot? TryParseSnapshot(string json)
    {
        try
        {
            return ProviderInvocationSnapshot.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? TrySnapshotIdentity(string json) => TryParseSnapshot(json)?.CanonicalJson();

    private static string MergeSnapshotGeneration(string digestJson, string generation)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(digestJson) ? "{}" : digestJson);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("snapshotGeneration"))
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                }

                writer.WriteString("snapshotGeneration", generation);
                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return digestJson;
        }
    }
}
