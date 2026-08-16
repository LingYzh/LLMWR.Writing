using System.Text;
using System.Text.Json;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Prompt;
using LLMW.Writing.Domain.Provider;
using LLMW.Writing.Domain.Runtime;

namespace LLMW.Writing.Application.Provider;

public sealed record ModelInvocationCommand(
    string RunId,
    string TaskId,
    string? AttemptId,
    PromptCompileRequest CompileRequest,
    RouteRequirementProfile Requirements,
    bool Stream,
    string? FallbackFromInvocationId = null,
    string? FallbackReason = null,
    ProviderRetryPolicy? Retry = null,
    ILocalToolExecutor? ToolExecutor = null,
    int ToolRound = 0);

public sealed record ModelInvocationOutcome(
    InvocationRecord Record,
    PromptIr? Prompt,
    IReadOnlyList<ModelRuntimeEvent> Events,
    IReadOnlyList<ToolProposalDecision> ToolProposals,
    string? StructuredOutputJson,
    string? StructuredOutputError);

public sealed class ProviderInvocationCoordinator
{
    public const int MaxToolRounds = 8;
    public const int MaxRetainedStreamEvents = 10_000;

    private readonly IProviderDefinitionStore definitions;
    private readonly IProviderCredentialResolver credentials;
    private readonly IModelCertificationStore protocolProfiles;
    private readonly ITaskCertificationStore taskCertifications;
    private readonly TaskCapabilityCertificationService taskCertService;
    private readonly IPriceSnapshotStore prices;
    private readonly IProviderAdapterResolver adapters;
    private readonly IModelCatalogStore catalog;
    private readonly IProviderInvocationStatePort core;
    private readonly TimeProvider clock;
    private readonly object invocationGate = new();
    private readonly Dictionary<string, ProviderInvocationSnapshot> frozen = new(StringComparer.Ordinal);

    public ProviderInvocationCoordinator(
        IProviderDefinitionStore definitions,
        IProviderCredentialResolver credentials,
        IModelCertificationStore protocolProfiles,
        IPriceSnapshotStore prices,
        IProviderAdapterResolver adapters,
        IProviderInvocationStatePort core,
        IModelCatalogStore? catalog = null,
        ITaskCertificationStore? taskCertifications = null,
        TimeProvider? clock = null)
    {
        this.definitions = definitions;
        this.credentials = credentials;
        this.protocolProfiles = protocolProfiles;
        this.prices = prices;
        this.adapters = adapters;
        this.core = core;
        this.catalog = catalog ?? new MemoryModelCatalogStore();
        this.taskCertifications = taskCertifications ?? new MemoryTaskCertificationStore();
        taskCertService = new TaskCapabilityCertificationService(this.taskCertifications);
        this.clock = clock ?? TimeProvider.System;
    }

    public ModelInvocationOutcome Invoke(ModelInvocationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return InvokeRound(command, command.Retry ?? ProviderRetryPolicy.Default, cancellationToken);
    }

    public ProviderInvocationSnapshot? GetFrozen(string invocationId)
    {
        lock (invocationGate)
        {
            return frozen.TryGetValue(invocationId, out var value) ? value : null;
        }
    }

