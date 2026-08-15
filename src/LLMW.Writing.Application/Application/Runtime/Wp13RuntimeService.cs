using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;
using RuntimeTaskStatus = LLMW.Writing.Domain.Runtime.TaskStatus;

namespace LLMW.Writing.Application.Runtime;

public sealed class Wp13RuntimeService : IEffectiveOversightSource, IDelegatedDecisionSink
{
    public const string DependencyProposalKind = "result_dependency_proposal";

    private readonly IRuntimePersistence store;
    private readonly RuntimeSchedulerService scheduler;
    private readonly ISecurityClock clock;
    private readonly IApplicationOversightDefaults applicationDefaults;
    private readonly IUserSpecialistProfileStore userSpecialists;
    private readonly IBuiltInSpecialistCatalog builtIns;
    private readonly ISemanticCompletionEvaluator semanticEvaluator;
    private readonly ISchedulerFaultInjector faults;

    public Wp13RuntimeService(
        IRuntimePersistence store,
        RuntimeSchedulerService scheduler,
        ISecurityClock clock,
        IApplicationOversightDefaults? applicationDefaults = null,
        IUserSpecialistProfileStore? userSpecialists = null,
        IBuiltInSpecialistCatalog? builtIns = null,
        ISemanticCompletionEvaluator? semanticEvaluator = null,
        ISchedulerFaultInjector? faults = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.applicationDefaults = applicationDefaults ?? new MemoryApplicationOversightDefaults();
        this.userSpecialists = userSpecialists ?? new MemoryUserSpecialistProfileStore();
        this.builtIns = builtIns ?? SyntheticBuiltInSpecialistCatalog.Instance;
        this.semanticEvaluator = semanticEvaluator ?? UnavailableSemanticCompletionEvaluator.Instance;
        this.faults = faults ?? NoSchedulerFaultInjector.Instance;
    }

    public EffectiveOversightPolicy Resolve(string? projectId, string? storylineId, string? taskId)
    {
        var checkpoints = new HashSet<string>(
            store.LoadSnapshot().Checkpoints.Select(item => item.CheckpointId),
            StringComparer.Ordinal);
        return OversightResolver.Resolve(
            applicationDefaults.Current,
            store.ListOversightOverrides(),
            checkpoints,
            projectId,
            storylineId,
            taskId);
    }

    public void Record(DelegatedDecisionRecord record) => store.InsertDelegatedDecision(record);

    public RuntimeResult<SubmitResultArtifactResponse> SubmitResultArtifact(
        SubmitResultArtifactRequest request,
        CallerPrincipal? principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (principal is { Kind: not PrincipalKind.AgentRun })
        {
            return RuntimeResults.Fail<SubmitResultArtifactResponse>(RuntimeError.OversightDenied, "agent-session-required");
        }

        var task = store.GetTask(request.TaskId);
        if (task is null)
        {
            return RuntimeResults.Fail<SubmitResultArtifactResponse>(RuntimeError.NotFound, "task");
        }

        if (!ResultArtifactStatusCodec.TryParse(request.Status, out _))
        {
            return RuntimeResults.Fail<SubmitResultArtifactResponse>(RuntimeError.CompletionFailed, "invalid-status");
        }

        var artifact = ResultArtifactCanonicalJson.ParseColumns(
            Guid.NewGuid().ToString("D"),
            request.TaskId,
            request.Status,
            request.ConclusionJson,
            request.FindingsJson,
            request.EvidenceJson,
            request.UncertaintyJson,
            request.DiagnosticsJson,
            request.FreshnessJson,
            clock.UtcNow.ToUnixTimeMilliseconds());
        if (ResultArtifactCanonicalJson.ContainsTranscript(artifact) ||
            SecretRedaction.ContainsSecretMaterial(ResultArtifactCanonicalJson.Write(artifact)))
        {
            artifact = ResultArtifactCanonicalJson.ParseColumns(
                artifact.ResultArtifactId,
                artifact.TaskId,
                request.Status,
                SecretRedaction.RedactObjectJson(request.ConclusionJson),
                SecretRedaction.RedactObjectJson(request.FindingsJson),
                request.EvidenceJson,
                SecretRedaction.RedactObjectJson(request.UncertaintyJson),
                SecretRedaction.RedactObjectJson(request.DiagnosticsJson),
                request.FreshnessJson,
                artifact.ProducedAtMs);
        }

        var durable = ResultArtifactCanonicalJson.ToDurable(artifact);
        store.InsertResultArtifact(durable);
        RecomputeConsumersOf(request.TaskId, durable.ResultArtifactId);
        return RuntimeResults.Success(new SubmitResultArtifactResponse(durable.ResultArtifactId, durable.Status));
    }

