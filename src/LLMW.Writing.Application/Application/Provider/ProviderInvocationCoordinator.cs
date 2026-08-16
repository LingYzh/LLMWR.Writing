using System.Text;
using System.Text.Json;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Domain.Prompt;
using LLMW.Writing.Domain.Provider;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Provider;

public sealed record ModelInvocationCommand(
    string RunId,
    string TaskId,
    string? AttemptId,
    PromptCompileRequest CompileRequest,
    RouteRequirementProfile Requirements,
    CapabilityEvaluationRequest? ToolCapability,
    bool Stream,
    string? FallbackFromInvocationId = null,
    string? FallbackReason = null);

public sealed record ModelInvocationOutcome(
    InvocationRecord Record,
    PromptIr? Prompt,
    IReadOnlyList<ModelRuntimeEvent> Events,
    IReadOnlyList<ToolProposalDecision> ToolProposals,
    string? StructuredOutputJson,
    string? StructuredOutputError);

public sealed class ProviderInvocationCoordinator
{
    private readonly IProviderDefinitionStore definitions;
    private readonly IProviderCredentialResolver credentials;
    private readonly IModelCertificationStore certifications;
    private readonly IPriceSnapshotStore prices;
    private readonly IProviderAdapterResolver adapters;
    private readonly IRuntimePersistence store;
    private readonly RuntimeSchedulerService scheduler;
    private readonly TimeProvider clock;
    private readonly object invocationGate = new();
    private readonly Dictionary<string, ProviderInvocationSnapshot> frozen = new(StringComparer.Ordinal);

    public ProviderInvocationCoordinator(
        IProviderDefinitionStore definitions,
        IProviderCredentialResolver credentials,
        IModelCertificationStore certifications,
        IPriceSnapshotStore prices,
        IProviderAdapterResolver adapters,
        IRuntimePersistence store,
        RuntimeSchedulerService scheduler,
        TimeProvider? clock = null)
    {
        this.definitions = definitions;
        this.credentials = credentials;
        this.certifications = certifications;
        this.prices = prices;
        this.adapters = adapters;
        this.store = store;
        this.scheduler = scheduler;
        this.clock = clock ?? TimeProvider.System;
    }