    private ModelInvocationOutcome InvokeRound(
        ModelInvocationCommand command,
        ProviderRetryPolicy policy,
        CancellationToken cancellationToken)
    {
        var coreSnapshot = core.GetSnapshot(new GetTaskExecutionSnapshotRequest(command.RunId, command.TaskId, command.AttemptId));
        if (!coreSnapshot.OwnershipValid)
        {
            return FailPrepared(command, InvocationFailureClass.None, "TASK_OWNERSHIP_DENIED");
        }

        if (!coreSnapshot.AttemptLegal)
        {
            return FailPrepared(command, InvocationFailureClass.None, "ILLEGAL_COMPLETION_LIFECYCLE");
        }

        var compileRequest = OverlayResults(command.CompileRequest, coreSnapshot);
        if (compileRequest.Results.Any(item => item.Required && item.Stale))
        {
            return FailPrepared(command, InvocationFailureClass.None, "RESULT_REQUIRED_STALE");
        }

        var compiled = PromptCompiler.Compile(compileRequest);
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

        var outcome = DispatchAttempt(command, compiled.Ir, route.Selected, coreSnapshot.SnapshotGeneration, cancellationToken);
        if (ShouldRetry(outcome.Record, policy) && policy.MaxNetworkAttempts > 1)
        {
            var retryCommand = command with
            {
                FallbackFromInvocationId = outcome.Record.Snapshot.InvocationId.Value,
                FallbackReason = "retry:" + outcome.Record.FailureClass,
                Retry = policy with { MaxNetworkAttempts = policy.MaxNetworkAttempts - 1 }
            };
            return InvokeRound(retryCommand, retryCommand.Retry ?? policy, cancellationToken);
        }

        if (ShouldFallback(outcome.Record, command, policy) && route.EligibleOrdered.Count > 1)
        {
            var next = route.EligibleOrdered.FirstOrDefault(item =>
                item.StableId != route.Selected.StableId);
            if (next is not null)
            {
                var fallbackCommand = command with
                {
                    FallbackFromInvocationId = outcome.Record.Snapshot.InvocationId.Value,
                    FallbackReason = "fallback:" + outcome.Record.FailureClass,
                    Requirements = command.Requirements with
                    {
                        PinnedProviderDefinitionId = next.ProviderDefinitionId.Value,
                        PinnedModelId = next.ModelId.Value,
                        AllowFallback = false
                    },
                    Retry = policy with { MaxNetworkAttempts = Math.Max(1, policy.MaxNetworkAttempts) }
                };
                return InvokeRound(fallbackCommand, fallbackCommand.Retry ?? policy, cancellationToken);
            }
        }

        if (command.ToolExecutor is not null &&
            command.ToolRound < MaxToolRounds &&
            outcome.ToolProposals.Any(item => item.MayExecute && item.Request is not null))
        {
            var results = new List<(string CallId, string ToolName, string ResultJson)>();
            foreach (var proposal in outcome.ToolProposals.Where(item => item.MayExecute && item.Request is not null))
            {
                var executed = command.ToolExecutor.Execute(
                    proposal.Request!.ProviderCallId,
                    proposal.Request.ToolName,
                    proposal.Request.ArgumentsJson);
                results.Add((executed.CallId, executed.ToolName, executed.ResultJson));
            }

            var nextCompile = compileRequest with { ToolResults = results };
            var next = command with
            {
                CompileRequest = nextCompile,
                ToolRound = command.ToolRound + 1,
                FallbackFromInvocationId = outcome.Record.Snapshot.InvocationId.Value,
                FallbackReason = "tool_loop"
            };
            return InvokeRound(next, policy, cancellationToken);
        }

        return outcome;
    }

