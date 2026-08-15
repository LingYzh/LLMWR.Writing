using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;
using RuntimeTaskStatus = LLMW.Writing.Domain.Runtime.TaskStatus;

namespace LLMW.Writing.Application.Runtime;

public sealed class Wp13RuntimeService : IEffectiveOversightSource, IDelegatedDecisionSink, IOversightCheckpointListener
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
        var context = BuildActivationContext(projectId, storylineId, taskId, runId: null);
        return OversightResolver.Resolve(
            applicationDefaults.Current,
            store.ListOversightOverrides(),
            context);
    }

    public EffectiveOversightPolicy ResolveForPrincipal(CallerPrincipal? principal, string? taskId = null, string? storylineId = null)
    {
        string? runId = null;
        string? projectId = principal?.ProjectScope?.ProjectId.ToString("D");
        if (principal is { Kind: PrincipalKind.AgentRun, RunId: { Length: > 0 } ownedRun })
        {
            runId = ownedRun;
            var run = store.GetRun(ownedRun);
            if (run is not null)
            {
                var workflow = store.GetWorkflowRun(run.WorkflowRunId);
                storylineId = workflow?.StorylineId ?? storylineId;
            }

            if (!string.IsNullOrWhiteSpace(taskId))
            {
                var owned = store.GetTask(taskId);
                if (owned is null || !StringComparer.Ordinal.Equals(owned.RunId, ownedRun))
                {
                    taskId = null;
                }
            }
            else
            {
                var running = store.LoadSnapshot().Tasks.FirstOrDefault(item =>
                    StringComparer.Ordinal.Equals(item.RunId, ownedRun) &&
                    TaskStatusCodec.TryParse(item.Status, out var status) &&
                    status is RuntimeTaskStatus.Running or RuntimeTaskStatus.Paused);
                taskId = running?.TaskId;
            }
        }

        return OversightResolver.Resolve(
            applicationDefaults.Current,
            store.ListOversightOverrides(),
            BuildActivationContext(projectId, storylineId, taskId, runId));
    }

    public void Record(DelegatedDecisionRecord record)
    {
        try
        {
            store.InsertDelegatedDecision(record);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
        }
    }

    public void OnSafeCheckpoint(string checkpointId, string runId, string? taskId, long createdAtMs)
    {
        _ = checkpointId;
        _ = createdAtMs;
        ReevaluatePendingApprovals(runId, taskId);
    }

    public RuntimeResult<SubmitResultArtifactResponse> SubmitResultArtifact(
        SubmitResultArtifactRequest request,
        CallerPrincipal? principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        var owned = AgentTaskOwnership.RequireOwnedAgentTask(store, principal, request.TaskId);
        if (!owned.Succeeded || owned.Value is null)
        {
            return RuntimeResults.Fail<SubmitResultArtifactResponse>(
                owned.Failure?.Code ?? RuntimeError.TaskOwnershipDenied,
                owned.Failure?.Detail);
        }

        var task = owned.Value;
        if (TaskStatusCodec.TryParse(task.Status, out var taskStatus) && taskStatus == RuntimeTaskStatus.Completed)
        {
            return RuntimeResults.Fail<SubmitResultArtifactResponse>(RuntimeError.ResultFrozen, "completed-task");
        }

        if (taskStatus is RuntimeTaskStatus.Ready or RuntimeTaskStatus.Blocked or RuntimeTaskStatus.Pending)
        {
            return RuntimeResults.Fail<SubmitResultArtifactResponse>(RuntimeError.IllegalCompletionLifecycle, "not-executing");
        }

        if (!ResultArtifactStatusCodec.TryParse(request.Status, out _))
        {
            return RuntimeResults.Fail<SubmitResultArtifactResponse>(RuntimeError.CompletionFailed, "invalid-status");
        }

        DurableResultArtifactRecord? durable = null;
        RuntimeFailure? failure = null;
        store.InTransaction(() =>
        {
            var current = store.GetTask(request.TaskId);
            if (current is null)
            {
                failure = new RuntimeFailure(RuntimeError.NotFound, "task");
                return;
            }

            if (TaskStatusCodec.TryParse(current.Status, out var live) && live == RuntimeTaskStatus.Completed)
            {
                failure = new RuntimeFailure(RuntimeError.ResultFrozen, "completed-task");
                return;
            }

            var attempt = store.FindActiveAttempt(request.TaskId);
            var parsed = ResultArtifactCanonicalJson.ParseColumns(
                Guid.NewGuid().ToString("D"),
                request.TaskId,
                request.Status,
                SecretRedaction.RedactObjectJson(request.ConclusionJson),
                SecretRedaction.RedactObjectJson(request.FindingsJson),
                SecretRedaction.RedactObjectJson(request.EvidenceJson),
                SecretRedaction.RedactObjectJson(request.UncertaintyJson),
                SecretRedaction.RedactObjectJson(request.DiagnosticsJson),
                SecretRedaction.RedactObjectJson(request.FreshnessJson),
                clock.UtcNow.ToUnixTimeMilliseconds());
            var stamped = StampFreshness(parsed, current, attempt, principal!);
            if (ResultArtifactCanonicalJson.ContainsTranscript(stamped) ||
                SecretRedaction.ContainsSecretMaterial(ResultArtifactCanonicalJson.Write(stamped)))
            {
                failure = new RuntimeFailure(RuntimeError.CompletionFailed, "secret-or-transcript");
                return;
            }

            durable = ResultArtifactCanonicalJson.ToDurable(stamped);
            store.InsertResultArtifact(durable);
        });

        if (failure is not null)
        {
            return RuntimeResults.Fail<SubmitResultArtifactResponse>(failure.Code, failure.Detail);
        }

        RecomputeConsumersOf(request.TaskId, durable!.ResultArtifactId);
        return RuntimeResults.Success(new SubmitResultArtifactResponse(durable.ResultArtifactId, durable.Status));
    }

    public RuntimeResult<TaskCompletionOutcome> RequestTaskCompletion(string taskId, CallerPrincipal? principal)
    {
        var owned = AgentTaskOwnership.RequireOwnedAgentTask(store, principal, taskId);
        if (!owned.Succeeded || owned.Value is null)
        {
            return RuntimeResults.Fail<TaskCompletionOutcome>(
                owned.Failure?.Code ?? RuntimeError.TaskOwnershipDenied,
                owned.Failure?.Detail);
        }

        TaskCompletionOutcome? outcome = null;
        RuntimeFailure? failure = null;
        store.InTransaction(() =>
        {
            var task = store.GetTask(taskId);
            if (task is null)
            {
                failure = new RuntimeFailure(RuntimeError.NotFound, "task");
                return;
            }

            if (TaskStatusCodec.TryParse(task.Status, out var existingStatus) &&
                existingStatus == RuntimeTaskStatus.Completed)
            {
                var existing = store.GetLatestResultArtifact(taskId);
                if (existing is null)
                {
                    failure = new RuntimeFailure(RuntimeError.CompletionFailed, "completed-without-result");
                    return;
                }

                outcome = new TaskCompletionOutcome("pass", existing.ResultArtifactId, []);
                return;
            }

            if (!TaskStatusCodec.TryParse(task.Status, out var currentStatus) ||
                !RuntimeLifecycle.IsLegal(currentStatus, RuntimeTaskStatus.Completed))
            {
                failure = new RuntimeFailure(RuntimeError.IllegalCompletionLifecycle, currentStatus.ToString());
                return;
            }

            var attempt = store.FindActiveAttempt(taskId);
            if (attempt is null ||
                !AttemptStatusCodec.TryParse(attempt.Status, out var attemptStatus) ||
                attemptStatus is not (AttemptStatus.Starting or AttemptStatus.Running) ||
                !StringComparer.Ordinal.Equals(attempt.TaskId, taskId))
            {
                failure = new RuntimeFailure(RuntimeError.IllegalCompletionLifecycle, "no-active-attempt");
                return;
            }

            var artifactRecord = store.GetLatestResultArtifact(taskId);
            var artifact = artifactRecord is null ? null : ResultArtifactCanonicalJson.FromDurable(artifactRecord);
            if (artifactRecord is null || artifact is null || artifact.Status != ResultArtifactStatus.Complete)
            {
                failure = new RuntimeFailure(RuntimeError.CompletionFailed, "result-not-complete");
                return;
            }

            var contract = TaskCompletionContractCanonicalJson.Parse(task.CompletionContractJson);
            var semantic = contract.HasSemanticCriteria
                ? semanticEvaluator.Evaluate(contract, artifact)
                : SemanticCompletionOutcome.Pass;
            var check = TaskCompletionContractChecker.Check(
                contract,
                new CompletionCheckInputs(
                    artifact.OutputKeys,
                    CompletedTaskIds(),
                    CurrentInputRefs(artifact),
                    artifact.OutputKeys,
                    artifact.BlockingDiagnosticCount,
                    store.DependenciesForConsumer(taskId),
                    true,
                    semantic));
            if (check.Outcome == CompletionCheckOutcome.SemanticReviewRequired)
            {
                failure = new RuntimeFailure(RuntimeError.SemanticReviewRequired, "semantic-review-required");
                return;
            }

            if (check.Outcome is not CompletionCheckOutcome.Pass)
            {
                failure = new RuntimeFailure(RuntimeError.CompletionFailed, string.Join(',', check.Failures));
                return;
            }

            var now = clock.UtcNow.ToUnixTimeMilliseconds();
            if (faults.Fault == SchedulerFaultPoint.AfterTaskBeforeResultPersist)
            {
                store.UpdateTaskStatus(taskId, TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Completed), now);
                throw new SchedulerFaultInjectedException(SchedulerFaultPoint.AfterTaskBeforeResultPersist);
            }

            if (faults.Fault == SchedulerFaultPoint.AfterResultBeforeTaskComplete)
            {
                throw new SchedulerFaultInjectedException(SchedulerFaultPoint.AfterResultBeforeTaskComplete);
            }

            var frozenResultId = artifactRecord.ResultArtifactId;
            CompleteAttempt(taskId, now);
            store.UpdateTaskStatus(taskId, TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Completed), now);
            RecomputeConsumersOf(taskId, frozenResultId);
            outcome = new TaskCompletionOutcome("pass", frozenResultId, []);
        });

        if (failure is not null)
        {
            return RuntimeResults.Fail<TaskCompletionOutcome>(failure.Code, failure.Detail);
        }

        return RuntimeResults.Success(outcome!);
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
        var edges = new List<TaskHandoffEdgeDto>();
        foreach (var dependency in deps)
        {
            var evaluation = ResultDependencyPolicy.Evaluate(dependency);
            if (evaluation.HasWarning)
            {
                warnings.Add(dependency.DependencyId + ":" + ResultDependencyStatusCodec.ToDurableValue(evaluation.EffectiveStatus));
            }

            var freshnessState = "missing";
            if (!string.IsNullOrWhiteSpace(dependency.ResultArtifactId))
            {
                resultIds.Add(dependency.ResultArtifactId);
                var artifact = store.GetResultArtifact(dependency.ResultArtifactId);
                if (artifact is not null)
                {
                    freshnessState = ResultFreshnessStateCodec.ToDurableValue(
                        ResultArtifactCanonicalJson.FromDurable(artifact).Freshness.State);
                    if (includeEvidence)
                    {
                        evidenceIds.AddRange(ResultArtifactCanonicalJson.FromDurable(artifact).EvidenceIds);
                    }
                }

                edges.Add(new TaskHandoffEdgeDto(
                    dependency.ResultArtifactId,
                    dependency.DependencyKind,
                    ResultDependencyStatusCodec.ToDurableValue(evaluation.EffectiveStatus),
                    freshnessState,
                    evaluation.BlocksDispatch,
                    evaluation.BlocksCompletion,
                    evaluation.HasWarning));
            }
        }

        return RuntimeResults.Success(new GetTaskHandoffResponse(
            consumerTaskId,
            resultIds.Distinct(StringComparer.Ordinal).ToArray(),
            evidenceIds.Distinct(StringComparer.Ordinal).ToArray(),
            edges.ToArray(),
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

    public RuntimeResult<UpdateResultDependencyResponse> UpdateResultDependency(string dependencyId, string kind)
    {
        var current = store.GetDependency(dependencyId);
        if (current is null)
        {
            return RuntimeResults.Fail<UpdateResultDependencyResponse>(RuntimeError.NotFound, "dependency");
        }

        if (!ResultDependencyKindCodec.TryParse(kind, out _))
        {
            return RuntimeResults.Fail<UpdateResultDependencyResponse>(RuntimeError.IllegalTransition, "invalid-kind");
        }

        store.UpdateDependencyRecord(dependencyId, kind, current.Status, current.ResultArtifactId);
        RecomputeOne(store.GetDependency(dependencyId)!);
        RefreshConsumerReadiness(current.ConsumerTaskId);
        var updated = store.GetDependency(dependencyId)!;
        return RuntimeResults.Success(new UpdateResultDependencyResponse(
            dependencyId,
            updated.DependencyKind,
            updated.Status));
    }

    public RuntimeResult<ProposeResultDependencyChangeResponse> ProposeResultDependencyChange(
        string dependencyId,
        string proposedKind,
        string reason,
        CallerPrincipal? principal)
    {
        _ = reason;
        var current = store.GetDependency(dependencyId);
        if (current is null)
        {
            return RuntimeResults.Fail<ProposeResultDependencyChangeResponse>(RuntimeError.NotFound, "dependency");
        }

        var owned = AgentTaskOwnership.RequireOwnedAgentTask(store, principal, current.ConsumerTaskId);
        if (!owned.Succeeded)
        {
            return RuntimeResults.Fail<ProposeResultDependencyChangeResponse>(
                owned.Failure?.Code ?? RuntimeError.TaskOwnershipDenied,
                owned.Failure?.Detail);
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
        _ = request.EffectiveAfterCheckpointId;
        string? checkpoint = null;
        if (HasInFlightExecution(scope, request.ScopeId))
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
        var active = OversightActivation.IsActiveForExecution(
            record,
            BuildActivationContext(
                scope == OversightScopeKind.Project ? request.ScopeId : principal.ProjectScope?.ProjectId.ToString("D"),
                scope == OversightScopeKind.Storyline ? request.ScopeId : null,
                scope == OversightScopeKind.Task ? request.ScopeId : null,
                runId: null));
        if (active)
        {
            ReevaluatePendingApprovals(null, scope == OversightScopeKind.Task ? request.ScopeId : null);
        }

        return RuntimeResults.Success(new SetOversightOverrideResponse(id, active));
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
        var approvalId = RuntimeGrillPolicy.StableApprovalId(runId, taskId, baselineDigest, reason, question);
        var existing = store.GetApproval(approvalId);
        if (existing is not null)
        {
            return RuntimeResults.Success(new RuntimeGrillPauseOutcome(existing.ApprovalId, existing.Status));
        }

        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var runRecord = store.GetRun(runId);
        var workflow = runRecord is null ? null : store.GetWorkflowRun(runRecord.WorkflowRunId);
        var oversight = OversightResolver.Resolve(
            applicationDefaults.Current,
            store.ListOversightOverrides(),
            BuildActivationContext(null, workflow?.StorylineId, taskId, runId));
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

        var persisted = LoadPersistedGrillRequest(approval);
        if (persisted is null)
        {
            return RuntimeResults.Fail<RuntimeGrillResolveOutcome>(RuntimeError.CheckpointCorrupt, "grill-request-missing");
        }

        if (principal is { Kind: PrincipalKind.AgentRun })
        {
            if (string.IsNullOrWhiteSpace(principal.RunId) ||
                !StringComparer.Ordinal.Equals(principal.RunId, persisted.RunId))
            {
                return RuntimeResults.Fail<RuntimeGrillResolveOutcome>(RuntimeError.GrillOwnershipDenied, "cross-run");
            }
        }
        else if (principal is not { Kind: PrincipalKind.UserInteractive })
        {
            return RuntimeResults.Fail<RuntimeGrillResolveOutcome>(RuntimeError.GrillAuthorRequired, "author-required");
        }

        var option = request.Option ?? request.Resolution;
        if (string.IsNullOrWhiteSpace(option) ||
            !persisted.Question.Options.Contains(option, StringComparer.Ordinal))
        {
            return RuntimeResults.Fail<RuntimeGrillResolveOutcome>(RuntimeError.GrillOptionRejected, "unknown-option");
        }

        var oversight = ResolveForPrincipal(principal, persisted.TaskId);
        var capability = scheduler.ProbeCapability(principal, Capability.AgentSpawn);
        var unknown = UnknownSideEffectPolicy.BlocksAutomaticRetry(
            store.ToolCallsFor(persisted.RunId, persisted.TaskId),
            persisted.TaskId);
        var inputsFresh = !unknown;
        if (principal is { Kind: PrincipalKind.AgentRun } &&
            !RuntimeGrillPolicy.AgentMayResolve(
                oversight,
                persisted.Reason,
                insideApprovedPlan: persisted.Reason is not RuntimeGrillPauseReason.TaskScopeExpansion,
                taskScopeUnchanged: persisted.Reason is not RuntimeGrillPauseReason.TaskScopeExpansion,
                capabilityAllowed: capability,
                inputsFresh: inputsFresh))
        {
            return RuntimeResults.Fail<RuntimeGrillResolveOutcome>(RuntimeError.GrillAuthorRequired, "author-required");
        }

        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var next = approval with
        {
            Status = ApprovalStatusCodec.ToDurableValue(ApprovalStatus.Resolved),
            DecidedBy = principal?.Kind == PrincipalKind.AgentRun
                ? principal.ToString()
                : principal?.TrustedInstanceId,
            DecidedAtMs = now
        };
        if (!store.TryCompareAndSetApproval(
                approval.ApprovalId,
                ApprovalStatusCodec.ToDurableValue(ApprovalStatus.Pending),
                next))
        {
            return RuntimeResults.Fail<RuntimeGrillResolveOutcome>(RuntimeError.GrillAlreadyResolved, "stale_already_resolved");
        }

        var checkpoint = store.CheckpointsForRun(persisted.RunId)
            .OrderByDescending(item => item.CreatedAtMs)
            .FirstOrDefault(item =>
                StringComparer.Ordinal.Equals(item.CheckpointId, persisted.CheckpointId) ||
                item.PayloadJson.Contains(persisted.ApprovalId, StringComparison.Ordinal));
        var run = store.GetRun(persisted.RunId);
        var freshness = scheduler.ClassifyResume(
            persisted.RunId,
            BuildResumeInputs(checkpoint, run, unknown, persisted.Reason));
        var mapped = freshness.Succeeded && freshness.Value is not null
            ? RuntimeGrillPolicy.MapResume(persisted.Reason, freshness.Value.Kind)
            : RuntimeGrillResolutionKind.PlanBlocked;
        if (mapped == RuntimeGrillResolutionKind.Continue &&
            store.GetRun(approval.RunId) is { } pausedRun &&
            RunStatusCodec.TryParse(pausedRun.Status, out var paused) &&
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
            option,
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

        var existing = AllSpecialists().FirstOrDefault(item =>
            StringComparer.Ordinal.Equals(item.SpecialistProfileId, profileId) &&
            StringComparer.Ordinal.Equals(item.ScopeKind, scopeKind));
        if (existing is null)
        {
            return RuntimeResults.Fail<SpecialistMutationOutcome>(RuntimeError.NotFound, "specialist");
        }

        SpecialistProfileDefinitionV1 profile;
        try
        {
            profile = SpecialistProfileCanonicalJson.Parse(definitionJson);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return RuntimeResults.Fail<SpecialistMutationOutcome>(RuntimeError.SpecialistInvalid, exception.Message);
        }

        if (!StringComparer.Ordinal.Equals(profile.ProfileId, profileId) || profile.ScopeKind != scope)
        {
            return RuntimeResults.Fail<SpecialistMutationOutcome>(RuntimeError.SpecialistIdentityMismatch, "target-body-mismatch");
        }

        var validation = SpecialistProfileValidator.Validate(profile);
        if (!validation.IsValid)
        {
            return RuntimeResults.Success(new SpecialistMutationOutcome(
                profile.ProfileId,
                validation.Errors.Select(item => item.Code + ":" + item.Message).ToArray()));
        }

        PersistProfile(profile, scope, principal.ProjectScope?.ProjectId.ToString("D"), existing.BaseDefinitionDigest);
        return RuntimeResults.Success(new SpecialistMutationOutcome(profile.ProfileId, []));
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
        if (!ExecutionBelongsToOwner(execution, record))
        {
            return RuntimeResults.Fail<StopBackgroundTaskResponse>(RuntimeError.TaskOwnershipDenied, "forged-execution-ref");
        }

        var stopped = execution.Kind switch
        {
            BackgroundTaskKind.SubAgentRun => CancelOwnedChildRun(execution, record),
            BackgroundTaskKind.ToolCall => CancelOwnedToolCall(execution, record),
            BackgroundTaskKind.Worker => StopOwnedWorker(execution, record),
            BackgroundTaskKind.RuntimeTask => CancelOwnedTask(execution, record),
            _ => RuntimeResults.Fail<bool>(RuntimeError.BackgroundStopUnavailable, "unknown-kind")
        };
        if (!stopped.Succeeded)
        {
            return RuntimeResults.Fail<StopBackgroundTaskResponse>(
                stopped.Failure?.Code ?? RuntimeError.BackgroundStopUnavailable,
                stopped.Failure?.Detail);
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
            .Select(item => ClassifyOne(item, snapshot, cancelledOwners, scheduler))
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

            var classification = ClassifyOne(record, snapshot, cancelledOwners, scheduler);
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
        HashSet<string> cancelledOwners,
        RuntimeSchedulerService scheduler)
    {
        var execution = BackgroundExecutionRefCodec.ParseKindColumn(item.KindJson);
        var unknown = UnknownSideEffectPolicy.BlocksAutomaticRetry(
            snapshot.ToolCalls,
            execution.TaskId ?? item.OwnerTaskId);
        var alive = execution.Kind switch
        {
            BackgroundTaskKind.SubAgentRun =>
                execution.RunId is not null &&
                snapshot.Runs.Any(run =>
                    StringComparer.Ordinal.Equals(run.RunId, execution.RunId) &&
                    RunStatusCodec.TryParse(run.Status, out var status) &&
                    RuntimeLifecycle.IsActive(status)),
            BackgroundTaskKind.ToolCall =>
                execution.ToolCallId is not null &&
                snapshot.ToolCalls.Any(call =>
                    StringComparer.Ordinal.Equals(call.ToolCallId, execution.ToolCallId) &&
                    StringComparer.Ordinal.Equals(call.Status, "running")),
            BackgroundTaskKind.Worker =>
                !string.IsNullOrWhiteSpace(execution.WorkerInstanceId) &&
                scheduler.WorkerIsAlive(execution.WorkerInstanceId),
            BackgroundTaskKind.RuntimeTask =>
                execution.TaskId is not null &&
                snapshot.Tasks.Any(task =>
                    StringComparer.Ordinal.Equals(task.TaskId, execution.TaskId) &&
                    TaskStatusCodec.TryParse(task.Status, out var status) &&
                    status is RuntimeTaskStatus.Running or RuntimeTaskStatus.Paused or RuntimeTaskStatus.Ready),
            _ => false
        };
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

    private bool HasInFlightExecution(OversightScopeKind scope, string? scopeId)
    {
        var snapshot = store.LoadSnapshot();
        return scope switch
        {
            OversightScopeKind.Task => snapshot.Tasks.Any(item =>
                StringComparer.Ordinal.Equals(item.TaskId, scopeId) &&
                TaskStatusCodec.TryParse(item.Status, out var status) &&
                status is RuntimeTaskStatus.Running or RuntimeTaskStatus.Paused),
            OversightScopeKind.Storyline => snapshot.Runs.Any(run =>
            {
                var workflow = snapshot.WorkflowRuns.FirstOrDefault(item =>
                    StringComparer.Ordinal.Equals(item.WorkflowRunId, run.WorkflowRunId));
                return StringComparer.Ordinal.Equals(workflow?.StorylineId, scopeId) &&
                       RunStatusCodec.TryParse(run.Status, out var status) &&
                       RuntimeLifecycle.IsActive(status);
            }),
            _ => snapshot.Runs.Any(item =>
                     RunStatusCodec.TryParse(item.Status, out var status) && RuntimeLifecycle.IsActive(status)) ||
                 snapshot.Tasks.Any(item =>
                     TaskStatusCodec.TryParse(item.Status, out var status) &&
                     status is RuntimeTaskStatus.Running or RuntimeTaskStatus.Paused)
        };
    }

    private OversightActivationContext BuildActivationContext(
        string? projectId,
        string? storylineId,
        string? taskId,
        string? runId)
    {
        DurableTaskRecord? task = null;
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            task = store.GetTask(taskId);
            runId ??= task?.RunId;
        }

        DurableRunRecord? run = null;
        if (!string.IsNullOrWhiteSpace(runId))
        {
            run = store.GetRun(runId);
        }

        var workflow = run is null ? null : store.GetWorkflowRun(run.WorkflowRunId);
        var checkpoints = run is null ? Array.Empty<DurableCheckpointRecord>() : store.CheckpointsForRun(run.RunId);
        return new OversightActivationContext(
            projectId,
            workflow?.StorylineId ?? storylineId,
            task?.TaskId ?? taskId,
            run?.RunId ?? runId,
            checkpoints);
    }

    private TaskResultArtifactV1 StampFreshness(
        TaskResultArtifactV1 artifact,
        DurableTaskRecord task,
        DurableAttemptRecord? attempt,
        CallerPrincipal principal)
    {
        var evidence = new Dictionary<string, EvidenceRecord>(StringComparer.Ordinal);
        foreach (var id in artifact.EvidenceIds)
        {
            var row = store.GetEvidence(id);
            if (row is not null)
            {
                evidence[id] = row;
            }
        }

        var upstream = new Dictionary<string, DurableResultArtifactRecord>(StringComparer.Ordinal);
        foreach (var reference in artifact.Freshness.ProducedAgainst.UpstreamRequiredResultRefs)
        {
            var row = store.GetResultArtifact(reference) ??
                      store.GetLatestResultArtifact(reference);
            if (row is not null)
            {
                upstream[reference] = row;
            }
        }

        var stamped = ResultFreshnessAuthority.Stamp(
            artifact.Freshness,
            new ResultFreshnessAuthorityInputs(
                principal.RunId ?? task.RunId,
                task.TaskId,
                attempt?.AttemptId,
                upstream,
                evidence,
                null,
                new HashSet<string>(StringComparer.Ordinal)),
            artifact.EvidenceIds);
        return artifact with { Freshness = stamped };
    }

    private RuntimeGrillDecisionRequestV1? LoadPersistedGrillRequest(DurableApprovalRecord approval)
    {
        foreach (var checkpoint in store.CheckpointsForRun(approval.RunId).OrderByDescending(item => item.CreatedAtMs))
        {
            try
            {
                var parsed = CanonicalJson.Parse(checkpoint.PayloadJson, checkpoint.SchemaVersion);
                var request = RuntimeGrillPolicy.TryParse(parsed.DagTaskStateJson);
                if (request is not null && StringComparer.Ordinal.Equals(request.ApprovalId, approval.ApprovalId))
                {
                    return request with { CheckpointId = checkpoint.CheckpointId };
                }
            }
            catch (CheckpointSchemaException)
            {
            }
        }

        return null;
    }

    private static FreshnessInputs BuildResumeInputs(
        DurableCheckpointRecord? checkpoint,
        DurableRunRecord? run,
        bool unknown,
        RuntimeGrillPauseReason reason)
    {
        _ = reason;
        _ = checkpoint;
        return new FreshnessInputs(
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            run?.PromptConfigId,
            run?.EffectivePromptDigest,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            run?.ProviderId,
            run?.ModelId,
            new Dictionary<string, string>(StringComparer.Ordinal),
            false,
            false,
            false,
            unknown);
    }

    private void ReevaluatePendingApprovals(string? runId, string? taskId)
    {
        foreach (var approval in store.ListApprovals(runId))
        {
            if (!ApprovalStatusCodec.TryParse(approval.Status, out var status) || status != ApprovalStatus.Pending)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(taskId) &&
                !string.IsNullOrWhiteSpace(approval.TaskId) &&
                !StringComparer.Ordinal.Equals(approval.TaskId, taskId))
            {
                continue;
            }

            var oversight = OversightResolver.Resolve(
                applicationDefaults.Current,
                store.ListOversightOverrides(),
                BuildActivationContext(null, null, approval.TaskId, approval.RunId));
            var snapshot = new PendingApprovalSnapshot(
                approval.ApprovalId,
                approval.ApprovalKind,
                StringComparer.Ordinal.Equals(approval.ApprovalKind, ApprovalKindCodec.RuntimeGrill),
                CapabilityAllowed: false,
                GateValid: true,
                InputsFresh: true,
                PlanValid: true,
                ProjectTrusted: false,
                HardDenied: false,
                oversight);
            var classification = PendingApprovalReevaluator.Reevaluate(snapshot);
            if (classification == PendingApprovalReevaluation.Denied)
            {
                store.TryCompareAndSetApproval(
                    approval.ApprovalId,
                    ApprovalStatusCodec.ToDurableValue(ApprovalStatus.Pending),
                    approval with
                    {
                        Status = ApprovalStatusCodec.ToDurableValue(ApprovalStatus.Denied),
                        DecidedAtMs = clock.UtcNow.ToUnixTimeMilliseconds()
                    });
            }
        }
    }

    private bool ExecutionBelongsToOwner(BackgroundExecutionRef execution, DurableBackgroundTaskRecord record)
    {
        var snapshot = store.LoadSnapshot();
        return execution.Kind switch
        {
            BackgroundTaskKind.SubAgentRun =>
                execution.RunId is not null &&
                snapshot.Runs.Any(run =>
                    StringComparer.Ordinal.Equals(run.RunId, execution.RunId) &&
                    StringComparer.Ordinal.Equals(run.ParentRunId, record.OwnerRunId)),
            BackgroundTaskKind.ToolCall =>
                execution.ToolCallId is not null &&
                store.GetToolCall(execution.ToolCallId) is { } tool &&
                StringComparer.Ordinal.Equals(tool.RunId, record.OwnerRunId),
            BackgroundTaskKind.Worker =>
                execution.WorkerInstanceId is not null &&
                scheduler.WorkerSnapshot().Any(item =>
                    StringComparer.Ordinal.Equals(item.WorkerInstanceId, execution.WorkerInstanceId) &&
                    StringComparer.Ordinal.Equals(item.RunId, record.OwnerRunId)),
            BackgroundTaskKind.RuntimeTask =>
                execution.TaskId is not null &&
                store.GetTask(execution.TaskId) is { } task &&
                StringComparer.Ordinal.Equals(task.RunId, record.OwnerRunId),
            _ => false
        };
    }

    private RuntimeResult<bool> CancelOwnedChildRun(BackgroundExecutionRef execution, DurableBackgroundTaskRecord record)
    {
        if (string.IsNullOrWhiteSpace(execution.RunId))
        {
            return RuntimeResults.Fail<bool>(RuntimeError.BackgroundStopUnavailable, "missing-child-run");
        }

        var result = scheduler.CancelScope("run", execution.RunId);
        return result.Succeeded
            ? RuntimeResults.Success(true)
            : RuntimeResults.Fail<bool>(result.Failure?.Code ?? RuntimeError.BackgroundStopUnavailable, result.Failure?.Detail);
    }

    private RuntimeResult<bool> CancelOwnedToolCall(BackgroundExecutionRef execution, DurableBackgroundTaskRecord record)
    {
        _ = record;
        if (string.IsNullOrWhiteSpace(execution.ToolCallId) || !store.TryCancelToolCall(execution.ToolCallId))
        {
            return RuntimeResults.Fail<bool>(RuntimeError.BackgroundStopUnavailable, "tool-call");
        }

        return RuntimeResults.Success(true);
    }

    private RuntimeResult<bool> StopOwnedWorker(BackgroundExecutionRef execution, DurableBackgroundTaskRecord record)
    {
        _ = record;
        if (string.IsNullOrWhiteSpace(execution.WorkerInstanceId) || !scheduler.WorkerIsAlive(execution.WorkerInstanceId))
        {
            return RuntimeResults.Fail<bool>(RuntimeError.BackgroundStopUnavailable, "worker");
        }

        var released = scheduler.ReleaseRunWorker(execution.WorkerInstanceId);
        return released.Succeeded
            ? RuntimeResults.Success(true)
            : RuntimeResults.Fail<bool>(released.Failure?.Code ?? RuntimeError.BackgroundStopUnavailable, released.Failure?.Detail);
    }

    private RuntimeResult<bool> CancelOwnedTask(BackgroundExecutionRef execution, DurableBackgroundTaskRecord record)
    {
        _ = record;
        if (string.IsNullOrWhiteSpace(execution.TaskId))
        {
            return RuntimeResults.Fail<bool>(RuntimeError.BackgroundStopUnavailable, "missing-task");
        }

        var result = scheduler.CancelScope("task", execution.TaskId);
        return result.Succeeded
            ? RuntimeResults.Success(true)
            : RuntimeResults.Fail<bool>(result.Failure?.Code ?? RuntimeError.BackgroundStopUnavailable, result.Failure?.Detail);
    }

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