    public RuntimeResult<TaskCompletionOutcome> RequestTaskCompletion(string taskId, CallerPrincipal? principal)
    {
        if (principal is { Kind: not PrincipalKind.AgentRun })
        {
            return RuntimeResults.Fail<TaskCompletionOutcome>(RuntimeError.OversightDenied, "agent-session-required");
        }

        var task = store.GetTask(taskId);
        if (task is null)
        {
            return RuntimeResults.Fail<TaskCompletionOutcome>(RuntimeError.NotFound, "task");
        }

        if (TaskStatusCodec.TryParse(task.Status, out var existingStatus) &&
            existingStatus == RuntimeTaskStatus.Completed)
        {
            var existing = store.GetLatestResultArtifact(taskId);
            if (existing is null)
            {
                return RuntimeResults.Fail<TaskCompletionOutcome>(RuntimeError.CompletionFailed, "completed-without-result");
            }

            return RuntimeResults.Success(new TaskCompletionOutcome("pass", existing.ResultArtifactId, []));
        }

        var artifactRecord = store.GetLatestResultArtifact(taskId);
        var artifact = artifactRecord is null ? null : ResultArtifactCanonicalJson.FromDurable(artifactRecord);
        var contract = TaskCompletionContractCanonicalJson.Parse(task.CompletionContractJson);
        var semantic = contract.HasSemanticCriteria && artifact is not null
            ? semanticEvaluator.Evaluate(contract, artifact)
            : SemanticCompletionOutcome.Pass;
        var check = TaskCompletionContractChecker.Check(
            contract,
            new CompletionCheckInputs(
                artifact?.OutputKeys ?? new HashSet<string>(StringComparer.Ordinal),
                CompletedTaskIds(),
                CurrentInputRefs(artifact),
                artifact?.OutputKeys ?? new HashSet<string>(StringComparer.Ordinal),
                artifact?.BlockingDiagnosticCount ?? 0,
                store.DependenciesForConsumer(taskId),
                artifactRecord is not null,
                semantic));
        if (check.Outcome == CompletionCheckOutcome.SemanticReviewRequired)
        {
            return RuntimeResults.Fail<TaskCompletionOutcome>(RuntimeError.SemanticReviewRequired, "semantic-review-required");
        }

        if (check.Outcome is not CompletionCheckOutcome.Pass)
        {
            return RuntimeResults.Fail<TaskCompletionOutcome>(
                RuntimeError.CompletionFailed,
                string.Join(',', check.Failures));
        }

        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            store.InTransaction(() =>
            {
                if (faults.Fault == SchedulerFaultPoint.AfterTaskBeforeResultPersist)
                {
                    store.UpdateTaskStatus(taskId, TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Completed), now);
                    throw new SchedulerFaultInjectedException(SchedulerFaultPoint.AfterTaskBeforeResultPersist);
                }

                if (artifactRecord is null)
                {
                    throw new InvalidOperationException("required-result-artifact-missing");
                }

                if (faults.Fault == SchedulerFaultPoint.AfterResultBeforeTaskComplete)
                {
                    throw new SchedulerFaultInjectedException(SchedulerFaultPoint.AfterResultBeforeTaskComplete);
                }

                CompleteAttempt(taskId, now);
                store.UpdateTaskStatus(taskId, TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Completed), now);
                RecomputeConsumersOf(taskId, artifactRecord.ResultArtifactId);
            });
        }
        catch (SchedulerFaultInjectedException)
        {
            throw;
        }

        return RuntimeResults.Success(new TaskCompletionOutcome("pass", artifactRecord!.ResultArtifactId, []));
    }

    public RuntimeResult<GetResultArtifactResponse> GetResultArtifact(string taskId, string? resultArtifactId)
    {
        var record = string.IsNullOrWhiteSpace(resultArtifactId)
            ? store.GetLatestResultArtifact(taskId)
            : store.GetResultArtifact(resultArtifactId);
        if (record is null)
        {
            return RuntimeResults.Fail<GetResultArtifactResponse>(RuntimeError.NotFound, "result-artifact");
        }

        return RuntimeResults.Success(new GetResultArtifactResponse(
            record.ResultArtifactId,
            record.TaskId,
            record.Status,
            record.ConclusionJson ?? "{}",
            record.FindingsJson ?? "{}",
            record.EvidenceJson ?? "{}",
            record.UncertaintyJson ?? "{}",
            record.DiagnosticsJson ?? "{}",
            record.FreshnessJson,
            record.ProducedAtMs));
    }

    public RuntimeResult<GetTaskHandoffResponse> GetTaskHandoff(string consumerTaskId, bool includeEvidence)
    {
        var deps = store.DependenciesForConsumer(consumerTaskId);
        var resultIds = new List<string>();
        var evidenceIds = new List<string>();
        var warnings = new List<string>();
        foreach (var dependency in deps)
        {
            var evaluation = ResultDependencyPolicy.Evaluate(dependency);
            if (evaluation.HasWarning)
            {
                warnings.Add(dependency.DependencyId + ":" + ResultDependencyStatusCodec.ToDurableValue(evaluation.EffectiveStatus));
            }

            if (!string.IsNullOrWhiteSpace(dependency.ResultArtifactId))
            {
                resultIds.Add(dependency.ResultArtifactId);
                if (includeEvidence)
                {
                    var artifact = store.GetResultArtifact(dependency.ResultArtifactId);
                    if (artifact is not null)
                    {
                        evidenceIds.AddRange(ResultArtifactCanonicalJson.FromDurable(artifact).EvidenceIds);
                    }
                }
            }
        }

        return RuntimeResults.Success(new GetTaskHandoffResponse(
            consumerTaskId,
            resultIds.Distinct(StringComparer.Ordinal).ToArray(),
            evidenceIds.Distinct(StringComparer.Ordinal).ToArray(),
            warnings.ToArray(),
            IncludeTranscript: false));
    }

    public RuntimeResult<CreateResultDependencyResponse> CreateResultDependency(
        string consumerTaskId,
        string producerTaskId,
        string kind)
    {
        if (!ResultDependencyKindCodec.TryParse(kind, out var parsedKind))
        {
            return RuntimeResults.Fail<CreateResultDependencyResponse>(RuntimeError.IllegalTransition, "invalid-kind");
        }

        if (store.GetTask(consumerTaskId) is null || store.GetTask(producerTaskId) is null)
        {
            return RuntimeResults.Fail<CreateResultDependencyResponse>(RuntimeError.NotFound, "task");
        }

        var producerResult = store.GetLatestResultArtifact(producerTaskId);
        var status = ResultDependencyPolicy.Recompute(
            parsedKind,
            producerResult?.ResultArtifactId,
            producerResult is null ? null : ResultArtifactCanonicalJson.FromDurable(producerResult).Freshness.State,
            producerResult is not null);
        var id = Guid.NewGuid().ToString("D");
        store.InsertDependency(new DurableDependencyRecord(
            id,
            consumerTaskId,
            producerTaskId,
            ResultDependencyKindCodec.ToDurableValue(parsedKind),
            ResultDependencyStatusCodec.ToDurableValue(status),
            producerResult?.ResultArtifactId));
        RefreshConsumerReadiness(consumerTaskId);
        return RuntimeResults.Success(new CreateResultDependencyResponse(id, ResultDependencyStatusCodec.ToDurableValue(status)));
    }

    public RuntimeResult<UpdateResultDependencyResponse> UpdateResultDependency(string dependencyId, string kind, string status)
    {
        var current = store.GetDependency(dependencyId);
        if (current is null)
        {
            return RuntimeResults.Fail<UpdateResultDependencyResponse>(RuntimeError.NotFound, "dependency");
        }

        if (!ResultDependencyKindCodec.TryParse(kind, out _) || !ResultDependencyStatusCodec.TryParse(status, out _))
        {
            return RuntimeResults.Fail<UpdateResultDependencyResponse>(RuntimeError.IllegalTransition, "invalid-edge");
        }

        store.UpdateDependencyRecord(dependencyId, kind, status, current.ResultArtifactId);
        RefreshConsumerReadiness(current.ConsumerTaskId);
        return RuntimeResults.Success(new UpdateResultDependencyResponse(dependencyId, kind, status));
    }

    public RuntimeResult<ProposeResultDependencyChangeResponse> ProposeResultDependencyChange(
        string dependencyId,
        string proposedKind,
        string reason,
        CallerPrincipal? principal)
    {
        _ = reason;
        if (principal is { Kind: not PrincipalKind.AgentRun })
        {
            return RuntimeResults.Fail<ProposeResultDependencyChangeResponse>(RuntimeError.OversightDenied, "agent-session-required");
        }

        var current = store.GetDependency(dependencyId);
        if (current is null)
        {
            return RuntimeResults.Fail<ProposeResultDependencyChangeResponse>(RuntimeError.NotFound, "dependency");
        }

        if (!ResultDependencyKindCodec.TryParse(proposedKind, out _))
        {
            return RuntimeResults.Fail<ProposeResultDependencyChangeResponse>(RuntimeError.IllegalTransition, "invalid-kind");
        }

        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        store.InsertApproval(new DurableApprovalRecord(
            "depprop:" + Guid.NewGuid().ToString("N"),
            store.GetTask(current.ConsumerTaskId)?.RunId ?? "unknown-run",
            current.ConsumerTaskId,
            DependencyProposalKind,
            ApprovalStatusCodec.ToDurableValue(ApprovalStatus.Pending),
            CanonicalJson.Sha256Hex(dependencyId + "\n" + proposedKind + "\n" + reason),
            null,
            null,
            now));
        return RuntimeResults.Success(new ProposeResultDependencyChangeResponse(true, current.DependencyKind));
    }

    public RuntimeResult<RefreshResultDependencyStatusResponse> RefreshResultDependencyStatus(string? producerTaskId, string? consumerTaskId)
    {
        var snapshot = store.LoadSnapshot();
        var updated = 0;
        foreach (var dependency in snapshot.Dependencies)
        {
            if ((!string.IsNullOrWhiteSpace(producerTaskId) &&
                 !StringComparer.Ordinal.Equals(dependency.ProducerTaskId, producerTaskId)) ||
                (!string.IsNullOrWhiteSpace(consumerTaskId) &&
                 !StringComparer.Ordinal.Equals(dependency.ConsumerTaskId, consumerTaskId)))
            {
                continue;
            }

            RecomputeOne(dependency);
            updated++;
            RefreshConsumerReadiness(dependency.ConsumerTaskId);
        }

        return RuntimeResults.Success(new RefreshResultDependencyStatusResponse(updated));
    }

    public RuntimeResult<GetEffectiveOversightResponse> GetEffectiveOversight(string? projectId, string? storylineId, string? taskId)
    {
        var policy = Resolve(projectId, storylineId, taskId);
        return RuntimeResults.Success(new GetEffectiveOversightResponse(
            NarrativeDecisionAuthorityCodec.ToDurableValue(policy.NarrativeAuthority),
            RuntimePermissionModeDurableCodec.ToDurableValue(policy.RuntimePermission),
            OversightScopeKindCodec.ToDurableValue(policy.WinningScope),
            policy.WinningScopeId,
            policy.Active));
    }

    public RuntimeResult<SetOversightOverrideResponse> SetOversightOverride(
        SetOversightOverrideRequest request,
        CallerPrincipal? principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (principal is not { Kind: PrincipalKind.UserInteractive })
        {
            return RuntimeResults.Fail<SetOversightOverrideResponse>(RuntimeError.OversightDenied, "user-interactive-required");
        }

        if (!OversightScopeKindCodec.TryParse(request.ScopeKind, out var scope) ||
            scope == OversightScopeKind.Application ||
            !NarrativeDecisionAuthorityCodec.TryParse(request.NarrativeAuthority, out var narrative) ||
            !RuntimePermissionModeDurableCodec.TryParse(request.RuntimePermissionMode, out var permission))
        {
            return RuntimeResults.Fail<SetOversightOverrideResponse>(RuntimeError.IllegalTransition, "invalid-oversight");
        }

        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        foreach (var existing in store.ListOversightOverrides())
        {
            if (existing.CreatedAtMs >= now)
            {
                now = existing.CreatedAtMs + 1;
            }
        }

        var id = Guid.NewGuid().ToString("D");
        var checkpoint = request.EffectiveAfterCheckpointId;
        if (string.IsNullOrWhiteSpace(checkpoint) && HasInFlightExecution())
        {
            checkpoint = OversightActivation.PendingBindToken(id);
        }

        var record = new OversightOverrideRecord(
            id,
            scope,
            request.ScopeId,
            narrative,
            permission,
            checkpoint,
            principal.TrustedInstanceId,
            now);
        store.InsertOversightOverride(record);
        var checkpoints = new HashSet<string>(
            store.LoadSnapshot().Checkpoints.Select(item => item.CheckpointId),
            StringComparer.Ordinal);
        return RuntimeResults.Success(new SetOversightOverrideResponse(
            id,
            OversightActivation.IsActive(record, checkpoints)));
    }

    public RuntimeResult<ListPendingApprovalsResponse> ListPendingApprovals(string? runId)
    {
        var items = store.ListApprovals(runId)
            .Where(item => StringComparer.Ordinal.Equals(item.Status, ApprovalStatusCodec.ToDurableValue(ApprovalStatus.Pending)))
            .Select(item => new PendingApprovalDto(
                item.ApprovalId,
                item.RunId,
                item.TaskId,
                item.ApprovalKind,
                item.Status,
                item.PayloadDigest))
            .ToArray();
        return RuntimeResults.Success(new ListPendingApprovalsResponse(items));
    }

    public RuntimeResult<RuntimeGrillPauseOutcome> PauseRuntimeGrill(
        string runId,
        string? taskId,
        RuntimeGrillPauseReason reason,
        RuntimeGrillQuestionV1 question,
        string baselineDigest)
    {
        var approvalId = RuntimeGrillPolicy.StableApprovalId(runId, taskId, baselineDigest);
        var existing = store.GetApproval(approvalId);
        if (existing is not null)
        {
            return RuntimeResults.Success(new RuntimeGrillPauseOutcome(existing.ApprovalId, existing.Status));
        }

        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var oversight = Resolve(null, null, taskId);
        var request = new RuntimeGrillDecisionRequestV1(
            RuntimeGrillDecisionRequestV1.CurrentSchemaVersion,
            approvalId,
            runId,
            taskId,
            reason,
            question,
            oversight.NarrativeAuthority,
            baselineDigest,
            null);
        var payload = RuntimeGrillPolicy.WriteCanonical(request);
        var checkpointPayload = CanonicalJson.WriteCheckpoint(CheckpointV1.Create(
            "runtime-grill",
            baselineDigest,
            payload,
            "{}",
            "runtime_grill",
            [],
            [],
            [approvalId],
            [],
            [],
            [],
            null,
            null,
            null,
            null));
        var checkpoint = scheduler.PersistCheckpoint(runId, taskId, CheckpointV1.CurrentSchemaVersion, checkpointPayload, "{}");
        if (!checkpoint.Succeeded || checkpoint.Value is null)
        {
            return RuntimeResults.Fail<RuntimeGrillPauseOutcome>(
                checkpoint.Failure?.Code ?? RuntimeError.CheckpointUnsupported,
                checkpoint.Failure?.Detail);
        }

        store.InsertApproval(new DurableApprovalRecord(
            approvalId,
            runId,
            taskId,
            ApprovalKindCodec.RuntimeGrill,
            ApprovalStatusCodec.ToDurableValue(ApprovalStatus.Pending),
            RuntimeGrillPolicy.Digest(request with { CheckpointId = checkpoint.Value }),
            null,
            null,
            now));
        if (store.GetRun(runId) is { } run &&
            RunStatusCodec.TryParse(run.Status, out var runStatus) &&
            RuntimeLifecycle.IsLegal(runStatus, RunStatus.Paused))
        {
            store.UpdateRunStatus(runId, RunStatusCodec.ToDurableValue(RunStatus.Paused), now);
        }

        if (!string.IsNullOrWhiteSpace(taskId) &&
            store.GetTask(taskId) is { } task &&
            TaskStatusCodec.TryParse(task.Status, out var taskStatus) &&
            RuntimeLifecycle.IsLegal(taskStatus, RuntimeTaskStatus.Paused))
        {
            store.UpdateTaskStatus(taskId, TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Paused), now);
        }

        return RuntimeResults.Success(new RuntimeGrillPauseOutcome(approvalId, "pending"));
    }

    public RuntimeResult<RuntimeGrillResolveOutcome> ResolveRuntimeGrill(
        ResolveRuntimeGrillRequest request,
        CallerPrincipal? principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        var approval = store.GetApproval(request.ApprovalId);
        if (approval is null)
        {
            return RuntimeResults.Fail<RuntimeGrillResolveOutcome>(RuntimeError.NotFound, "approval");
        }

        if (!StringComparer.Ordinal.Equals(approval.ApprovalKind, ApprovalKindCodec.RuntimeGrill))
        {
            return RuntimeResults.Fail<RuntimeGrillResolveOutcome>(RuntimeError.IllegalTransition, "not-runtime-grill");
        }

        if (!ApprovalStatusCodec.TryParse(approval.Status, out var currentStatus))
        {
            return RuntimeResults.Fail<RuntimeGrillResolveOutcome>(RuntimeError.IllegalTransition, "corrupt-approval");
        }

        if (currentStatus is ApprovalStatus.Resolved or ApprovalStatus.Denied)
        {
            return RuntimeResults.Fail<RuntimeGrillResolveOutcome>(RuntimeError.GrillAlreadyResolved, "stale_already_resolved");
        }

        if (faults.Fault == SchedulerFaultPoint.GrillResolutionRace &&
            store.GetApproval(request.ApprovalId) is { } raced &&
            ApprovalStatusCodec.TryParse(raced.Status, out var racedStatus) &&
            racedStatus is ApprovalStatus.Resolved or ApprovalStatus.Denied)
        {
            return RuntimeResults.Fail<RuntimeGrillResolveOutcome>(RuntimeError.GrillAlreadyResolved, "stale_already_resolved");
        }

        var oversight = Resolve(principal?.ProjectScope?.ProjectId.ToString("D"), null, approval.TaskId);
        var reason = ParseGrillReason(request.Option) ?? RuntimeGrillPauseReason.NewCreativeDecisionRequired;
        var agent = principal is { Kind: PrincipalKind.AgentRun };
        if (agent && !RuntimeGrillPolicy.AgentMayResolve(
                oversight,
                reason,
                insideApprovedPlan: !StringComparer.Ordinal.Equals(request.Option, "expand-scope"),
                taskScopeUnchanged: !StringComparer.Ordinal.Equals(request.Option, "expand-scope"),
                capabilityAllowed: true,
                inputsFresh: true))
        {
            return RuntimeResults.Fail<RuntimeGrillResolveOutcome>(RuntimeError.GrillAuthorRequired, "author-required");
        }

        if (!agent && principal is not { Kind: PrincipalKind.UserInteractive })
        {
            return RuntimeResults.Fail<RuntimeGrillResolveOutcome>(RuntimeError.GrillAuthorRequired, "author-required");
        }

        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var winner = RuntimeGrillPolicy.Compete(currentStatus, ApprovalStatus.Resolved);
        if (winner == ApprovalStatus.StaleAlreadyResolved)
        {
            return RuntimeResults.Fail<RuntimeGrillResolveOutcome>(RuntimeError.GrillAlreadyResolved, "stale_already_resolved");
        }

        store.UpdateApproval(approval with
        {
            Status = ApprovalStatusCodec.ToDurableValue(ApprovalStatus.Resolved),
            DecidedBy = principal?.ToString(),
            DecidedAtMs = now
        });
        var freshness = scheduler.ClassifyResume(
            approval.RunId,
            new FreshnessInputs(
                null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                null,
                null,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                null,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                false,
                reason == RuntimeGrillPauseReason.PlanAssumptionsInvalid,
                false,
                false));
        var mapped = freshness.Succeeded && freshness.Value is not null
            ? RuntimeGrillPolicy.MapResume(reason, freshness.Value.Kind)
            : RuntimeGrillResolutionKind.PlanBlocked;
        if (mapped == RuntimeGrillResolutionKind.Continue &&
            store.GetRun(approval.RunId) is { } run &&
            RunStatusCodec.TryParse(run.Status, out var paused) &&
            paused == RunStatus.Paused)
        {
            store.UpdateRunStatus(approval.RunId, RunStatusCodec.ToDurableValue(RunStatus.Running), now);
        }

        if (mapped == RuntimeGrillResolutionKind.Continue &&
            !string.IsNullOrWhiteSpace(approval.TaskId) &&
            store.GetTask(approval.TaskId) is { } task &&
            TaskStatusCodec.TryParse(task.Status, out var taskStatus) &&
            taskStatus == RuntimeTaskStatus.Paused)
        {
            store.UpdateTaskStatus(approval.TaskId, TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Running), now);
        }

        return RuntimeResults.Success(new RuntimeGrillResolveOutcome(
            "resolved",
            request.Resolution,
            mapped.ToString()));
    }

    public RuntimeResult<ListSpecialistsResponse> ListSpecialists(string? scopeKind)
    {
        var items = AllSpecialists()
            .Where(item => string.IsNullOrWhiteSpace(scopeKind) ||
                           StringComparer.Ordinal.Equals(item.ScopeKind, scopeKind))
            .Select(item =>
            {
                var parsed = TryParseProfile(item.DefinitionJson);
                return new SpecialistSummaryDto(
                    item.SpecialistProfileId,
                    item.ScopeKind,
                    item.Name,
                    parsed?.DisplayName ?? item.Name,
                    item.Version,
                    item.Enabled);
            })
            .ToArray();
        return RuntimeResults.Success(new ListSpecialistsResponse(items));
    }

    public RuntimeResult<GetSpecialistResponse> GetSpecialist(string profileId, string? scopeKind)
    {
        var match = AllSpecialists().FirstOrDefault(item =>
            StringComparer.Ordinal.Equals(item.SpecialistProfileId, profileId) &&
            (string.IsNullOrWhiteSpace(scopeKind) || StringComparer.Ordinal.Equals(item.ScopeKind, scopeKind)));
        if (match is null)
        {
            return RuntimeResults.Fail<GetSpecialistResponse>(RuntimeError.NotFound, "specialist");
        }

        return RuntimeResults.Success(new GetSpecialistResponse(
            match.SpecialistProfileId,
            match.ScopeKind,
            match.DefinitionJson,
            match.Enabled));
    }

    public RuntimeResult<SpecialistMutationOutcome> CreateSpecialist(string scopeKind, string definitionJson, CallerPrincipal? principal)
    {
        if (principal is not { Kind: PrincipalKind.UserInteractive })
        {
            return RuntimeResults.Fail<SpecialistMutationOutcome>(RuntimeError.OversightDenied, "user-interactive-required");
        }

        if (SpecialistScopeKindCodec.IsPersistentForbidden(scopeKind) ||
            !SpecialistScopeKindCodec.TryParse(scopeKind, out var scope) ||
            scope == SpecialistScopeKind.BuiltIn)
        {
            return RuntimeResults.Fail<SpecialistMutationOutcome>(RuntimeError.SpecialistImmutable, "persistent-scope");
        }

        SpecialistProfileDefinitionV1 profile;
        try
        {
            profile = SpecialistProfileCanonicalJson.Parse(definitionJson) with { ScopeKind = scope };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return RuntimeResults.Fail<SpecialistMutationOutcome>(RuntimeError.SpecialistInvalid, exception.Message);
        }

        var validation = SpecialistProfileValidator.Validate(profile);
        if (!validation.IsValid)
        {
            return RuntimeResults.Success(new SpecialistMutationOutcome(
                profile.ProfileId,
                validation.Errors.Select(item => item.Code + ":" + item.Message).ToArray()));
        }

        PersistProfile(profile, scope, principal.ProjectScope?.ProjectId.ToString("D"), null);
        return RuntimeResults.Success(new SpecialistMutationOutcome(profile.ProfileId, []));
    }

    public RuntimeResult<SpecialistMutationOutcome> UpdateSpecialist(
        string profileId,
        string scopeKind,
        string definitionJson,
        CallerPrincipal? principal)
    {
        if (principal is not { Kind: PrincipalKind.UserInteractive })
        {
            return RuntimeResults.Fail<SpecialistMutationOutcome>(RuntimeError.OversightDenied, "user-interactive-required");
        }

        if (!SpecialistScopeKindCodec.TryParse(scopeKind, out var scope) || scope == SpecialistScopeKind.BuiltIn)
        {
            return RuntimeResults.Fail<SpecialistMutationOutcome>(RuntimeError.SpecialistImmutable, "built-in");
        }

        var result = CreateSpecialist(scopeKind, definitionJson, principal);
        _ = profileId;
        return result;
    }

    public RuntimeResult<DuplicateSpecialistResponse> DuplicateSpecialist(
        string profileId,
        string sourceScopeKind,
        string targetScopeKind,
        CallerPrincipal? principal)
    {
        if (principal is not { Kind: PrincipalKind.UserInteractive })
        {
            return RuntimeResults.Fail<DuplicateSpecialistResponse>(RuntimeError.OversightDenied, "user-interactive-required");
        }

        var source = GetSpecialist(profileId, sourceScopeKind);
        if (!source.Succeeded || source.Value is null)
        {
            return RuntimeResults.Fail<DuplicateSpecialistResponse>(RuntimeError.NotFound, "specialist");
        }

        if (!SpecialistScopeKindCodec.TryParse(targetScopeKind, out var target) || target == SpecialistScopeKind.BuiltIn)
        {
            return RuntimeResults.Fail<DuplicateSpecialistResponse>(RuntimeError.SpecialistImmutable, "target-scope");
        }

        var parsed = SpecialistProfileCanonicalJson.Parse(source.Value.DefinitionJson);
        var digest = SpecialistProfileCanonicalJson.Digest(parsed);
        var copy = parsed with
        {
            ProfileId = parsed.ProfileId + ".copy." + Guid.NewGuid().ToString("N")[..8],
            Name = parsed.Name + "-copy",
            ScopeKind = target,
            BaseProfileId = parsed.ProfileId,
            BaseDefinitionDigest = digest,
            OverrideProvenance = "duplicate:" + parsed.ProfileId
        };
        PersistProfile(copy, target, principal.ProjectScope?.ProjectId.ToString("D"), digest);
        return RuntimeResults.Success(new DuplicateSpecialistResponse(copy.ProfileId, digest));
    }

    public RuntimeResult<ValidateSpecialistResponse> ValidateSpecialist(string definitionJson)
    {
        _ = store;
        try
        {
            var profile = SpecialistProfileCanonicalJson.Parse(definitionJson);
            var validation = SpecialistProfileValidator.Validate(profile);
            return RuntimeResults.Success(new ValidateSpecialistResponse(
                validation.IsValid,
                validation.Errors.Select(item => item.Code + ":" + item.Message).ToArray()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return RuntimeResults.Success(new ValidateSpecialistResponse(false, [exception.Message]));
        }
    }

    public RuntimeResult<CreateSpecialistTestRunResponse> CreateSpecialistTestRun(
        string profileId,
        string scopeKind,
        CallerPrincipal? principal)
    {
        if (principal is not { Kind: PrincipalKind.UserInteractive })
        {
            return RuntimeResults.Fail<CreateSpecialistTestRunResponse>(RuntimeError.OversightDenied, "user-interactive-required");
        }

        var specialist = GetSpecialist(profileId, scopeKind);
        if (!specialist.Succeeded || specialist.Value is null)
        {
            return RuntimeResults.Fail<CreateSpecialistTestRunResponse>(RuntimeError.NotFound, "specialist");
        }

        _ = specialist;
        return RuntimeResults.Success(new CreateSpecialistTestRunResponse("provider_unavailable", null));
    }

    public RuntimeResult<SpecialistRouteDecision> RouteSpecialist(string workflowStage)
    {
        var profiles = AllSpecialists()
            .Select(item => TryParseProfile(item.DefinitionJson))
            .Where(item => item is not null)
            .Cast<SpecialistProfileDefinitionV1>()
            .ToArray();
        return RuntimeResults.Success(SpecialistRouter.RouteDeterministic(workflowStage, profiles));
    }

    public RuntimeResult<SpecialistTaskPacketV1> BuildIsolatedTaskPacket(
        string runId,
        string taskId,
        string? profileId,
        string? temporaryInstructions)
    {
        var deps = store.DependenciesForConsumer(taskId);
        var required = new List<string>();
        var warnings = new List<string>();
        foreach (var dependency in deps)
        {
            var evaluation = ResultDependencyPolicy.Evaluate(dependency);
            if (ResultDependencyKindCodec.IsRequired(dependency.DependencyKind) &&
                !string.IsNullOrWhiteSpace(dependency.ResultArtifactId))
            {
                required.Add(dependency.ResultArtifactId);
            }

            if (evaluation.HasWarning)
            {
                warnings.Add(dependency.DependencyId);
            }
        }

        var contract = store.GetTask(taskId)?.CompletionContractJson ?? "{}";
        return RuntimeResults.Success(SpecialistTaskPacketV1.Isolated(
            runId,
            taskId,
            profileId,
            temporaryInstructions,
            contract,
            required,
            warnings,
            null));
    }

    public RuntimeResult<SpawnChildRunResponseModel> SpawnTemporarySpecialist(
        string parentRunId,
        string parentTaskId,
        string role,
        CallerPrincipal? principal)
    {
        var spawned = scheduler.SpawnChildRun(parentRunId, parentTaskId, role, null, principal);
        if (!spawned.Succeeded || spawned.Value?.ChildRunId is null)
        {
            return spawned;
        }

        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        store.InsertBackgroundTask(new DurableBackgroundTaskRecord(
            Guid.NewGuid().ToString("D"),
            parentRunId,
            parentTaskId,
            BackgroundExecutionRefCodec.WriteKindColumn(new BackgroundExecutionRef(
                BackgroundTaskKind.SubAgentRun,
                spawned.Value.ChildRunId,
                null,
                null,
                null)),
            BackgroundTaskStatusCodec.ToDurableValue(
                StringComparer.Ordinal.Equals(spawned.Value.Outcome, "queued")
                    ? BackgroundTaskStatus.Queued
                    : BackgroundTaskStatus.Running),
            null,
            now,
            null));
        return spawned;
    }

    public RuntimeResult<ListBackgroundTasksResponse> ListBackgroundTasks(string? ownerRunId)
    {
        var items = store.ListBackgroundTasks(ownerRunId).Select(ToDto).ToArray();
        return RuntimeResults.Success(new ListBackgroundTasksResponse(items));
    }

    public RuntimeResult<GetBackgroundTaskResponse> GetBackgroundTask(string backgroundTaskId)
    {
        var record = store.GetBackgroundTask(backgroundTaskId);
        if (record is null)
        {
            return RuntimeResults.Fail<GetBackgroundTaskResponse>(RuntimeError.NotFound, "background-task");
        }

        return RuntimeResults.Success(new GetBackgroundTaskResponse(ToDto(record)));
    }

    public RuntimeResult<StopBackgroundTaskResponse> StopBackgroundTask(string backgroundTaskId)
    {
        var record = store.GetBackgroundTask(backgroundTaskId);
        if (record is null)
        {
            return RuntimeResults.Fail<StopBackgroundTaskResponse>(RuntimeError.NotFound, "background-task");
        }

        if (!BackgroundTaskStatusCodec.TryParse(record.Status, out var status) ||
            !BackgroundTaskLifecycle.IsLegal(status, BackgroundTaskStatus.Cancelled))
        {
            return RuntimeResults.Fail<StopBackgroundTaskResponse>(RuntimeError.BackgroundIllegalTransition, record.Status);
        }

        if (faults.Fault == SchedulerFaultPoint.BackgroundStopRaceComplete &&
            StringComparer.Ordinal.Equals(record.Status, BackgroundTaskStatusCodec.ToDurableValue(BackgroundTaskStatus.Completed)))
        {
            return RuntimeResults.Fail<StopBackgroundTaskResponse>(RuntimeError.BackgroundIllegalTransition, "completed");
        }

        var execution = BackgroundExecutionRefCodec.ParseKindColumn(record.KindJson);
        if (!string.IsNullOrWhiteSpace(execution.RunId))
        {
            scheduler.CancelScope("run", execution.RunId);
        }
        else
        {
            scheduler.CancelScope("run", record.OwnerRunId);
        }

        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        store.UpdateBackgroundTask(record with
        {
            Status = BackgroundTaskStatusCodec.ToDurableValue(BackgroundTaskStatus.Cancelled),
            CompletedAtMs = now
        });
        return RuntimeResults.Success(new StopBackgroundTaskResponse(true, "cancelled"));
    }

    public RuntimeResult<DurableBackgroundTaskRecord> CreateBackgroundTask(DurableBackgroundTaskRecord record)
    {
        store.InsertBackgroundTask(record);
        return RuntimeResults.Success(record);
    }

    public IReadOnlyList<BackgroundRecoveryClassification> ClassifyBackgroundRecovery()
    {
        var snapshot = store.LoadSnapshot();
        var cancelledOwners = CancelledOwnerIds(snapshot);
        return store.ListBackgroundTasks(null)
            .Select(item => ClassifyOne(item, snapshot, cancelledOwners))
            .ToArray();
    }

    public int RecoverBackgroundTasks()
    {
        var snapshot = store.LoadSnapshot();
        var cancelledOwners = CancelledOwnerIds(snapshot);
        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var updated = 0;
        foreach (var record in store.ListBackgroundTasks(null))
        {
            if (!BackgroundTaskStatusCodec.TryParse(record.Status, out var status) ||
                status is BackgroundTaskStatus.Completed or BackgroundTaskStatus.Cancelled or BackgroundTaskStatus.Failed)
            {
                continue;
            }

            var classification = ClassifyOne(record, snapshot, cancelledOwners);
            var next = classification switch
            {
                BackgroundRecoveryClassification.OwnerCancelled => BackgroundTaskStatus.Cancelled,
                BackgroundRecoveryClassification.UnknownSideEffect => BackgroundTaskStatus.Interrupted,
                BackgroundRecoveryClassification.WorkerOrToolGone => BackgroundTaskStatus.Interrupted,
                BackgroundRecoveryClassification.ResumableInterrupted => BackgroundTaskStatus.Interrupted,
                BackgroundRecoveryClassification.CheckpointAvailable => BackgroundTaskStatus.Interrupted,
                BackgroundRecoveryClassification.StillQueued => BackgroundTaskStatus.Queued,
                _ => status
            };
            if (next == status || !BackgroundTaskLifecycle.IsLegal(status, next))
            {
                continue;
            }

            store.UpdateBackgroundTask(record with
            {
                Status = BackgroundTaskStatusCodec.ToDurableValue(next),
                CompletedAtMs = next is BackgroundTaskStatus.Cancelled or BackgroundTaskStatus.Failed ? now : record.CompletedAtMs
            });
            updated++;
        }

        return updated;
    }

    private static HashSet<string> CancelledOwnerIds(SchedulerSnapshot snapshot) =>
        snapshot.Runs
            .Where(item => StringComparer.Ordinal.Equals(item.Status, RunStatusCodec.ToDurableValue(RunStatus.Cancelled)))
            .Select(item => item.RunId)
            .ToHashSet(StringComparer.Ordinal);

    private static BackgroundRecoveryClassification ClassifyOne(
        DurableBackgroundTaskRecord item,
        SchedulerSnapshot snapshot,
        HashSet<string> cancelledOwners)
    {
        var execution = BackgroundExecutionRefCodec.ParseKindColumn(item.KindJson);
        var unknown = UnknownSideEffectPolicy.BlocksAutomaticRetry(
            snapshot.ToolCalls,
            execution.TaskId ?? item.OwnerTaskId);
        var alive = execution.RunId is null ||
                    snapshot.Runs.Any(run =>
                        StringComparer.Ordinal.Equals(run.RunId, execution.RunId) &&
                        RunStatusCodec.TryParse(run.Status, out var status) &&
                        RuntimeLifecycle.IsActive(status));
        return BackgroundTaskLifecycle.ClassifyRestart(
            item,
            cancelledOwners.Contains(item.OwnerRunId),
            alive,
            unknown,
            !string.IsNullOrWhiteSpace(item.CheckpointId));
    }

    public void SetTaskCompletionContract(string taskId, TaskCompletionContractV1 contract) =>
        store.UpdateTaskCompletionContract(taskId, TaskCompletionContractCanonicalJson.Write(contract));

    private void PersistProfile(
        SpecialistProfileDefinitionV1 profile,
        SpecialistScopeKind scope,
        string? projectId,
        string? baseDigest)
    {
        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var json = SpecialistProfileCanonicalJson.Write(profile);
        var record = new DurableProjectSpecialistRecord(
            profile.ProfileId,
            SpecialistScopeKindCodec.ToDurableValue(scope),
            projectId,
            profile.Name,
            profile.Version,
            json,
            baseDigest ?? profile.BaseDefinitionDigest,
            profile.Enabled,
            now,
            now);
        if (scope == SpecialistScopeKind.UserLibrary)
        {
            userSpecialists.Upsert(record);
            return;
        }

        store.UpsertProjectSpecialist(record);
    }

    private DurableProjectSpecialistRecord[] AllSpecialists()
    {
        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var builtIn = builtIns.List().Select(profile => new DurableProjectSpecialistRecord(
            profile.ProfileId,
            SpecialistScopeKindCodec.ToDurableValue(SpecialistScopeKind.BuiltIn),
            null,
            profile.Name,
            profile.Version,
            SpecialistProfileCanonicalJson.Write(profile),
            null,
            profile.Enabled,
            now,
            now));
        return builtIn.Concat(userSpecialists.List()).Concat(store.ListProjectSpecialists()).ToArray();
    }

    private static SpecialistProfileDefinitionV1? TryParseProfile(string json)
    {
        try
        {
            return SpecialistProfileCanonicalJson.Parse(json);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
            return null;
        }
    }

    private void RecomputeConsumersOf(string producerTaskId, string? resultArtifactId)
    {
        foreach (var dependency in store.LoadSnapshot().Dependencies.Where(item =>
                     StringComparer.Ordinal.Equals(item.ProducerTaskId, producerTaskId)))
        {
            RecomputeOne(dependency with { ResultArtifactId = resultArtifactId ?? dependency.ResultArtifactId });
            RefreshConsumerReadiness(dependency.ConsumerTaskId);
        }
    }

    private void RecomputeOne(DurableDependencyRecord dependency)
    {
        if (!ResultDependencyKindCodec.TryParse(dependency.DependencyKind, out var kind))
        {
            kind = ResultDependencyKind.Required;
        }
        var artifact = store.GetLatestResultArtifact(dependency.ProducerTaskId);
        var freshness = artifact is null ? (ResultFreshnessState?)null : ResultArtifactCanonicalJson.FromDurable(artifact).Freshness.State;
        var status = ResultDependencyPolicy.Recompute(kind, artifact?.ResultArtifactId, freshness, artifact is not null);
        store.UpdateDependencyRecord(
            dependency.DependencyId,
            dependency.DependencyKind,
            ResultDependencyStatusCodec.ToDurableValue(status),
            artifact?.ResultArtifactId);
    }

    private void RefreshConsumerReadiness(string consumerTaskId)
    {
        var task = store.GetTask(consumerTaskId);
        if (task is null ||
            !TaskStatusCodec.TryParse(task.Status, out var status) ||
            status is RuntimeTaskStatus.Running or RuntimeTaskStatus.Completed or RuntimeTaskStatus.Cancelled
                or RuntimeTaskStatus.Paused)
        {
            return;
        }

        var ready = StructuralReadiness.IsTaskStructurallyReady(consumerTaskId, store.LoadSnapshot().Dependencies);
        var next = ready ? RuntimeTaskStatus.Ready : RuntimeTaskStatus.Blocked;
        if (status != next)
        {
            store.UpdateTaskStatus(consumerTaskId, TaskStatusCodec.ToDurableValue(next), clock.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    private void CompleteAttempt(string taskId, long now)
    {
        var attempt = store.FindActiveAttempt(taskId);
        if (attempt is null)
        {
            return;
        }

        if (AttemptStatusCodec.TryParse(attempt.Status, out var status) && status == AttemptStatus.Starting)
        {
            store.UpdateAttemptStatus(attempt.AttemptId, AttemptStatusCodec.ToDurableValue(AttemptStatus.Running), null);
        }

        store.UpdateAttemptStatus(attempt.AttemptId, AttemptStatusCodec.ToDurableValue(AttemptStatus.Completed), now);
    }

    private HashSet<string> CompletedTaskIds() =>
        store.LoadSnapshot().Tasks
            .Where(item => StringComparer.Ordinal.Equals(item.Status, TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Completed)))
            .Select(item => item.TaskId)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> CurrentInputRefs(TaskResultArtifactV1? artifact)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (artifact is null)
        {
            return set;
        }

        foreach (var digest in artifact.Freshness.ProducedAgainst.NarrativeObjectDigests)
        {
            set.Add(digest);
        }

        foreach (var evidenceId in artifact.EvidenceIds)
        {
            set.Add(evidenceId);
        }

        return set;
    }

    private bool HasInFlightExecution()
    {
        var snapshot = store.LoadSnapshot();
        return snapshot.Runs.Any(item =>
                   RunStatusCodec.TryParse(item.Status, out var status) && RuntimeLifecycle.IsActive(status)) ||
               snapshot.Tasks.Any(item =>
                   TaskStatusCodec.TryParse(item.Status, out var status) &&
                   status is RuntimeTaskStatus.Running or RuntimeTaskStatus.Paused);
    }

    private static RuntimeGrillPauseReason? ParseGrillReason(string? option) => option switch
    {
        "expand-scope" => RuntimeGrillPauseReason.TaskScopeExpansion,
        "invalid-plan" => RuntimeGrillPauseReason.PlanAssumptionsInvalid,
        "ambiguous" => RuntimeGrillPauseReason.PlanAuthorityAmbiguous,
        _ => null
    };

    private static BackgroundTaskDto ToDto(DurableBackgroundTaskRecord record)
    {
        var execution = BackgroundExecutionRefCodec.ParseKindColumn(record.KindJson);
        return new BackgroundTaskDto(
            record.BackgroundTaskId,
            record.OwnerRunId,
            record.OwnerTaskId,
            BackgroundTaskKindCodec.ToDurableValue(execution.Kind),
            record.Status,
            record.KindJson,
            record.CheckpointId,
            record.StartedAtMs,
            record.CompletedAtMs,
            BackgroundTaskLifecycle.DurationMs(record));
    }
}