    private ModelInvocationOutcome DispatchAttempt(
        ModelInvocationCommand command,
        PromptIr ir,
        RouteCandidate selected,
        string snapshotGeneration,
        CancellationToken cancellationToken)
    {
        var definition = definitions.FindById(selected.ProviderDefinitionId)
            ?? throw new InvalidOperationException("Routed provider disappeared.");
        var endpoint = ProviderEndpoint.TryCreate(definition.Endpoint, definition.AllowInsecureLocalHttp, out var endpointError);
        if (endpoint is null)
        {
            return FailPrepared(command, InvocationFailureClass.FailedBeforeSend, endpointError ?? "endpoint-malformed");
        }

        var adapter = adapters.Resolve(definition.ProtocolKind);
        if (definition.ProtocolKind == ProtocolKind.OpenAiCompatibleChatCompletions &&
            definition.AdapterExtensions.TryGetValue("thinking", out _) &&
            ir.Tools.Count > 0)
        {
            return FailPrepared(command, InvocationFailureClass.FailedBeforeSend, "DEEPSEEK_THINKING_TOOLS_UNSUPPORTED");
        }

        var invocationId = new InvocationId(Guid.NewGuid().ToString("N"));
        var ext = new Dictionary<string, string>(definition.AdapterExtensions, StringComparer.Ordinal);
        if (ir.OutputContract.Kind == OutputContractKind.StructuredJson &&
            ir.OutputContract.SchemaJson is not null &&
            OutputSchemaSubset.TryValidateSchema(ir.OutputContract.SchemaJson, out _) &&
            CapabilitySupportCodec.IsUsableAsSupported(selected.Certification.SupportFor(ModelCapabilityNames.StructuredJson)))
        {
            ext["strictStructuredOutput"] = "true";
        }

        var invokeRequest = new ProviderInvokeRequest(
            ir,
            selected.ModelId,
            command.Stream,
            ext,
            invocationId.Value);
        var prepared = adapter.Prepare(definition, endpoint, invokeRequest);
        if (!prepared.Succeeded || prepared.Request is null)
        {
            return FailPrepared(command, InvocationFailureClass.FailedBeforeSend, prepared.ErrorCode ?? "PREPARE_FAILED");
        }

        var snapshot = FreezeSnapshot(command, ir, definition, adapter, selected, endpoint, invocationId, prepared.Request);
        lock (invocationGate)
        {
            frozen[snapshot.InvocationId.Value] = snapshot;
        }

        Persist(command, snapshot, ir);
        var revalidated = core.GetSnapshot(new GetTaskExecutionSnapshotRequest(command.RunId, command.TaskId, command.AttemptId));
        if (!string.Equals(revalidated.SnapshotGeneration, snapshotGeneration, StringComparison.Ordinal) &&
            revalidated.RequiredResults.Any(item => item.Required && (item.Stale || item.Missing)))
        {
            var blocked = new InvocationRecord(
                snapshot, InvocationLifecycle.FailedBeforeSend, InvocationFailureClass.FailedBeforeSend,
                null, null, null, NormalizedUsage.Unknown, CostEstimate.Unknown(snapshot.PriceSnapshotId), false, "RESULT_REQUIRED_STALE", false);
            Persist(command, snapshot, ir, blocked);
            return new ModelInvocationOutcome(blocked, ir, [], [], null, "RESULT_REQUIRED_STALE");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            var cancelled = new InvocationRecord(
                snapshot, InvocationLifecycle.CancelRequested, InvocationFailureClass.LocalCancelUnknownRemote,
                null, null, null, NormalizedUsage.Unknown, CostEstimate.Unknown(snapshot.PriceSnapshotId), false, null, false);
            Persist(command, snapshot, ir, cancelled);
            return new ModelInvocationOutcome(cancelled, ir, [], [], null, null);
        }

        var secretResult = credentials.Resolve(definition.CredentialRef);
        if (!secretResult.Succeeded || secretResult.Secret is null)
        {
            var failed = new InvocationRecord(
                snapshot, InvocationLifecycle.FailedBeforeSend, InvocationFailureClass.CredentialUnavailable,
                null, null, null, NormalizedUsage.Unknown, CostEstimate.Unknown(snapshot.PriceSnapshotId), false, null, false);
            Persist(command, snapshot, ir, failed);
            return new ModelInvocationOutcome(failed, ir, [], [], null, null);
        }

        using (secretResult.Secret)
        {
            ProviderInvokeResult invoked;
            try
            {
                invoked = command.Stream
                    ? ConsumeStream(adapter, definition, endpoint, secretResult.Secret, invokeRequest, cancellationToken)
                    : adapter.InvokeAsync(definition, endpoint, secretResult.Secret, invokeRequest, cancellationToken)
                        .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                var cancelled = new InvocationRecord(
                    snapshot, InvocationLifecycle.CancelRequested, InvocationFailureClass.LocalCancelUnknownRemote,
                    null, null, null, NormalizedUsage.Unknown, CostEstimate.Unknown(snapshot.PriceSnapshotId),
                    true, null, false);
                Persist(command, snapshot, ir, cancelled);
                return new ModelInvocationOutcome(cancelled, ir, [], [], null, null);
            }

            var tools = invoked.CompletedToolCalls.Select(call =>
            {
                var capabilityName = ToolCapabilityMap.CanonicalName(call.ToolName);
                var authorization = core.Authorize(new AuthorizeToolProposalRequest(
                    command.RunId, command.TaskId, call.ToolName, call.ArgumentsJson, capabilityName, null));
                CoreToolAuthorizationResult? mapped = authorization.Allowed
                    ? CoreToolAuthorizationResult.Authorized(authorization.CapabilityName)
                    : authorization.Status == "denied"
                        ? CoreToolAuthorizationResult.Denied(authorization.CapabilityName, authorization.DenialCode)
                        : CoreToolAuthorizationResult.Unavailable(authorization.CapabilityName);
                return ToolProposalGuard.Inspect(call, ir.Tools, mapped);
            }).ToArray();

            string? structured = invoked.StructuredOutputJson;
            string? structuredError = null;
            if (ir.OutputContract.Kind == OutputContractKind.StructuredJson)
            {
                var candidate = structured ?? LooksJsonHint(invoked.Events);
                if (ir.OutputContract.SchemaJson is null ||
                    !StructuredOutputValidator.TryValidateObject(candidate, ir.OutputContract.SchemaJson, ir.OutputContract.RequiredProperties, out structuredError))
                {
                    structured = null;
                }
                else
                {
                    structured = candidate;
                }
            }

            var hostedRejected = invoked.CompletedToolCalls.Any(call => ToolProposalGuard.IsProviderHosted(call.ToolName));
            var price = snapshot.PriceSnapshotId is null ? null : prices.FindById(snapshot.PriceSnapshotId);
            var record = new InvocationRecord(
                snapshot,
                invoked.Lifecycle,
                invoked.FailureClass,
                invoked.ProviderRequestId,
                invoked.ProviderResponseId,
                invoked.ProviderReportedModel,
                invoked.Usage,
                CostEstimate.FromReportedUsage(invoked.Usage, price),
                invoked.DuplicateExecutionRisk,
                invoked.RefusalText ?? invoked.ErrorCode,
                hostedRejected);
            Persist(command, snapshot, ir, record);
            return new ModelInvocationOutcome(record, ir, invoked.Events, tools, structured, structuredError);
        }
    }

