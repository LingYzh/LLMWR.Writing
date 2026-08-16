using System.Text.Json;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Provider;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;

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

    public ProviderInvocationStateHandler(
        IRuntimePersistence store,
        RuntimeSchedulerService scheduler,
        IAuthorizationService? authorization = null,
        TimeProvider? clock = null)
    {
        this.store = store;
        this.scheduler = scheduler;
        this.authorization = authorization;
        this.clock = clock ?? TimeProvider.System;
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

        var payload = string.IsNullOrWhiteSpace(request.RecordJson) ? request.SnapshotJson : request.RecordJson;
        var incomingIdentity = TrySnapshotIdentity(request.SnapshotJson);
        var existingCheckpoints = store.CheckpointsForRun(request.RunId)
            .OrderBy(item => item.CreatedAtMs)
            .ThenBy(item => item.CheckpointId, StringComparer.Ordinal)
            .ToArray();
        CheckpointV1? latest = null;
        string? historicalCheckpointId = null;
        string? historicalIdentity = null;
        string? historicalPayload = null;
        var maxCreatedAtMs = long.MinValue;
        foreach (var checkpoint in existingCheckpoints)
        {
            try
            {
                var parsed = CanonicalJson.Parse(checkpoint.PayloadJson, checkpoint.SchemaVersion);
                latest = parsed;
                foreach (var item in parsed.InvocationLog)
                {
                    var parsedSnapshot = TryParseSnapshot(item);
                    if (parsedSnapshot is not null && parsedSnapshot.CreatedAtMs > maxCreatedAtMs)
                    {
                        maxCreatedAtMs = parsedSnapshot.CreatedAtMs;
                    }

                    if (!ContainsInvocation(item, request.InvocationId))
                    {
                        continue;
                    }

                    historicalCheckpointId = checkpoint.CheckpointId;
                    historicalPayload = item;
                    historicalIdentity = parsedSnapshot?.CanonicalJson() ?? TrySnapshotIdentity(item);
                }
            }
            catch (CheckpointSchemaException)
            {
                // Skip corrupt historical rows; Core remains the writer of a new bounded checkpoint.
            }
        }

        if (historicalIdentity is not null)
        {
            if (incomingIdentity is not null &&
                !string.Equals(historicalIdentity, incomingIdentity, StringComparison.Ordinal))
            {
                throw new ProviderInvocationDeniedException(
                    IpcErrorCodes.InvocationIdentityConflict,
                    "InvocationId was reused with a different immutable snapshot identity.");
            }

            var inWindow = latest is not null &&
                           latest.InvocationLog.Any(item => ContainsInvocation(item, request.InvocationId));
            if (!inWindow || string.Equals(historicalPayload, payload, StringComparison.Ordinal))
            {
                return new PersistProviderInvocationResponse(historicalCheckpointId ?? "", true);
            }
        }

        var log = new List<string>();
        if (latest is not null)
        {
            log.AddRange(latest.InvocationLog);
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
        ProviderInvocationSnapshot? snapshot = null;
        try
        {
            snapshot = ProviderInvocationSnapshot.Parse(request.SnapshotJson);
        }
        catch (JsonException)
        {
            // Persistence still records the opaque safe payload.
        }

        var run = store.GetRun(request.RunId);
        var incomingCreated = snapshot?.CreatedAtMs ?? long.MinValue;
        var isCurrentIdentity = incomingCreated >= maxCreatedAtMs;
        if (run is not null && snapshot is not null && isCurrentIdentity)
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
        var inputDigests = (latest?.InputDigestSet ?? []).Where(item =>
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
        json.Contains("\"invocationId\":\"" + invocationId + "\"", StringComparison.Ordinal);

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