    public ModelInvocationOutcome Invoke(ModelInvocationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        foreach (var result in command.CompileRequest.Results)
        {
            if (result.Required && result.Stale)
            {
                return FailPrepared(command, InvocationFailureClass.None, "RESULT_REQUIRED_STALE");
            }
        }

        var compiled = PromptCompiler.Compile(command.CompileRequest);
        if (!compiled.Succeeded || compiled.Ir is null)
        {
            return FailPrepared(command, InvocationFailureClass.None, compiled.Failure?.Code ?? "PROMPT_COMPILE_FAILED");
        }

        var candidates = BuildCandidates();
        var route = ProviderRouter.Route(candidates, command.Requirements);
        if (route.Selected is null)
        {
            return FailPrepared(command, InvocationFailureClass.None, route.FailureCode ?? "ROUTE_NO_ELIGIBLE_CANDIDATE");
        }

        var definition = definitions.FindById(route.Selected.ProviderDefinitionId)
            ?? throw new InvalidOperationException("Routed provider disappeared.");
        var endpoint = ProviderEndpoint.TryCreate(definition.Endpoint, definition.AllowInsecureLocalHttp, out var endpointError);
        if (endpoint is null)
        {
            return FailPrepared(command, InvocationFailureClass.FailedBeforeSend, endpointError ?? "endpoint-malformed");
        }

        var adapter = adapters.Resolve(definition.ProtocolKind);
        var snapshot = FreezeSnapshot(command, compiled.Ir, definition, adapter, route.Selected);
        lock (invocationGate)
        {
            frozen[snapshot.InvocationId.Value] = snapshot;
        }

        PersistSnapshot(command, snapshot, compiled.Ir);

        if (cancellationToken.IsCancellationRequested)
        {
            var cancelled = new InvocationRecord(
                snapshot, InvocationLifecycle.CancelRequested, InvocationFailureClass.LocalCancelUnknownRemote,
                null, null, null, NormalizedUsage.Unknown, CostEstimate.Unknown(snapshot.PriceSnapshotId), false, null, false);
            PersistSnapshot(command, snapshot, compiled.Ir, cancelled);
            return new ModelInvocationOutcome(cancelled, compiled.Ir, [], [], null, null);
        }

        var secretResult = credentials.Resolve(definition.CredentialRef);
        if (!secretResult.Succeeded || secretResult.Secret is null)
        {
            var failed = new InvocationRecord(
                snapshot, InvocationLifecycle.FailedBeforeSend, InvocationFailureClass.CredentialUnavailable,
                null, null, null, NormalizedUsage.Unknown, CostEstimate.Unknown(snapshot.PriceSnapshotId), false, null, false);
            PersistSnapshot(command, snapshot, compiled.Ir, failed);
            return new ModelInvocationOutcome(failed, compiled.Ir, [], [], null, null);
        }

        using (secretResult.Secret)
        {
            ProviderInvokeResult invoked;
            try
            {
                invoked = adapter.InvokeAsync(
                    definition,
                    endpoint,
                    secretResult.Secret,
                    new ProviderInvokeRequest(compiled.Ir, route.Selected.ModelId, command.Stream, definition.AdapterExtensions),
                    cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                var cancelled = new InvocationRecord(
                    snapshot, InvocationLifecycle.CancelRequested, InvocationFailureClass.LocalCancelUnknownRemote,
                    null, null, null, NormalizedUsage.Unknown, CostEstimate.Unknown(snapshot.PriceSnapshotId),
                    true, null, false);
                PersistSnapshot(command, snapshot, compiled.Ir, cancelled);
                return new ModelInvocationOutcome(cancelled, compiled.Ir, [], [], null, null);
            }

            var tools = invoked.CompletedToolCalls
                .Select(call => ToolProposalGuard.Inspect(call, compiled.Ir.Tools, command.ToolCapability))
                .ToArray();
            string? structured = invoked.StructuredOutputJson;
            string? structuredError = null;
            if (compiled.Ir.OutputContract.Kind == OutputContractKind.StructuredJson)
            {
                if (!StructuredOutputValidator.TryValidateObject(structured, compiled.Ir.OutputContract.RequiredProperties, out structuredError))
                {
                    structured = null;
                }
            }

            var hostedRejected = invoked.CompletedToolCalls.Any(call => ToolProposalGuard.IsProviderHosted(call.ToolName));
            var record = new InvocationRecord(
                snapshot with
                {
                    EffectiveModelId = invoked.ProviderReportedModel is null
                        ? snapshot.EffectiveModelId
                        : new ModelId(invoked.ProviderReportedModel)
                },
                invoked.Lifecycle,
                invoked.FailureClass,
                invoked.ProviderRequestId,
                invoked.ProviderResponseId,
                invoked.ProviderReportedModel,
                invoked.Usage,
                CostEstimate.Unknown(snapshot.PriceSnapshotId),
                invoked.DuplicateExecutionRisk,
                invoked.RefusalText,
                hostedRejected);
            PersistSnapshot(command, record.Snapshot, compiled.Ir, record);
            return new ModelInvocationOutcome(record, compiled.Ir, invoked.Events, tools, structured, structuredError);
        }
    }

    public ProviderInvocationSnapshot? GetFrozen(string invocationId)
    {
        lock (invocationGate)
        {
            return frozen.TryGetValue(invocationId, out var value) ? value : null;
        }
    }

    private ModelInvocationOutcome FailPrepared(ModelInvocationCommand command, InvocationFailureClass failure, string code)
    {
        var emptyDigest = Utf8Digest.Sha256Hex(code);
        var snapshot = new ProviderInvocationSnapshot(
            new InvocationId(Guid.NewGuid().ToString("N")),
            command.RunId,
            command.TaskId,
            command.AttemptId,
            new ProviderDefinitionId("none"),
            new ProviderRevision(0),
            "none",
            "0",
            new ModelId("none"),
            new ModelId("none"),
            null,
            null,
            emptyDigest,
            emptyDigest,
            emptyDigest,
            emptyDigest,
            emptyDigest,
            ProviderDataBehavior.Unknown,
            null,
            null,
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            command.FallbackFromInvocationId,
            command.FallbackReason ?? code);
        var record = new InvocationRecord(
            snapshot,
            failure == InvocationFailureClass.FailedBeforeSend ? InvocationLifecycle.FailedBeforeSend : InvocationLifecycle.Rejected,
            failure,
            null, null, null, NormalizedUsage.Unknown, CostEstimate.Unknown(null), false, code, false);
        return new ModelInvocationOutcome(record, null, [], [], null, code);
    }

    private List<RouteCandidate> BuildCandidates()
    {
        var list = new List<RouteCandidate>();
        foreach (var definition in definitions.List())
        {
            var model = definition.DefaultModelId ?? new ModelId("unspecified");
            var certification = certifications.Find(definition.ProviderDefinitionId, model)
                ?? ModelCertificationRecord.Uncertified(
                    definition.ProviderDefinitionId,
                    definition.Revision,
                    definition.Endpoint,
                    definition.ProtocolKind.ToString(),
                    "unknown",
                    model);
            if (certification.IsStaleFor(definition.Revision, definition.Endpoint, certification.ProtocolAdapterId, certification.ProtocolAdapterVersion) &&
                certification.State != CertificationState.Uncertified)
            {
                certification = certification with { State = CertificationState.Stale };
            }

            var credential = credentials.Resolve(definition.CredentialRef);
            var available = credential.Succeeded;
            credential.Secret?.Dispose();
            var endpoint = ProviderEndpoint.TryCreate(definition.Endpoint, definition.AllowInsecureLocalHttp, out _);
            if (endpoint is null)
            {
                continue;
            }

            list.Add(new RouteCandidate(
                definition.ProviderDefinitionId,
                definition.Revision,
                model,
                definition.ProtocolKind,
                definition.Enabled,
                available,
                certification,
                null,
                definition.RoutingPriority));
        }

        return list;
    }

    private ProviderInvocationSnapshot FreezeSnapshot(
        ModelInvocationCommand command,
        PromptIr ir,
        ProviderDefinitionV1 definition,
        IProviderProtocolAdapter adapter,
        RouteCandidate selected)
    {
        var generation = "{\"temperature\":null}";
        var wire = PromptDigests.WireRequestDigest(
            ir, definition.ProtocolKind, adapter.AdapterId, adapter.AdapterVersion, selected.ModelId.Value, generation);
        var cert = certifications.Find(definition.ProviderDefinitionId, selected.ModelId);
        var priceSnapshotId = definition.PriceSnapshotId;
        if (!string.IsNullOrWhiteSpace(priceSnapshotId) && prices.FindById(priceSnapshotId) is null)
        {
            priceSnapshotId = null;
        }

        return new ProviderInvocationSnapshot(
            new InvocationId(Guid.NewGuid().ToString("N")),
            command.RunId,
            command.TaskId,
            command.AttemptId,
            definition.ProviderDefinitionId,
            definition.Revision,
            adapter.AdapterId,
            adapter.AdapterVersion,
            selected.ModelId,
            selected.ModelId,
            cert?.CertificationId,
            ir.PromptConfigId,
            ir.EffectivePromptDigest,
            wire,
            Utf8Digest.Sha256Hex(generation),
            PromptDigests.ToolSchemaDigest(ir.Tools),
            PromptDigests.OutputSchemaDigest(ir.OutputContract),
            definition.DataPolicy,
            priceSnapshotId,
            definition.CredentialRef,
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            command.FallbackFromInvocationId,
            command.FallbackReason);
    }

    private void PersistSnapshot(
        ModelInvocationCommand command,
        ProviderInvocationSnapshot snapshot,
        PromptIr ir,
        InvocationRecord? record = null)
    {
        var run = store.GetRun(command.RunId);
        if (run is not null)
        {
            store.UpdateRun(run with
            {
                ProviderId = snapshot.ProviderDefinitionId.Value,
                ModelId = snapshot.EffectiveModelId.Value,
                PromptConfigId = ir.PromptConfigId,
                EffectivePromptDigest = ir.EffectivePromptDigest,
                UpdatedAtMs = clock.GetUtcNow().ToUnixTimeMilliseconds()
            });
        }

        var existing = store.CheckpointsForRun(command.RunId)
            .OrderBy(item => item.CreatedAtMs)
            .Select(item =>
            {
                try
                {
                    return CanonicalJson.Parse(item.PayloadJson, item.SchemaVersion);
                }
                catch (CheckpointSchemaException)
                {
                    return null;
                }
            })
            .LastOrDefault(item => item is not null);

        var log = new List<string>();
        if (existing is not null)
        {
            log.AddRange(existing.InvocationLog);
        }

        var payload = snapshot.CanonicalJson();
        if (record is not null)
        {
            payload = MergeRecord(snapshot, record);
        }

        log.Add(payload);
        var checkpoint = CheckpointV1.Create(
            existing?.ApprovedPlanReference ?? "provider-invocation",
            existing?.ApprovedPlanDigest,
            existing?.DagTaskStateJson ?? "{}",
            existing?.AgentStateJson ?? "{}",
            "provider_invocation",
            existing?.CriticalMessages ?? [],
            existing?.ToolReferences ?? [],
            existing?.ApprovalReferences ?? [],
            existing?.ContextPointers ?? [],
            existing?.ArtifactEvidenceReferences ?? [],
            existing?.InputDigestSet ?? [],
            snapshot.PromptConfigId,
            snapshot.ProviderDefinitionId.Value,
            snapshot.EffectiveModelId.Value,
            snapshot.EffectivePromptDigest,
            log);
        var digestObject = WriteRunIdentityDigest(snapshot, ir);
        _ = scheduler.PersistCheckpoint(
            command.RunId,
            command.TaskId,
            CheckpointV1.CurrentSchemaVersion,
            CanonicalJson.WriteCheckpoint(checkpoint),
            digestObject);
    }

    private static string WriteRunIdentityDigest(ProviderInvocationSnapshot snapshot, PromptIr ir)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("effectivePromptDigest", ir.EffectivePromptDigest);
            writer.WriteString("modelId", snapshot.EffectiveModelId.Value);
            writer.WriteString("promptConfigId", ir.PromptConfigId);
            writer.WriteString("providerId", snapshot.ProviderDefinitionId.Value);
            writer.WriteString("wireRequestDigest", snapshot.WireRequestDigest);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string MergeRecord(ProviderInvocationSnapshot snapshot, InvocationRecord record)
    {
        using var document = JsonDocument.Parse(snapshot.CanonicalJson());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteString("lifecycle", record.Lifecycle.ToString());
            writer.WriteString("failureClass", record.FailureClass.ToString());
            if (!string.IsNullOrEmpty(record.ProviderRequestId))
            {
                writer.WriteString("providerRequestId", record.ProviderRequestId);
            }

            if (!string.IsNullOrEmpty(record.ProviderResponseId))
            {
                writer.WriteString("providerResponseId", record.ProviderResponseId);
            }

            if (!string.IsNullOrEmpty(record.ProviderReportedModel))
            {
                writer.WriteString("providerReportedModel", record.ProviderReportedModel);
            }

            writer.WritePropertyName("usage");
            using (var usage = JsonDocument.Parse(record.Usage.CanonicalJson()))
            {
                usage.RootElement.WriteTo(writer);
            }

            writer.WriteBoolean("duplicateExecutionRisk", record.DuplicateExecutionRisk);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