    private static ProviderInvokeResult ConsumeStream(
        IProviderProtocolAdapter adapter,
        ProviderDefinitionV1 definition,
        ProviderEndpoint endpoint,
        ResolvedProviderSecret secret,
        ProviderInvokeRequest request,
        CancellationToken cancellationToken)
    {
        var events = new List<ModelRuntimeEvent>();
        var tools = new List<ToolCallRequest>();
        ModelRuntimeEvent? terminal = null;
        try
        {
            var enumerator = adapter.StreamAsync(definition, endpoint, secret, request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    var current = enumerator.Current;
                    if (events.Count >= MaxRetainedStreamEvents)
                    {
                        terminal = new ModelRuntimeEvent(ModelRuntimeEventKind.Error, null, null, null, null, null, null, "PROTOCOL_SSE_LIMIT", true);
                        events.Add(terminal);
                        break;
                    }

                    events.Add(current);
                    if (current.Kind == ModelRuntimeEventKind.ToolCallCompleted &&
                        current.ProviderCallId is not null &&
                        current.ToolName is not null)
                    {
                        tools.Add(new ToolCallRequest(current.ProviderCallId, current.ToolName, current.ArgumentsJson ?? "{}"));
                    }

                    if (current.Terminal)
                    {
                        terminal = current;
                        break;
                    }
                }
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ProviderInvokeResult(
                InvocationLifecycle.CancelRequested, InvocationFailureClass.LocalCancelUnknownRemote,
                events, null, null, null, NormalizedUsage.Unknown, tools, null, null, "cancel", true);
        }
        catch (OperationCanceledException)
        {
            return new ProviderInvokeResult(
                InvocationLifecycle.OutcomeUnknown, InvocationFailureClass.TimeoutOutcomeUnknown,
                events, null, null, null, NormalizedUsage.Unknown, tools, null, null, "timeout", true);
        }
        catch (HttpRequestException)
        {
            return new ProviderInvokeResult(
                InvocationLifecycle.OutcomeUnknown, InvocationFailureClass.TransportOutcomeUnknown,
                events, null, null, null, NormalizedUsage.Unknown, tools, null, null, "transport", true);
        }

        if (terminal is null)
        {
            return new ProviderInvokeResult(
                InvocationLifecycle.Incomplete, InvocationFailureClass.StreamBroken,
                events, null, null, null, NormalizedUsage.Unknown, tools, null, null, "eof-before-terminal", false);
        }

        var usage = events.LastOrDefault(item => item.Usage is not null)?.Usage ?? NormalizedUsage.Unknown;
        return terminal.Kind switch
        {
            ModelRuntimeEventKind.Completed when tools.Count > 0 =>
                new ProviderInvokeResult(InvocationLifecycle.Completed, InvocationFailureClass.None, events, null, null, null, usage, tools, null, null, null, false),
            ModelRuntimeEventKind.Completed =>
                new ProviderInvokeResult(InvocationLifecycle.Completed, InvocationFailureClass.None, events, null, null, null, usage, tools, null, null, null, false),
            ModelRuntimeEventKind.ToolCallCompleted =>
                new ProviderInvokeResult(InvocationLifecycle.Completed, InvocationFailureClass.None, events, null, null, null, usage, tools, null, null, null, false),
            ModelRuntimeEventKind.Incomplete =>
                new ProviderInvokeResult(InvocationLifecycle.Incomplete, InvocationFailureClass.IncompleteGeneration, events, null, null, null, usage, tools, null, null, terminal.ErrorCode, false),
            ModelRuntimeEventKind.Refusal =>
                new ProviderInvokeResult(InvocationLifecycle.Rejected, InvocationFailureClass.ProviderRefusal, events, null, null, null, usage, tools, null, terminal.Text, terminal.ErrorCode, false),
            ModelRuntimeEventKind.Error when terminal.ErrorCode is "HttpUnauthorized" or "401" or "403" =>
                new ProviderInvokeResult(InvocationLifecycle.Rejected, InvocationFailureClass.HttpUnauthorized, events, null, null, null, usage, tools, null, null, terminal.ErrorCode, false),
            ModelRuntimeEventKind.Error when terminal.ErrorCode is "HttpRateLimited" or "429" =>
                new ProviderInvokeResult(InvocationLifecycle.Rejected, InvocationFailureClass.HttpRateLimited, events, null, null, null, usage, tools, null, null, terminal.ErrorCode, false),
            ModelRuntimeEventKind.Error when terminal.ErrorCode is "HttpServerError" or "500" =>
                new ProviderInvokeResult(InvocationLifecycle.FailedAfterPossibleSend, InvocationFailureClass.HttpServerError, events, null, null, null, usage, tools, null, null, terminal.ErrorCode, false),
            _ => new ProviderInvokeResult(
                InvocationLifecycle.Rejected, InvocationFailureClass.MalformedProtocol, events, null, null, null, usage, tools, null, null, terminal.ErrorCode, false)
        };
    }

