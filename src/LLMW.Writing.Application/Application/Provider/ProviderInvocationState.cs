using System.Text;
using System.Text.Json;
using LLMW.Writing.Application.Ipc;
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
    private readonly CallerPrincipal? principal;

    public DirectProviderInvocationStatePort(ProviderInvocationStateHandler handler, CallerPrincipal? principal = null)
    {
        this.handler = handler;
        this.principal = principal;
    }

    public GetTaskExecutionSnapshotResponse GetSnapshot(GetTaskExecutionSnapshotRequest request) =>
        handler.GetSnapshot(request);

    public PersistProviderInvocationResponse Persist(PersistProviderInvocationRequest request) =>
        handler.Persist(request);

    public AuthorizeToolProposalResponse Authorize(AuthorizeToolProposalRequest request) =>
        handler.Authorize(request, principal);
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

    public GetTaskExecutionSnapshotResponse GetSnapshot(GetTaskExecutionSnapshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = store.GetRun(request.RunId);
        var task = store.GetTask(request.TaskId);
        var ownership = run is not null && task is not null &&
                        string.Equals(task.RunId, request.RunId, StringComparison.Ordinal);
        var attemptLegal = ownership &&
                           task!.Status is not ("completed" or "failed" or "cancelled");
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

    public PersistProviderInvocationResponse Persist(PersistProviderInvocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (SecretRedaction.ContainsSecretMaterial(request.SnapshotJson) ||
            (!string.IsNullOrWhiteSpace(request.RecordJson) && SecretRedaction.ContainsSecretMaterial(request.RecordJson)))
        {
            throw new InvalidOperationException(IpcErrorCodes.ProviderSecretForbidden);
        }

        var payload = string.IsNullOrWhiteSpace(request.RecordJson) ? request.SnapshotJson : request.RecordJson;
        var existingCheckpoints = store.CheckpointsForRun(request.RunId)
            .OrderBy(item => item.CreatedAtMs)
            .ToArray();
        CheckpointV1? latest = null;
        string? existingCheckpointId = null;
        foreach (var checkpoint in existingCheckpoints)
        {
            try
            {
                var parsed = CanonicalJson.Parse(checkpoint.PayloadJson, checkpoint.SchemaVersion);
                latest = parsed;
                if (parsed.InvocationLog.Any(item => ContainsInvocation(item, request.InvocationId)))
                {
                    existingCheckpointId = checkpoint.CheckpointId;
                }
            }
            catch (CheckpointSchemaException)
            {
                // Skip corrupt historical rows; Core remains the writer of a new bounded checkpoint.
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

            if (string.Equals(log[i], payload, StringComparison.Ordinal))
            {
                return new PersistProviderInvocationResponse(existingCheckpointId ?? "", true);
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
        if (run is not null && snapshot is not null)
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
            latest?.InputDigestSet ?? [],
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
            string.IsNullOrWhiteSpace(request.InputDigestSetJson) ? "{}" : request.InputDigestSetJson);
        if (!persisted.Succeeded || persisted.Value is null)
        {
            throw new InvalidOperationException(persisted.Failure?.Detail ?? "checkpoint-persist-failed");
        }

        return new PersistProviderInvocationResponse(persisted.Value, replaced);
    }

    public AuthorizeToolProposalResponse Authorize(AuthorizeToolProposalRequest request, CallerPrincipal? principal)
    {
        ArgumentNullException.ThrowIfNull(request);
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
        if (authorization is null || principal is null)
        {
            return new AuthorizeToolProposalResponse(false, "awaitingAuthorization", "CAPABILITY_UNAVAILABLE", name);
        }

        var decision = authorization.Authorize(principal, new AuthorizationRequest(capability));
        if (!decision.IsAllowed)
        {
            return new AuthorizeToolProposalResponse(false, "denied", "CAPABILITY_DENIED", name);
        }

        return new AuthorizeToolProposalResponse(true, "authorized", null, name);
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
}

public sealed class IpcProviderInvocationStateClient : IProviderInvocationStatePort
{
    private readonly IIpcApplicationCommandHandler handler;
    private readonly string workspaceInstanceId;
    private readonly Guid? projectId;
    private readonly CallerPrincipal? principal;

    public IpcProviderInvocationStateClient(
        IIpcApplicationCommandHandler handler,
        string workspaceInstanceId,
        Guid? projectId = null,
        CallerPrincipal? principal = null)
    {
        this.handler = handler;
        this.workspaceInstanceId = workspaceInstanceId;
        this.projectId = projectId;
        this.principal = principal;
    }

    public GetTaskExecutionSnapshotResponse GetSnapshot(GetTaskExecutionSnapshotRequest request) =>
        Exchange(
            IpcSemanticTypes.GetTaskExecutionSnapshot,
            request,
            IpcJsonContext.Default.GetTaskExecutionSnapshotRequest,
            IpcJsonContext.Default.GetTaskExecutionSnapshotResponseEnvelope);

    public PersistProviderInvocationResponse Persist(PersistProviderInvocationRequest request)
    {
        AssertNoSecret(request.SnapshotJson);
        AssertNoSecret(request.RecordJson);
        return Exchange(
            IpcSemanticTypes.PersistProviderInvocation,
            request,
            IpcJsonContext.Default.PersistProviderInvocationRequest,
            IpcJsonContext.Default.PersistProviderInvocationResponseEnvelope);
    }

    public AuthorizeToolProposalResponse Authorize(AuthorizeToolProposalRequest request) =>
        Exchange(
            IpcSemanticTypes.AuthorizeToolProposal,
            request,
            IpcJsonContext.Default.AuthorizeToolProposalRequest,
            IpcJsonContext.Default.AuthorizeToolProposalResponseEnvelope);

    private TResponse Exchange<TRequest, TResponse>(
        string semanticType,
        TRequest payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> requestInfo,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<IpcEnvelope<TResponse>> responseInfo)
    {
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(payload, requestInfo);
        AssertNoSecret(Encoding.UTF8.GetString(utf8));
        using var document = JsonDocument.Parse(utf8);
        var context = new IpcApplicationCommandContext(
            IpcClientKind.AgentRuntime,
            "wp14-state",
            null,
            principal,
            IpcMessageIds.Create(),
            IpcMessageIds.Create(),
            projectId,
            null,
            semanticType,
            document.RootElement.Clone(),
            CancellationToken.None);
        var result = handler.HandleAsync(context).GetAwaiter().GetResult()
                     ?? throw new InvalidOperationException("WP14 IPC command was not handled.");
        var envelope = IpcJson.Deserialize(result.ResponseUtf8, responseInfo);
        return envelope.Payload;
    }

    private static void AssertNoSecret(string? json)
    {
        if (!string.IsNullOrEmpty(json) && SecretRedaction.ContainsSecretMaterial(json))
        {
            throw new InvalidOperationException(IpcErrorCodes.ProviderSecretForbidden);
        }
    }
}