    private static bool ShouldRetry(InvocationRecord record, ProviderRetryPolicy policy) =>
        policy.MaxNetworkAttempts > 1 &&
        InvocationStateMachine.MayAutoRetry(record.FailureClass, record.Lifecycle);

    private static bool ShouldFallback(InvocationRecord record, ModelInvocationCommand command, ProviderRetryPolicy policy)
    {
        if (record.Lifecycle is InvocationLifecycle.Completed or InvocationLifecycle.Incomplete)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(command.Requirements.PinnedProviderDefinitionId) ||
            !string.IsNullOrWhiteSpace(command.Requirements.PinnedModelId))
        {
            return policy.AllowFallbackWhenPinned && command.Requirements.AllowFallback;
        }

        return command.Requirements.AllowFallback;
    }

    private static PromptCompileRequest OverlayResults(PromptCompileRequest request, GetTaskExecutionSnapshotResponse snapshot)
    {
        if (snapshot.RequiredResults.Length == 0)
        {
            return request;
        }

        var overlay = snapshot.RequiredResults
            .Select(item => (item.ResultId, item.Text ?? "", item.Required, item.Stale || item.Missing))
            .ToArray();
        return request with { Results = overlay };
    }

    private static string? LooksJsonHint(IReadOnlyList<ModelRuntimeEvent> events)
    {
        var text = events.LastOrDefault(item => item.Kind is ModelRuntimeEventKind.TextCompleted or ModelRuntimeEventKind.Completed)?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[') ? text : null;
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
            var endpoint = ProviderEndpoint.TryCreate(definition.Endpoint, definition.AllowInsecureLocalHttp, out _);
            if (endpoint is null)
            {
                continue;
            }

            IProviderProtocolAdapter adapter;
            try
            {
                adapter = adapters.Resolve(definition.ProtocolKind);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            var models = new HashSet<string>(StringComparer.Ordinal);
            if (definition.DefaultModelId is { } defaultModel)
            {
                models.Add(defaultModel.Value);
            }

            foreach (var alias in definition.ModelAliases.Values)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    models.Add(alias);
                }
            }

            foreach (var entry in catalog.List(definition.ProviderDefinitionId))
            {
                models.Add(entry.ModelId.Value);
            }

            if (models.Count == 0)
            {
                models.Add("unspecified");
            }

            var credential = credentials.Resolve(definition.CredentialRef);
            var available = credential.Succeeded;
            credential.Secret?.Dispose();

            foreach (var modelValue in models.OrderBy(value => value, StringComparer.Ordinal))
            {
                var model = new ModelId(modelValue);
                var protocol = protocolProfiles.Find(definition.ProviderDefinitionId, model)
                    ?? ModelCertificationRecord.Uncertified(
                        definition.ProviderDefinitionId,
                        definition.Revision,
                        endpoint.CanonicalUri,
                        adapter.AdapterId,
                        adapter.AdapterVersion,
                        model);
                if (protocol.IsStaleFor(
                        definition.Revision,
                        endpoint.CanonicalUri,
                        adapter.AdapterId,
                        adapter.AdapterVersion,
                        ModelCertificationRecord.CurrentProbeSuiteVersion,
                        null) &&
                    protocol.State != CertificationState.Uncertified)
                {
                    protocol = protocol with { State = CertificationState.Stale };
                }

                var task = taskCertService.ResolveCurrent(
                    definition.ProviderDefinitionId,
                    model,
                    definition.Revision,
                    endpoint.CanonicalUri,
                    adapter.AdapterId,
                    adapter.AdapterVersion,
                    TaskCapabilityCertification.CurrentEvaluationSuiteVersion,
                    null);
                var contextLimit = catalog.List(definition.ProviderDefinitionId)
                    .FirstOrDefault(item => item.ModelId.Value == model.Value)?.DeclaredContextLimit;
                list.Add(new RouteCandidate(
                    definition.ProviderDefinitionId,
                    definition.Revision,
                    model,
                    definition.ProtocolKind,
                    definition.Enabled,
                    available,
                    protocol,
                    contextLimit,
                    definition.RoutingPriority,
                    task));
            }
        }

        return list;
    }

    private ProviderInvocationSnapshot FreezeSnapshot(
        ModelInvocationCommand command,
        PromptIr ir,
        ProviderDefinitionV1 definition,
        IProviderProtocolAdapter adapter,
        RouteCandidate selected,
        ProviderEndpoint endpoint,
        InvocationId invocationId,
        PreparedProviderRequest prepared)
    {
        var wire = PromptDigests.WireRequestDigestFromPrepared(
            adapter.AdapterId,
            adapter.AdapterVersion,
            prepared.Method,
            prepared.Path,
            prepared.Stream,
            prepared.CanonicalSemanticBody,
            prepared.NonSecretHeaders);
        var cert = protocolProfiles.Find(definition.ProviderDefinitionId, selected.ModelId);
        var priceSnapshotId = definition.PriceSnapshotId;
        if (!string.IsNullOrWhiteSpace(priceSnapshotId) && prices.FindById(priceSnapshotId) is null)
        {
            priceSnapshotId = null;
        }

        return new ProviderInvocationSnapshot(
            invocationId,
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
            Utf8Digest.Sha256Hex(prepared.CanonicalSemanticBody),
            PromptDigests.ToolSchemaDigest(ir.Tools),
            PromptDigests.OutputSchemaDigest(ir.OutputContract),
            definition.DataPolicy,
            priceSnapshotId,
            definition.CredentialRef,
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            command.FallbackFromInvocationId,
            command.FallbackReason,
            endpoint.CanonicalUri,
            ProviderIdentity.EndpointProfileDigest(
                definition.ProtocolKind,
                endpoint.CanonicalUri,
                adapter.AdapterId,
                adapter.AdapterVersion,
                definition.DataPolicy,
                definition.DefaultModelId?.Value,
                definition.ModelAliases,
                definition.AdapterExtensions),
            credentials.BindingGeneration(definition.CredentialRef));
    }

    private void Persist(
        ModelInvocationCommand command,
        ProviderInvocationSnapshot snapshot,
        PromptIr ir,
        InvocationRecord? record = null)
    {
        var payload = snapshot.CanonicalJson();
        string? recordJson = null;
        if (record is not null)
        {
            recordJson = MergeRecord(snapshot, record);
        }

        _ = core.Persist(new PersistProviderInvocationRequest(
            snapshot.InvocationId.Value,
            command.RunId,
            command.TaskId,
            command.AttemptId,
            payload,
            recordJson,
            WriteRunIdentityDigest(snapshot, ir),
            snapshot.InvocationId.Value));
    }

    private static string WriteRunIdentityDigest(ProviderInvocationSnapshot snapshot, PromptIr ir)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("effectivePromptDigest", ir.EffectivePromptDigest);
            writer.WriteString("modelId", snapshot.EffectiveRoutedModelId.Value);
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

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
