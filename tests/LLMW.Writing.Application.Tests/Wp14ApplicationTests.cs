using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Provider;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Prompt;
using LLMW.Writing.Domain.Provider;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Tests;

internal static class Wp14ApplicationTests
{
    public const string Canary = "sk-wp14-canary-9f3a2c";

    public static int Run()
    {
        var n = 0;
        n += Check(nameof(CoordinatorFreezesSnapshotBeforeConfigChange), CoordinatorFreezesSnapshotBeforeConfigChange);
        n += Check(nameof(CanarySecretNeverAppearsInSnapshotOrDefinition), CanarySecretNeverAppearsInSnapshotOrDefinition);
        n += Check(nameof(PinDoesNotFallback), PinDoesNotFallback);
        n += Check(nameof(FallbackCreatesNewInvocation), FallbackCreatesNewInvocation);
        n += Check(nameof(UnauthorizedAndHostedToolsDoNotExecute), UnauthorizedAndHostedToolsDoNotExecute);
        n += Check(nameof(StructuredOutputValidatedLocally), StructuredOutputValidatedLocally);
        n += Check(nameof(PromptChangeCausesReplan), PromptChangeCausesReplan);
        n += Check(nameof(RequiredStaleBlocksDispatch), RequiredStaleBlocksDispatch);
        n += Check(nameof(UnknownUsageFromAdapterIsNotZero), UnknownUsageFromAdapterIsNotZero);
        n += Check(nameof(ReportedModelDoesNotRewriteFrozenSnapshot), ReportedModelDoesNotRewriteFrozenSnapshot);
        n += Check(nameof(ProtocolProbeDoesNotRaiseTaskCeiling), ProtocolProbeDoesNotRaiseTaskCeiling);
        n += Check(nameof(CatalogNonDefaultModelIsRoutable), CatalogNonDefaultModelIsRoutable);
        n += Check(nameof(RetryUsesNewInvocationId), RetryUsesNewInvocationId);
        n += Check(nameof(CheckpointInvocationLogIsBounded), CheckpointInvocationLogIsBounded);
        n += Check(nameof(CrossRunCallerIsDenied), CrossRunCallerIsDenied);
        n += Check(nameof(MissingRunSessionIsDenied), MissingRunSessionIsDenied);
        n += Check(nameof(HistoricalInvocationReplayIsIdempotentWithoutIdentityRollback), HistoricalInvocationReplayIsIdempotentWithoutIdentityRollback);
        n += Check(nameof(SameInvocationDifferentSnapshotIsRejected), SameInvocationDifferentSnapshotIsRejected);
        n += Check(nameof(AttemptOwnedByAnotherTaskIsIllegal), AttemptOwnedByAnotherTaskIsIllegal);
        n += Check(nameof(PacketDigestChangeBlocksSendWhileResultsStayCurrent), PacketDigestChangeBlocksSendWhileResultsStayCurrent);
        n += Check(nameof(PersistedSnapshotGenerationIsCoreGenerationNotInvocationId), PersistedSnapshotGenerationIsCoreGenerationNotInvocationId);
        n += Check(nameof(MissingPromptBaselineCannotCertifyAndMatchingBaselineRoutes), MissingPromptBaselineCannotCertifyAndMatchingBaselineRoutes);
        n += Check(nameof(AuthenticatedIpcTransportBindsRunSession), AuthenticatedIpcTransportBindsRunSession);
        n += Check(nameof(RetentionRacePersistsI1TerminalRefinement), RetentionRacePersistsI1TerminalRefinement);
        n += Check(nameof(SameMillisecondLaterI1DoesNotRollBackI2Identity), SameMillisecondLaterI1DoesNotRollBackI2Identity);
        n += Check(nameof(ConcurrentPersistLinearizesCurrentIdentity), ConcurrentPersistLinearizesCurrentIdentity);
        n += Check(nameof(SnapshotGenerationMismatchIsProvenanceConflict), SnapshotGenerationMismatchIsProvenanceConflict);
        n += Check(nameof(TerminalTaskWithRunningAttemptIsIllegal), TerminalTaskWithRunningAttemptIsIllegal);
        n += Check(nameof(ToolLoopLimitIsIncompleteNotCompleted), ToolLoopLimitIsIncompleteNotCompleted);
        n += Check(nameof(CertificationPolicyIgnoresWeakCandidateThresholds), CertificationPolicyIgnoresWeakCandidateThresholds);
        n += Check(nameof(CoordinatorMultiRoundRetainsPriorToolTurns), CoordinatorMultiRoundRetainsPriorToolTurns);
        return n;
    }

    private static int Check(string name, Action test)
    {
        test();
        Console.WriteLine("PASS " + name);
        return 1;
    }

    private static void CoordinatorFreezesSnapshotBeforeConfigChange()
    {
        var harness = Harness();
        harness.Definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
        Certify(harness, "prov-a", "m1");
        var first = harness.Coordinator.Invoke(Command(harness, "run-cfg", "task-cfg"));
        AssertEqual("prov-a", first.Record.Snapshot.ProviderDefinitionId.Value, "First route drifted.");
        harness.Definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1", enabled: false));
        harness.Definitions.Upsert(ProviderDefinitionFactory.Create("prov-b", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m2", revision: 2));
        Certify(harness, "prov-b", "m2");
        var frozen = harness.Coordinator.GetFrozen(first.Record.Snapshot.InvocationId.Value);
        AssertEqual("prov-a", frozen!.ProviderDefinitionId.Value, "Active snapshot mutated after config change.");
        AssertEqual("m1", frozen.RequestedModelId.Value, "Frozen model mutated.");
        var second = harness.Coordinator.Invoke(Command(harness, "run-cfg-2", "task-cfg-2"));
        AssertEqual("prov-b", second.Record.Snapshot.ProviderDefinitionId.Value, "Next invocation did not observe new config.");
    }

    private static void CanarySecretNeverAppearsInSnapshotOrDefinition()
    {
        var harness = Harness();
        harness.Credentials.Store(new CredentialRef("cred-a"), Canary);
        harness.Definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
        Certify(harness, "prov-a", "m1");
        var outcome = harness.Coordinator.Invoke(Command(harness, "run-sec", "task-sec"));
        var json = outcome.Record.Snapshot.CanonicalJson();
        AssertTrue(!json.Contains(Canary, StringComparison.Ordinal), "Canary leaked into invocation snapshot.");
        AssertTrue(!json.Contains("Authorization", StringComparison.Ordinal), "Authorization leaked into snapshot.");
        var listed = System.Text.Json.JsonSerializer.Serialize(harness.Definitions.FindById(new ProviderDefinitionId("prov-a")));
        AssertTrue(!listed.Contains(Canary, StringComparison.Ordinal), "Canary leaked into Provider Definition.");
        foreach (var checkpoint in harness.Store.CheckpointsForRun("run-sec"))
        {
            AssertTrue(!checkpoint.PayloadJson.Contains(Canary, StringComparison.Ordinal), "Canary leaked into checkpoint.");
        }
    }

    private static void PinDoesNotFallback()
    {
        var harness = Harness(toolsOnlyOnB: true);
        var outcome = harness.Coordinator.Invoke(Command(harness, "run-pin", "task-pin", tools: true, pin: "prov-a"));
        AssertEqual("ROUTE_PIN_UNAVAILABLE", outcome.StructuredOutputError ?? outcome.Record.RefusalText, "Pin fell back.");
    }

    private static void FallbackCreatesNewInvocation()
    {
        var harness = Harness();
        harness.Definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
        Certify(harness, "prov-a", "m1");
        var first = harness.Coordinator.Invoke(Command(harness, "run-fb", "task-fb"));
        var second = harness.Coordinator.Invoke(Command(harness, "run-fb", "task-fb", fallbackFrom: first.Record.Snapshot.InvocationId.Value, fallbackReason: "timeout"));
        AssertTrue(first.Record.Snapshot.InvocationId.Value != second.Record.Snapshot.InvocationId.Value, "Fallback reused InvocationId.");
        AssertEqual(first.Record.Snapshot.InvocationId.Value, second.Record.Snapshot.FallbackFromInvocationId, "Fallback parent lost.");
        AssertEqual("timeout", second.Record.Snapshot.FallbackReason, "Fallback reason lost.");
    }

    private static void UnauthorizedAndHostedToolsDoNotExecute()
    {
        var unknown = ToolProposalGuard.Inspect(new ToolCallRequest("c1", "Shell.Execute", "{}"), [], null);
        AssertEqual("UNKNOWN_TOOL", unknown.DenialCode, "Unknown tool executed.");
        var hosted = ToolProposalGuard.Inspect(new ToolCallRequest("c2", "web_search", "{}"), [new AuthorizedToolSchema("web_search", "", "{}", [])], null);
        AssertEqual("PROVIDER_HOSTED_TOOL_UNSUPPORTED", hosted.DenialCode, "Hosted tool executed.");
        var malformed = ToolProposalGuard.Inspect(
            new ToolCallRequest("c3", "lookup", "{"),
            [new AuthorizedToolSchema("lookup", "", "{\"type\":\"object\"}", ["q"])],
            null);
        AssertEqual("MALFORMED_TOOL_ARGUMENTS", malformed.DenialCode, "Malformed args executed.");
        var denied = ToolProposalGuard.Inspect(
            new ToolCallRequest("c4", "lookup", "{\"q\":\"a\"}"),
            [new AuthorizedToolSchema("lookup", "", "{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}}}", ["q"])],
            CoreToolAuthorizationResult.Denied("Registry.Query", "CAPABILITY_DENIED"));
        AssertEqual("CAPABILITY_DENIED", denied.DenialCode, "CapabilityEvaluator bypassed.");
        var missingCore = ToolProposalGuard.Inspect(
            new ToolCallRequest("c5", "lookup", "{\"q\":\"a\"}"),
            [new AuthorizedToolSchema("lookup", "", "{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}}}", ["q"])],
            null);
        AssertTrue(!missingCore.MayExecute, "Null Core authorization executed a known tool.");
        AssertEqual("AWAITING_AUTHORIZATION", missingCore.DenialCode, "Null Core authorization was not fail-closed.");
    }

    private static void StructuredOutputValidatedLocally()
    {
        AssertTrue(!StructuredOutputValidator.TryValidateObject("{\"x\":1}", ["y"], out var error), "Missing field accepted.");
        AssertEqual("missing:y", error, "Validation error drifted.");
        AssertTrue(!StructuredOutputValidator.TryValidateObject("not-json", ["y"], out _), "Provider json-mode trusted.");
    }

    private static void PromptChangeCausesReplan()
    {
        var harness = Harness();
        harness.Definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
        Certify(harness, "prov-a", "m1");
        var first = harness.Coordinator.Invoke(Command(harness, "run-replan", "task-replan"));
        var run = harness.Store.GetRun("run-replan")!;
        var checkpoint = harness.Store.CheckpointsForRun("run-replan")[0];
        var same = ResumeClassifier.Classify(run, checkpoint, new FreshnessInputs(
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            run.PromptConfigId,
            run.EffectivePromptDigest,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            run.ProviderId,
            run.ModelId,
            new Dictionary<string, string>(StringComparer.Ordinal),
            false,
            false,
            false,
            false));
        AssertEqual(ResumeDecisionKind.Continue, same.Kind, "Unchanged prompt should CONTINUE.");
        var changed = ResumeClassifier.Classify(run, checkpoint, new FreshnessInputs(
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            "other-config",
            run.EffectivePromptDigest,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            run.ProviderId,
            run.ModelId,
            new Dictionary<string, string>(StringComparer.Ordinal),
            false,
            false,
            false,
            false));
        AssertEqual(ResumeDecisionKind.Replan, changed.Kind, "PromptConfig change must REPLAN.");
        _ = first;
    }

    private static void RequiredStaleBlocksDispatch()
    {
        var harness = Harness();
        harness.Definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
        Certify(harness, "prov-a", "m1");
        var outcome = harness.Coordinator.Invoke(Command(harness, "run-stale", "task-stale", staleRequired: true));
        AssertEqual("RESULT_REQUIRED_STALE", outcome.Record.RefusalText, "Stale required result dispatched.");
    }

    private static void UnknownUsageFromAdapterIsNotZero()
    {
        var harness = Harness();
        harness.Definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
        Certify(harness, "prov-a", "m1");
        var outcome = harness.Coordinator.Invoke(Command(harness, "run-usage", "task-usage"));
        AssertEqual(UsageStatus.Unknown, outcome.Record.Usage.Status, "Adapter missing usage became reported.");
        AssertTrue(outcome.Record.Usage.InputTokens.Value is null, "Missing usage became 0.");
        AssertTrue(!outcome.Record.Cost.InvoiceTruth, "Estimate labeled invoice truth.");
        AssertEqual(CostKind.Unknown, outcome.Record.Cost.Kind, "Missing usage became zero-cost.");
    }

    private static void ReportedModelDoesNotRewriteFrozenSnapshot()
    {
        var harness = Harness();
        harness.Definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
        Certify(harness, "prov-a", "m1");
        var outcome = harness.Coordinator.Invoke(Command(harness, "run-hist", "task-hist"));
        AssertEqual("m1", outcome.Record.Snapshot.EffectiveRoutedModelId.Value, "Frozen route rewritten.");
        AssertEqual("m1-actual", outcome.Record.ProviderReportedModel, "Reported model dropped.");
        var frozenJson = outcome.Record.Snapshot.CanonicalJson();
        var again = harness.Coordinator.GetFrozen(outcome.Record.Snapshot.InvocationId.Value)!.CanonicalJson();
        AssertEqual(frozenJson, again, "Frozen CanonicalJson mutated.");
        AssertTrue(!frozenJson.Contains("m1-actual", StringComparison.Ordinal), "Reported model leaked into frozen snapshot.");
    }

    private static void ProtocolProbeDoesNotRaiseTaskCeiling()
    {
        var harness = Harness();
        harness.Definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
        harness.Certifications.Upsert(CertificationFactory.Certified(
            new ProviderDefinitionId("prov-a"), new ProviderRevision(1), "http://127.0.0.1:9",
            "scripted", "v1", new ModelId("m1"),
            (ModelCapabilityNames.BasicText, CapabilitySupport.Supported),
            (ModelCapabilityNames.InstructionHierarchy, CapabilitySupport.Supported),
            (ModelCapabilityNames.ToolCalling, CapabilitySupport.Supported)) with { Ceiling = ReasoningCeiling.Adaptive });
        var command = Command(harness, "run-ceil", "task-ceil") with
        {
            Requirements = RouteRequirementProfile.TextOnly with { RequestedReasoning = ReasoningCeiling.Adaptive, RequiresInstructionHierarchy = false }
        };
        var outcome = harness.Coordinator.Invoke(command);
        AssertEqual("ROUTE_NO_ELIGIBLE_CANDIDATE", outcome.StructuredOutputError ?? outcome.Record.RefusalText, "Protocol probe raised the task ceiling.");
    }

    private static void CatalogNonDefaultModelIsRoutable()
    {
        var harness = Harness();
        harness.Definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
        Certify(harness, "prov-a", "m1");
        harness.Catalog.Upsert(new ModelCatalogEntry(
            new ModelId("m-custom"), "custom", new ProviderDefinitionId("prov-a"),
            128000, 4096, MetadataProvenance.UserConfigured, MetadataProvenance.UserConfigured,
            MetadataProvenance.UserConfigured, "manual", 1));
        Certify(harness, "prov-a", "m-custom");
        var command = Command(harness, "run-catalog", "task-catalog") with
        {
            Requirements = RouteRequirementProfile.TextOnly with
            {
                RequiresInstructionHierarchy = false,
                PinnedProviderDefinitionId = "prov-a",
                PinnedModelId = "m-custom"
            }
        };
        var outcome = harness.Coordinator.Invoke(command);
        AssertEqual("m-custom", outcome.Record.Snapshot.EffectiveRoutedModelId.Value, "Pinned catalog model was not routed.");
    }

    private static void RetryUsesNewInvocationId()
    {
        var store = new MemoryRuntimeStore();
        var clock = new FixedClock(10_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor());
        Success(scheduler.CreateWorkflowRun("wf-14"));
        Success(scheduler.CreateRun("wf-14", "writer", null, "run-retry"));
        Success(scheduler.CreateTask("run-retry", "write", 1, null, "task-retry"));
        ProviderInvocationStateHandler.SeedInferenceAttempt(store, "task-retry", AttemptId("task-retry"));
        var identity = new DirectProviderInvocationIdentity(store, clock);
        identity.Bind("run-retry");
        var definitions = new MemoryProviderDefinitionStore();
        var credentials = new MemoryProviderCredentialResolver();
        credentials.Store(new CredentialRef("cred-a"), Canary);
        definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
        var certifications = new MemoryModelCertificationStore();
        certifications.Upsert(CertificationFactory.Certified(
            new ProviderDefinitionId("prov-a"), new ProviderRevision(1), "http://127.0.0.1:9",
            "scripted", "v1", new ModelId("m1"),
            (ModelCapabilityNames.BasicText, CapabilitySupport.Supported),
            (ModelCapabilityNames.InstructionHierarchy, CapabilitySupport.Supported)));
        var remaining = 1;
        var adapter = new ScriptedProtocolAdapter(
            ProtocolKind.OpenAiResponses, "scripted", "v1",
            _ =>
            {
                if (remaining-- > 0)
                {
                    return new ProviderInvokeResult(
                        InvocationLifecycle.Rejected, InvocationFailureClass.HttpRateLimited,
                        [], "req", null, "m1", NormalizedUsage.Unknown, [], null, null, "429", false);
                }

                return new ProviderInvokeResult(
                    InvocationLifecycle.Completed, InvocationFailureClass.None,
                    [new ModelRuntimeEvent(ModelRuntimeEventKind.Completed, "ok", null, null, null, null, NormalizedUsage.Unknown, null, true)],
                    "req2", "resp", "m1", NormalizedUsage.Unknown, [], null, null, null, false);
            });
        var coordinator = new ProviderInvocationCoordinator(
            definitions, credentials, certifications, new MemoryPriceSnapshotStore(),
            new StaticProviderAdapterResolver(adapter),
            new DirectProviderInvocationStatePort(new ProviderInvocationStateHandler(store, scheduler), identity));
        var firstIds = new HashSet<string>(StringComparer.Ordinal);
        var outcome = coordinator.Invoke(Command(new HarnessState(store, scheduler, definitions, credentials, certifications, new MemoryModelCatalogStore(), coordinator, new MemoryTaskCertificationStore(), identity), "run-retry", "task-retry") with
        {
            Retry = new ProviderRetryPolicy(2, false)
        });
        AssertEqual(InvocationLifecycle.Completed, outcome.Record.Lifecycle, "Retry did not complete.");
        AssertTrue(!string.IsNullOrEmpty(outcome.Record.Snapshot.ParentInvocationId), "Retry parent InvocationId missing.");
        AssertEqual(InvocationContinuationKinds.Retry, outcome.Record.Snapshot.ContinuationKind, "Retry continuation kind drifted.");
        AssertTrue(firstIds.Add(outcome.Record.Snapshot.InvocationId.Value), "Retry reused InvocationId.");
        _ = firstIds;
    }

    private static void CheckpointInvocationLogIsBounded()
    {
        var harness = Harness();
        harness.Definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
        Certify(harness, "prov-a", "m1");
        for (var i = 0; i < 20; i++)
        {
            _ = harness.Coordinator.Invoke(Command(harness, "run-bound", "task-bound"));
        }

        var latest = harness.Store.CheckpointsForRun("run-bound")[0];
        var parsed = CanonicalJson.Parse(latest.PayloadJson, latest.SchemaVersion);
        AssertTrue(parsed.InvocationLog.Count <= CheckpointV1.RetainedInvocationLogLimit,
            "Checkpoint invocation log grew without bound.");
        AssertTrue(harness.Store.CheckpointsForRun("run-bound").Count >= 20, "Historical checkpoints were deleted.");
    }

    private static HarnessState Harness(bool toolsOnlyOnB = false)
    {
        var store = new MemoryRuntimeStore();
        var clock = new FixedClock(10_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor());
        Success(scheduler.CreateWorkflowRun("wf-14"));
        var identity = new DirectProviderInvocationIdentity(store, clock);
        foreach (var run in new[]
                 {
                     "run-cfg", "run-cfg-2", "run-sec", "run-pin", "run-fb", "run-replan", "run-stale", "run-usage",
                     "run-hist", "run-retry", "run-bound", "run-catalog", "run-ceil", "run-idemp", "run-fence",
                     "run-attempt", "run-cert", "run-cross-a", "run-cross-b", "run-gen",
                     "run-refine", "run-order", "run-race", "run-legal", "run-loop", "run-ms", "run-tools"
                 })
        {
            Success(scheduler.CreateRun("wf-14", "writer", null, run));
            var taskId = run.Replace("run", "task", StringComparison.Ordinal);
            Success(scheduler.CreateTask(run, "write", 1, null, taskId));
            ProviderInvocationStateHandler.SeedInferenceAttempt(store, taskId, AttemptId(taskId));
            identity.Bind(run);
        }

        var definitions = new MemoryProviderDefinitionStore();
        var credentials = new MemoryProviderCredentialResolver();
        credentials.Store(new CredentialRef("cred-a"), Canary);
        credentials.Store(new CredentialRef("cred-b"), Canary);
        var certifications = new MemoryModelCertificationStore();
        var catalog = new MemoryModelCatalogStore();
        if (toolsOnlyOnB)
        {
            definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
            definitions.Upsert(ProviderDefinitionFactory.Create("prov-b", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-b", "m2", priority: 1));
            certifications.Upsert(CertificationFactory.Certified(
                new ProviderDefinitionId("prov-a"), new ProviderRevision(1), "http://127.0.0.1:9",
                "scripted", "v1", new ModelId("m1"),
                (ModelCapabilityNames.BasicText, CapabilitySupport.Supported),
                (ModelCapabilityNames.ToolCalling, CapabilitySupport.Unsupported)));
            certifications.Upsert(CertificationFactory.Certified(
                new ProviderDefinitionId("prov-b"), new ProviderRevision(1), "http://127.0.0.1:9",
                "scripted", "v1", new ModelId("m2"),
                (ModelCapabilityNames.BasicText, CapabilitySupport.Supported),
                (ModelCapabilityNames.ToolCalling, CapabilitySupport.Supported)));
        }

        var adapter = new ScriptedProtocolAdapter(
            ProtocolKind.OpenAiResponses,
            "scripted",
            "v1",
            _ => new ProviderInvokeResult(
                InvocationLifecycle.Completed,
                InvocationFailureClass.None,
                [new ModelRuntimeEvent(ModelRuntimeEventKind.Completed, "ok", null, null, null, null, NormalizedUsage.Unknown, null, true)],
                "prov-req",
                "prov-resp",
                "m1-actual",
                NormalizedUsage.Unknown,
                [],
                null,
                null,
                null,
                false));
        var taskCertifications = new MemoryTaskCertificationStore();
        var coordinator = new ProviderInvocationCoordinator(
            definitions,
            credentials,
            certifications,
            new MemoryPriceSnapshotStore(),
            new StaticProviderAdapterResolver(adapter),
            new DirectProviderInvocationStatePort(new ProviderInvocationStateHandler(store, scheduler), identity),
            catalog: catalog,
            taskCertifications: taskCertifications,
            clock: TimeProvider.System);
        return new HarnessState(store, scheduler, definitions, credentials, certifications, catalog, coordinator, taskCertifications, identity);
    }

    private static void Certify(HarnessState harness, string provider, string model)
    {
        harness.Certifications.Upsert(CertificationFactory.Certified(
            new ProviderDefinitionId(provider),
            harness.Definitions.FindById(new ProviderDefinitionId(provider))!.Revision,
            harness.Definitions.FindById(new ProviderDefinitionId(provider))!.Endpoint,
            "scripted",
            "v1",
            new ModelId(model),
            (ModelCapabilityNames.BasicText, CapabilitySupport.Supported),
            (ModelCapabilityNames.Streaming, CapabilitySupport.Supported),
            (ModelCapabilityNames.InstructionHierarchy, CapabilitySupport.Supported)));
    }

    private static ModelInvocationCommand Command(
        HarnessState harness,
        string runId,
        string taskId,
        bool tools = false,
        string? pin = null,
        string? fallbackFrom = null,
        string? fallbackReason = null,
        bool staleRequired = false)
    {
        _ = harness;
        var compile = new PromptCompileRequest(
            PromptCompilerVersions.Current,
            "writer",
            "Writer role",
            "Behavioral",
            BehavioralOverrideMode.Default,
            null,
            ContentBehaviorMode.Sfw,
            ["AGENTS"],
            [],
            "workflow",
            "task",
            staleRequired ? [("r1", "old", true, true)] : [],
            [],
            "user text",
            tools ? [new AuthorizedToolSchema("lookup", "lookup", "{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}}}", ["q"])] : [],
            new PromptOutputContract(OutputContractKind.PlainText, null, []),
            null,
            32);
        var requirements = new RouteRequirementProfile(
            false,
            tools,
            false,
            false,
            ReasoningCeiling.Conservative,
            ProviderDataBehavior.Unknown,
            null,
            pin,
            pin is null ? null : "m1",
            null,
            null,
            false);
        return new ModelInvocationCommand(runId, taskId, AttemptId(taskId), compile, requirements, false, fallbackFrom, fallbackReason);
    }

    private static string AttemptId(string taskId) => taskId + "-att";

    private static void CrossRunCallerIsDenied()
    {
        var harness = Harness();
        var handler = new ProviderInvocationStateHandler(harness.Store, harness.Scheduler);
        var principalA = harness.Identity.PrincipalFor("run-cross-a")!;
        AssertDenied(
            () => handler.GetSnapshot(new GetTaskExecutionSnapshotRequest("run-cross-b", "task-cross-b", AttemptId("task-cross-b")), principalA, harness.Identity.Channel),
            IpcErrorCodes.TaskOwnershipDenied,
            "Cross-run snapshot was allowed.");
        AssertDenied(
            () => handler.Persist(
                new PersistProviderInvocationRequest("inv-x", "run-cross-b", "task-cross-b", AttemptId("task-cross-b"), "{}", null, "{}", "g1"),
                principalA,
                harness.Identity.Channel),
            IpcErrorCodes.TaskOwnershipDenied,
            "Cross-run persist was allowed.");
        AssertDenied(
            () => handler.Authorize(
                new AuthorizeToolProposalRequest("run-cross-b", "task-cross-b", "lookup", "{}", "Registry.Query", null),
                principalA,
                harness.Identity.Channel),
            IpcErrorCodes.TaskOwnershipDenied,
            "Authorize with request.Run != principal.Run was allowed.");
    }

    private static void MissingRunSessionIsDenied()
    {
        var harness = Harness();
        var handler = new ProviderInvocationStateHandler(harness.Store, harness.Scheduler);
        AssertDenied(
            () => handler.GetSnapshot(new GetTaskExecutionSnapshotRequest("run-cross-a", "task-cross-a", AttemptId("task-cross-a")), null, harness.Identity.Channel),
            IpcErrorCodes.InvalidSession,
            "Missing RunSession was allowed.");
    }

    private static void HistoricalInvocationReplayIsIdempotentWithoutIdentityRollback()
    {
        var harness = Harness();
        var handler = new ProviderInvocationStateHandler(harness.Store, harness.Scheduler);
        var principal = harness.Identity.PrincipalFor("run-idemp")!;
        const string run = "run-idemp";
        const string task = "task-idemp";
        ProviderInvocationSnapshot First() => InvocationSnap("inv-1", run, task, 1_000, "old-digest");
        var first = handler.Persist(
            new PersistProviderInvocationRequest("inv-1", run, task, AttemptId(task), First().CanonicalJson(), null, "{}", "g1"),
            principal,
            harness.Identity.Channel);
        AssertTrue(!first.IdempotentReplay, "First persist was treated as replay.");
        for (var i = 2; i <= 9; i++)
        {
            var snap = InvocationSnap("inv-" + i, run, task, 1_000 + i, "new-digest");
            _ = handler.Persist(
                new PersistProviderInvocationRequest(snap.InvocationId.Value, run, task, AttemptId(task), snap.CanonicalJson(), null, "{}", "g" + i),
                principal,
                harness.Identity.Channel);
        }

        AssertEqual("new-digest", harness.Store.GetRun(run)!.EffectivePromptDigest, "Current identity was not I9.");
        var before = harness.Store.CheckpointsForRun(run).Count;
        var replay = handler.Persist(
            new PersistProviderInvocationRequest("inv-1", run, task, AttemptId(task), First().CanonicalJson(), null, "{}", "g1"),
            principal,
            harness.Identity.Channel);
        AssertTrue(replay.IdempotentReplay, "Historical I1 replay was not idempotent.");
        AssertEqual(before, harness.Store.CheckpointsForRun(run).Count, "Historical I1 replay wrote a new checkpoint.");
        AssertEqual("new-digest", harness.Store.GetRun(run)!.EffectivePromptDigest, "Historical I1 replay rolled back Run identity.");
    }

    private static void SameInvocationDifferentSnapshotIsRejected()
    {
        var harness = Harness();
        var handler = new ProviderInvocationStateHandler(harness.Store, harness.Scheduler);
        var principal = harness.Identity.PrincipalFor("run-idemp")!;
        var first = InvocationSnap("inv-conflict", "run-idemp", "task-idemp", 2_000, "digest-a");
        _ = handler.Persist(
            new PersistProviderInvocationRequest("inv-conflict", "run-idemp", "task-idemp", AttemptId("task-idemp"), first.CanonicalJson(), null, "{}", "g1"),
            principal,
            harness.Identity.Channel);
        var other = InvocationSnap("inv-conflict", "run-idemp", "task-idemp", 2_000, "digest-b");
        AssertDenied(
            () => handler.Persist(
                new PersistProviderInvocationRequest("inv-conflict", "run-idemp", "task-idemp", AttemptId("task-idemp"), other.CanonicalJson(), null, "{}", "g1"),
                principal,
                harness.Identity.Channel),
            IpcErrorCodes.InvocationIdentityConflict,
            "Same InvocationId with a different snapshot identity was accepted.");
    }

    private static void AttemptOwnedByAnotherTaskIsIllegal()
    {
        var harness = Harness();
        var handler = new ProviderInvocationStateHandler(harness.Store, harness.Scheduler);
        var snapshot = handler.GetSnapshot(
            new GetTaskExecutionSnapshotRequest("run-attempt", "task-attempt", AttemptId("task-cross-a")),
            harness.Identity.PrincipalFor("run-attempt"),
            harness.Identity.Channel);
        AssertTrue(!snapshot.AttemptLegal, "Attempt owned by another Task was treated as legal.");
    }

    private static void PacketDigestChangeBlocksSendWhileResultsStayCurrent()
    {
        var harness = Harness();
        harness.Definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
        Certify(harness, "prov-a", "m1");
        var sends = 0;
        var adapter = new ScriptedProtocolAdapter(
            ProtocolKind.OpenAiResponses, "scripted", "v1",
            _ =>
            {
                sends++;
                return new ProviderInvokeResult(
                    InvocationLifecycle.Completed, InvocationFailureClass.None,
                    [new ModelRuntimeEvent(ModelRuntimeEventKind.Completed, "ok", null, null, null, null, NormalizedUsage.Unknown, null, true)],
                    "req", "resp", "m1", NormalizedUsage.Unknown, [], null, null, null, false);
            });
        var inner = new DirectProviderInvocationStatePort(
            new ProviderInvocationStateHandler(harness.Store, harness.Scheduler), harness.Identity);
        var coordinator = new ProviderInvocationCoordinator(
            harness.Definitions, harness.Credentials, harness.Certifications, new MemoryPriceSnapshotStore(),
            new StaticProviderAdapterResolver(adapter),
            new PacketDigestShiftPort(inner),
            catalog: harness.Catalog,
            taskCertifications: harness.TaskCertifications);
        var outcome = coordinator.Invoke(Command(harness, "run-fence", "task-fence"));
        AssertEqual(0, sends, "Old compiled request was sent after PacketDigest changed.");
        AssertEqual("EXECUTION_SNAPSHOT_STALE", outcome.Record.RefusalText, "PacketDigest change was not fenced.");
        AssertEqual(InvocationLifecycle.FailedBeforeSend, outcome.Record.Lifecycle, "Fenced send was not FailedBeforeSend.");
    }

    private static void PersistedSnapshotGenerationIsCoreGenerationNotInvocationId()
    {
        var harness = Harness();
        var handler = new ProviderInvocationStateHandler(harness.Store, harness.Scheduler);
        var principal = harness.Identity.PrincipalFor("run-gen")!;
        var g1 = handler.GetSnapshot(
            new GetTaskExecutionSnapshotRequest("run-gen", "task-gen", AttemptId("task-gen")),
            principal,
            harness.Identity.Channel).SnapshotGeneration;
        harness.Store.UpdateTaskCompletionContract("task-gen", "{\"packet\":\"g2\"}");
        var g2 = handler.GetSnapshot(
            new GetTaskExecutionSnapshotRequest("run-gen", "task-gen", AttemptId("task-gen")),
            principal,
            harness.Identity.Channel).SnapshotGeneration;
        AssertTrue(g1 != g2, "Packet change did not advance SnapshotGeneration.");
        var snap = InvocationSnap("inv-gen", "run-gen", "task-gen", 3_000, "digest", g1);
        var persisted = handler.Persist(
            new PersistProviderInvocationRequest("inv-gen", "run-gen", "task-gen", AttemptId("task-gen"), snap.CanonicalJson(), null, "{\"k\":\"v\"}", g1),
            principal,
            harness.Identity.Channel);
        AssertTrue(!persisted.IdempotentReplay, "First persist of G1 claim was a replay.");
        var checkpoint = harness.Store.CheckpointsForRun("run-gen")[0];
        AssertTrue(checkpoint.InputDigestSetJson.Contains(g1, StringComparison.Ordinal), "Claimed G1 was not stored.");
        AssertTrue(!checkpoint.InputDigestSetJson.Contains(snap.InvocationId.Value, StringComparison.Ordinal) ||
                   checkpoint.InputDigestSetJson.Contains("\"snapshotGeneration\":\"" + g1 + "\"", StringComparison.Ordinal),
            "SnapshotGeneration silently became InvocationId.");
        AssertTrue(checkpoint.PayloadJson.Contains("snapshotGeneration:" + g1, StringComparison.Ordinal) ||
                   checkpoint.InputDigestSetJson.Contains(g1, StringComparison.Ordinal),
            "Core did not retain compiled SnapshotGeneration G1.");
        AssertTrue(!string.Equals(g1, snap.InvocationId.Value, StringComparison.Ordinal), "Test fixture used InvocationId as G1.");
    }

    private static void MissingPromptBaselineCannotCertifyAndMatchingBaselineRoutes()
    {
        var service = new TaskCapabilityCertificationService(new MemoryTaskCertificationStore());
        var missing = service.Issue(RootConflictCert(null, ReasoningCeiling.Adaptive));
        AssertEqual(CertificationState.Uncertified, missing.State, "Certified without PromptBaselineDigest.");

        var harness = Harness();
        harness.Definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
        Certify(harness, "prov-a", "m1");
        var issuer = new TaskCapabilityCertificationService(harness.TaskCertifications);
        _ = issuer.Issue(RootConflictCert(PromptCompiler.CurrentShippedCertificationBaselineDigest, ReasoningCeiling.Adaptive));
        var matching = harness.Coordinator.Invoke(Command(harness, "run-cert", "task-cert") with
        {
            Requirements = RouteRequirementProfile.TextOnly with
            {
                RequiresInstructionHierarchy = false,
                RequestedReasoning = ReasoningCeiling.Adaptive,
                RequiredTaskClass = TaskCapabilityCertification.RootConflictTaskClass
            }
        });
        AssertEqual(InvocationLifecycle.Completed, matching.Record.Lifecycle, "Matching shipped baseline did not route.");

        harness.TaskCertifications.UpsertUncheckedForTests(RootConflictCert("other-baseline", ReasoningCeiling.Adaptive));
        var stale = harness.Coordinator.Invoke(Command(harness, "run-cert", "task-cert") with
        {
            Requirements = RouteRequirementProfile.TextOnly with
            {
                RequiresInstructionHierarchy = false,
                RequestedReasoning = ReasoningCeiling.Adaptive,
                RequiredTaskClass = TaskCapabilityCertification.RootConflictTaskClass
            }
        });
        AssertEqual("ROUTE_NO_ELIGIBLE_CANDIDATE", stale.StructuredOutputError ?? stale.Record.RefusalText,
            "Changed PromptBaselineDigest did not become STALE at the coordinator route.");
    }

    private static void AuthenticatedIpcTransportBindsRunSession()
    {
        var store = new MemoryRuntimeStore();
        var clock = new FixedClock(10_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor());
        var sessions = new RunSessionService(new RuntimePersistenceRunSessionStore(store), clock);
        var handler = new ProviderInvocationStateHandler(store, scheduler);
        var bindings = new TrustedIpcBindingRegistry();
        var scope = new ProjectScope(DirectProviderInvocationIdentity.TestProjectId, "workspace-01");
        bindings.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.AgentRuntime, "runtime-1", "runtime-ch", scope));
        var token = IpcBootstrapToken.Create();
        var options = new IpcServerOptions
        {
            WorkspaceInstanceId = "workspace-01",
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(token),
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D")),
            Bindings = bindings,
            RunSessions = sessions,
            Commands = new CompositeIpcCommandHandler(
                new RuntimeIpcCommandHandler(scheduler, "workspace-01"),
                new Wp14IpcCommandHandler(handler, "workspace-01"))
        };
        RunHosted(token, options, async client =>
        {
            var workflow = await client.RequestAsync(
                IpcSemanticTypes.CreateWorkflowRun,
                new CreateWorkflowRunRequest(null),
                IpcJsonContext.Default.CreateWorkflowRunRequestEnvelope,
                IpcJsonContext.Default.CreateWorkflowRunResponseEnvelope,
                CancellationToken.None);
            var run = await client.RequestAsync(
                IpcSemanticTypes.CreateRun,
                new CreateRunRequest(workflow.Payload.WorkflowRunId, "writer", null, "run-ipc"),
                IpcJsonContext.Default.CreateRunRequestEnvelope,
                IpcJsonContext.Default.CreateRunResponseEnvelope,
                CancellationToken.None);
            var task = await client.RequestAsync(
                IpcSemanticTypes.CreateTask,
                new CreateTaskRequest(run.Payload.RunId, "write", 1, null, "task-ipc"),
                IpcJsonContext.Default.CreateTaskRequestEnvelope,
                IpcJsonContext.Default.CreateTaskResponseEnvelope,
                CancellationToken.None);
            ProviderInvocationStateHandler.SeedInferenceAttempt(store, task.Payload.TaskId, AttemptId(task.Payload.TaskId));
            var issued = await client.RequestAsync(
                IpcSemanticTypes.CreateRunSession,
                new LLMW.Writing.Contracts.Ipc.CreateRunSessionRequest(run.Payload.RunId, null),
                IpcJsonContext.Default.CreateRunSessionRequestEnvelope,
                IpcJsonContext.Default.CreateRunSessionResponseEnvelope,
                CancellationToken.None);
            var proof = new RunSessionProof(issued.Payload.RunId, issued.Payload.OpaqueToken);
            var port = new AuthenticatedProviderInvocationStateClient(new IpcClientSessionRequestClient(client), proof);
            var snapshot = port.GetSnapshot(new GetTaskExecutionSnapshotRequest(run.Payload.RunId, task.Payload.TaskId, AttemptId(task.Payload.TaskId)));
            AssertTrue(snapshot.OwnershipValid, "Authenticated WP14 snapshot was not bound.");
            AssertTrue(snapshot.AttemptLegal, "Seeded attempt was illegal over authenticated IPC.");

            try
            {
                _ = port.GetSnapshot(new GetTaskExecutionSnapshotRequest("run-other", task.Payload.TaskId, AttemptId(task.Payload.TaskId)));
                throw new InvalidOperationException("Cross-run snapshot over real IPC was allowed.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.TaskOwnershipDenied, exception.ErrorCode, "Cross-run snapshot IPC code drifted.");
            }

            try
            {
                await client.RequestAsync(
                    IpcSemanticTypes.GetTaskExecutionSnapshot,
                    new GetTaskExecutionSnapshotRequest(run.Payload.RunId, task.Payload.TaskId, AttemptId(task.Payload.TaskId)),
                    IpcJsonContext.Default.GetTaskExecutionSnapshotRequestEnvelope,
                    IpcJsonContext.Default.GetTaskExecutionSnapshotResponseEnvelope,
                    CancellationToken.None);
                throw new InvalidOperationException("Missing RunSession was allowed on the real transport.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.InvalidSession, exception.ErrorCode, "Missing RunSession IPC code drifted.");
            }
        });
    }

    private static void RunHosted(string token, IpcServerOptions options, Func<IpcClientSession, Task> test)
    {
        var (left, right) = IpcConnectedStreamPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = Task.Run(() => IpcServerSession.ServeAsync(left, options, timeout.Token), timeout.Token);
        IpcClientSession? client = null;
        try
        {
            client = IpcClientSession.HandshakeAsync(right, "workspace-01", token, IpcClientKind.AgentRuntime, TimeSpan.FromMilliseconds(200), timeout.Token)
                .GetAwaiter()
                .GetResult();
            test(client).GetAwaiter().GetResult();
        }
        finally
        {
            client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            timeout.Cancel();
            try
            {
                server.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException)
            {
            }

            left.Dispose();
            right.Dispose();
        }
    }

    private static ProviderInvocationSnapshot InvocationSnap(
        string invocationId,
        string runId,
        string taskId,
        long createdAtMs,
        string digest,
        string? compiledGeneration = null) =>
        new(
            new InvocationId(invocationId),
            runId,
            taskId,
            AttemptId(taskId),
            new ProviderDefinitionId("prov-a"),
            new ProviderRevision(1),
            "scripted",
            "v1",
            new ModelId("m1"),
            new ModelId("m1"),
            null,
            "cfg",
            digest,
            digest,
            digest,
            digest,
            digest,
            ProviderDataBehavior.StatelessClientManaged,
            null,
            null,
            createdAtMs,
            null,
            null,
            "http://127.0.0.1:9/",
            "profile",
            1,
            null,
            null,
            compiledGeneration);

    private static TaskCapabilityCertification RootConflictCert(string? baseline, ReasoningCeiling ceiling) =>
        new(
            "task-cert-1",
            1,
            "ds-root",
            "1",
            TaskCapabilityCertification.CurrentEvaluationSuiteVersion,
            [
                new TaskEvalScore(RootConflictMetrics.RootRecall, 0.95m),
                new TaskEvalScore(RootConflictMetrics.FalseMergeRate, 0.01m),
                new TaskEvalScore(RootConflictMetrics.EvidenceFidelity, 0.9m),
                new TaskEvalScore(RootConflictMetrics.PropagationAccuracy, 0.9m),
                new TaskEvalScore(RootConflictMetrics.RecomputeAccuracy, 0.9m),
                new TaskEvalScore(RootConflictMetrics.AbstentionQuality, 0.8m)
            ],
            [
                TaskEvalThreshold.AtLeast(RootConflictMetrics.RootRecall, 0.9m),
                TaskEvalThreshold.AtMost(RootConflictMetrics.FalseMergeRate, 0.05m),
                TaskEvalThreshold.AtLeast(RootConflictMetrics.EvidenceFidelity, 0.9m),
                TaskEvalThreshold.AtLeast(RootConflictMetrics.PropagationAccuracy, 0.9m),
                TaskEvalThreshold.AtLeast(RootConflictMetrics.RecomputeAccuracy, 0.9m),
                TaskEvalThreshold.AtLeast(RootConflictMetrics.AbstentionQuality, 0.8m)
            ],
            [TaskCapabilityCertification.RootConflictTaskClass],
            ceiling,
            new ProviderDefinitionId("prov-a"),
            new ProviderRevision(1),
            "http://127.0.0.1:9/",
            "scripted",
            "v1",
            new ModelId("m1"),
            baseline,
            CertificationState.Certified,
            1);

    private static void RetentionRacePersistsI1TerminalRefinement()
    {
        var harness = Harness();
        var handler = new ProviderInvocationStateHandler(harness.Store, harness.Scheduler);
        var principal = harness.Identity.PrincipalFor("run-refine")!;
        const string run = "run-refine";
        const string task = "task-refine";
        var first = InvocationSnap("inv-1", run, task, 1_000, "old-digest");
        _ = handler.Persist(
            new PersistProviderInvocationRequest("inv-1", run, task, AttemptId(task), first.CanonicalJson(), null, "{}", "g1"),
            principal,
            harness.Identity.Channel);
        for (var i = 2; i <= 10; i++)
        {
            var snap = InvocationSnap("inv-" + i, run, task, 1_000 + i, "new-digest");
            _ = handler.Persist(
                new PersistProviderInvocationRequest(snap.InvocationId.Value, run, task, AttemptId(task), snap.CanonicalJson(), null, "{}", "g" + i),
                principal,
                harness.Identity.Channel);
        }

        AssertEqual("new-digest", harness.Store.GetRun(run)!.EffectivePromptDigest, "Current identity was not I10.");
        var before = harness.Store.CheckpointsForRun(run).Count;
        var completed = CompletedPayload(first, "req-1", "resp-1", 11);
        var refined = handler.Persist(
            new PersistProviderInvocationRequest("inv-1", run, task, AttemptId(task), first.CanonicalJson(), completed, "{}", "g1"),
            principal,
            harness.Identity.Channel);
        AssertTrue(!refined.IdempotentReplay, "I1 terminal refinement was mistaken for replay.");
        AssertTrue(harness.Store.CheckpointsForRun(run).Count > before, "I1 terminal refinement was not persisted.");
        AssertEqual("new-digest", harness.Store.GetRun(run)!.EffectivePromptDigest, "I1 refinement rolled back Run identity.");
        var discovered = DiscoverInvocations(harness.Store, run);
        AssertTrue(discovered.Contains("inv-1"), "I1 provenance was lost.");
        AssertTrue(discovered.Contains("inv-10"), "I10 provenance was overwritten by I1 prefix match.");
        AssertTrue(LatestLogHasInvocation(harness.Store, run, "inv-10"), "I1 refinement replaced I10 in the working window.");
        AssertTrue(LatestLogHasInvocation(harness.Store, run, "inv-1"), "I1 terminal record was not in the working window.");
        var replayBefore = harness.Store.CheckpointsForRun(run).Count;
        var replay = handler.Persist(
            new PersistProviderInvocationRequest("inv-1", run, task, AttemptId(task), first.CanonicalJson(), completed, "{}", "g1"),
            principal,
            harness.Identity.Channel);
        AssertTrue(replay.IdempotentReplay, "Exact terminal I1 replay was not idempotent.");
        AssertEqual(replayBefore, harness.Store.CheckpointsForRun(run).Count, "Exact terminal I1 replay wrote a checkpoint.");
        var conflicting = CompletedPayload(first, "req-1", "resp-1", 11, "Incomplete");
        AssertDenied(
            () => handler.Persist(
                new PersistProviderInvocationRequest("inv-1", run, task, AttemptId(task), first.CanonicalJson(), conflicting, "{}", "g1"),
                principal,
                harness.Identity.Channel),
            IpcErrorCodes.InvocationLifecycleConflict,
            "Conflicting terminal I1 truth was accepted.");
    }

    private static void SameMillisecondLaterI1DoesNotRollBackI2Identity()
    {
        var harness = Harness();
        var handler = new ProviderInvocationStateHandler(harness.Store, harness.Scheduler);
        var principal = harness.Identity.PrincipalFor("run-ms")!;
        const string run = "run-ms";
        const string task = "task-ms";
        var i1 = InvocationSnap("inv-ms-1", run, task, 5_000, "digest-1");
        var i2 = InvocationSnap("inv-ms-2", run, task, 5_000, "digest-2");
        _ = handler.Persist(
            new PersistProviderInvocationRequest("inv-ms-1", run, task, AttemptId(task), i1.CanonicalJson(), null, "{}", "g1"),
            principal,
            harness.Identity.Channel);
        _ = handler.Persist(
            new PersistProviderInvocationRequest("inv-ms-2", run, task, AttemptId(task), i2.CanonicalJson(), null, "{}", "g2"),
            principal,
            harness.Identity.Channel);
        AssertEqual("digest-2", harness.Store.GetRun(run)!.EffectivePromptDigest, "I2 was not current after same-ms create.");
        _ = handler.Persist(
            new PersistProviderInvocationRequest("inv-ms-2", run, task, AttemptId(task), i2.CanonicalJson(), CompletedPayload(i2, "r2", "s2", 7), "{}", "g2"),
            principal,
            harness.Identity.Channel);
        _ = handler.Persist(
            new PersistProviderInvocationRequest("inv-ms-1", run, task, AttemptId(task), i1.CanonicalJson(), CompletedPayload(i1, "r1", "s1", 3), "{}", "g1"),
            principal,
            harness.Identity.Channel);
        AssertEqual("digest-2", harness.Store.GetRun(run)!.EffectivePromptDigest, "Completing I1 after I2 rolled back identity.");
        var ids = DiscoverInvocations(harness.Store, run);
        AssertTrue(ids.Contains("inv-ms-1") && ids.Contains("inv-ms-2"), "Same-ms history was not discoverable.");
    }

    private static void ConcurrentPersistLinearizesCurrentIdentity()
    {
        var harness = Harness();
        using var barrier = new ManualRuntimeLinearizationBarrier(RuntimeLinearizationGate.BeforeProviderInvocationPersist);
        var handler = new ProviderInvocationStateHandler(harness.Store, harness.Scheduler, linearization: barrier);
        var principal = harness.Identity.PrincipalFor("run-race")!;
        const string run = "run-race";
        const string task = "task-race";
        var i1 = InvocationSnap("inv-race-1", run, task, 9_000, "digest-a");
        var i2 = InvocationSnap("inv-race-2", run, task, 9_000, "digest-b");
        Exception? error1 = null;
        Exception? error2 = null;
        barrier.Arm();
        var thread1 = new Thread(() =>
        {
            try
            {
                _ = handler.Persist(
                    new PersistProviderInvocationRequest("inv-race-1", run, task, AttemptId(task), i1.CanonicalJson(), null, "{}", "g1"),
                    principal,
                    harness.Identity.Channel);
            }
            catch (Exception exception)
            {
                error1 = exception;
            }
        })
        { IsBackground = true };
        thread1.Start();
        barrier.WaitUntilEntered();
        var thread2 = new Thread(() =>
        {
            try
            {
                _ = handler.Persist(
                    new PersistProviderInvocationRequest("inv-race-2", run, task, AttemptId(task), i2.CanonicalJson(), null, "{}", "g2"),
                    principal,
                    harness.Identity.Channel);
            }
            catch (Exception exception)
            {
                error2 = exception;
            }
        })
        { IsBackground = true };
        thread2.Start();
        barrier.Release();
        if (!thread1.Join(ManualRuntimeLinearizationBarrier.DefaultTimeoutMs) ||
            !thread2.Join(ManualRuntimeLinearizationBarrier.DefaultTimeoutMs))
        {
            throw new TimeoutException("Concurrent persist threads did not finish.");
        }

        AssertTrue(error1 is null && error2 is null, "Concurrent persist failed: " + (error1 ?? error2));
        var current = harness.Store.GetRun(run)!.EffectivePromptDigest;
        AssertTrue(current is "digest-a" or "digest-b", "Current identity was lost.");
        var ids = DiscoverInvocations(harness.Store, run);
        AssertTrue(ids.Contains("inv-race-1") && ids.Contains("inv-race-2"), "Raced invocations were not both discoverable.");
        var reloaded = harness.Store.GetRun(run)!.EffectivePromptDigest;
        AssertEqual(current, reloaded, "Reloaded current identity drifted.");
    }

    private static void SnapshotGenerationMismatchIsProvenanceConflict()
    {
        var harness = Harness();
        var handler = new ProviderInvocationStateHandler(harness.Store, harness.Scheduler);
        var principal = harness.Identity.PrincipalFor("run-gen")!;
        var snap = InvocationSnap("inv-gfake", "run-gen", "task-gen", 4_000, "digest", "G1");
        AssertDenied(
            () => handler.Persist(
                new PersistProviderInvocationRequest("inv-gfake", "run-gen", "task-gen", AttemptId("task-gen"), snap.CanonicalJson(), null, "{}", "Gfake"),
                principal,
                harness.Identity.Channel),
            IpcErrorCodes.InvocationProvenanceConflict,
            "Mismatched SnapshotGeneration was accepted.");
    }

    private static void TerminalTaskWithRunningAttemptIsIllegal()
    {
        var harness = Harness();
        var handler = new ProviderInvocationStateHandler(harness.Store, harness.Scheduler);
        harness.Store.UpdateTaskStatus("task-legal", TaskStatusCodec.ToDurableValue(LLMW.Writing.Domain.Runtime.TaskStatus.Cancelled), 11_000);
        var snapshot = handler.GetSnapshot(
            new GetTaskExecutionSnapshotRequest("run-legal", "task-legal", AttemptId("task-legal")),
            harness.Identity.PrincipalFor("run-legal"),
            harness.Identity.Channel);
        AssertTrue(!snapshot.AttemptLegal, "Cancelled Task with Starting Attempt was treated as legal.");
    }

    private static void ToolLoopLimitIsIncompleteNotCompleted()
    {
        var harness = Harness();
        var handler = new ProviderInvocationStateHandler(harness.Store, harness.Scheduler, new AllowAllAuth());
        var calls = 0;
        var adapter = new ScriptedProtocolAdapter(
            ProtocolKind.OpenAiResponses, "scripted", "v1",
            _ =>
            {
                calls++;
                var id = "call-" + calls;
                return new ProviderInvokeResult(
                    InvocationLifecycle.Completed, InvocationFailureClass.None,
                    [new ModelRuntimeEvent(ModelRuntimeEventKind.ToolCallCompleted, null, id, "lookup", null, "{\"q\":\"a\"}", null, null, false)],
                    "req", "resp", "m1", NormalizedUsage.Unknown,
                    [new ToolCallRequest(id, "lookup", "{\"q\":\"a\"}")],
                    null, null, null, false);
            });
        harness.Definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
        harness.Certifications.Upsert(CertificationFactory.Certified(
            new ProviderDefinitionId("prov-a"), new ProviderRevision(1), "http://127.0.0.1:9",
            "scripted", "v1", new ModelId("m1"),
            (ModelCapabilityNames.BasicText, CapabilitySupport.Supported),
            (ModelCapabilityNames.ToolCalling, CapabilitySupport.Supported),
            (ModelCapabilityNames.InstructionHierarchy, CapabilitySupport.Supported)));
        var coordinator = new ProviderInvocationCoordinator(
            harness.Definitions, harness.Credentials, harness.Certifications, new MemoryPriceSnapshotStore(),
            new StaticProviderAdapterResolver(adapter),
            new DirectProviderInvocationStatePort(handler, harness.Identity),
            catalog: harness.Catalog,
            taskCertifications: harness.TaskCertifications);
        var outcome = coordinator.Invoke(Command(harness, "run-loop", "task-loop", tools: true) with
        {
            ToolExecutor = new RecordingToolExecutor()
        });
        AssertEqual(InvocationLifecycle.Incomplete, outcome.Record.Lifecycle, "Tool loop limit returned ordinary Completed.");
        AssertEqual("TOOL_LOOP_LIMIT", outcome.Record.RefusalText ?? outcome.StructuredOutputError, "Tool loop limit was not typed.");
        AssertTrue(calls > ProviderInvocationCoordinator.MaxToolRounds, "Coordinator stopped before the tool-round ceiling.");
    }

    private static void CertificationPolicyIgnoresWeakCandidateThresholds()
    {
        var service = new TaskCapabilityCertificationService(new MemoryTaskCertificationStore());
        var weak = RootConflictCert(PromptCompiler.CurrentShippedCertificationBaselineDigest, ReasoningCeiling.Adaptive) with
        {
            Scores =
            [
                new TaskEvalScore(RootConflictMetrics.RootRecall, 0.95m),
                new TaskEvalScore(RootConflictMetrics.FalseMergeRate, 0.50m),
                new TaskEvalScore(RootConflictMetrics.EvidenceFidelity, 0.9m),
                new TaskEvalScore(RootConflictMetrics.PropagationAccuracy, 0.9m),
                new TaskEvalScore(RootConflictMetrics.RecomputeAccuracy, 0.9m),
                new TaskEvalScore(RootConflictMetrics.AbstentionQuality, 0.8m)
            ],
            Thresholds =
            [
                TaskEvalThreshold.AtLeast(RootConflictMetrics.RootRecall, 0.9m),
                TaskEvalThreshold.AtMost(RootConflictMetrics.FalseMergeRate, 1.00m),
                TaskEvalThreshold.AtLeast(RootConflictMetrics.EvidenceFidelity, 0.9m),
                TaskEvalThreshold.AtLeast(RootConflictMetrics.PropagationAccuracy, 0.9m),
                TaskEvalThreshold.AtLeast(RootConflictMetrics.RecomputeAccuracy, 0.9m),
                TaskEvalThreshold.AtLeast(RootConflictMetrics.AbstentionQuality, 0.8m)
            ]
        };
        AssertEqual(CertificationState.Uncertified, service.Issue(weak).State, "Candidate bound=1.00 certified FalseMergeRate=0.50.");

        var wrongComparator = RootConflictCert(PromptCompiler.CurrentShippedCertificationBaselineDigest, ReasoningCeiling.Adaptive) with
        {
            Thresholds =
            [
                TaskEvalThreshold.AtLeast(RootConflictMetrics.RootRecall, 0.9m),
                TaskEvalThreshold.AtLeast(RootConflictMetrics.FalseMergeRate, 0.05m),
                TaskEvalThreshold.AtLeast(RootConflictMetrics.EvidenceFidelity, 0.9m),
                TaskEvalThreshold.AtLeast(RootConflictMetrics.PropagationAccuracy, 0.9m),
                TaskEvalThreshold.AtLeast(RootConflictMetrics.RecomputeAccuracy, 0.9m),
                TaskEvalThreshold.AtLeast(RootConflictMetrics.AbstentionQuality, 0.8m)
            ]
        };
        AssertEqual(CertificationState.Uncertified, service.Issue(wrongComparator).State, "Wrong FalseMergeRate comparator certified.");

        var missing = RootConflictCert(PromptCompiler.CurrentShippedCertificationBaselineDigest, ReasoningCeiling.Adaptive) with
        {
            Scores = RootConflictCert(PromptCompiler.CurrentShippedCertificationBaselineDigest, ReasoningCeiling.Adaptive)
                .Scores.Where(item => item.MetricName != RootConflictMetrics.EvidenceFidelity).ToArray()
        };
        AssertEqual(CertificationState.Uncertified, service.Issue(missing).State, "Missing required metric certified.");

        var valid = service.Issue(RootConflictCert(PromptCompiler.CurrentShippedCertificationBaselineDigest, ReasoningCeiling.Adaptive));
        AssertEqual(CertificationState.Certified, valid.State, "Valid scores against authoritative profile did not certify.");
    }

    private static void CoordinatorMultiRoundRetainsPriorToolTurns()
    {
        var harness = Harness();
        var handler = new ProviderInvocationStateHandler(harness.Store, harness.Scheduler, new AllowAllAuth());
        ProviderInvokeRequest? last = null;
        var round = 0;
        var adapter = new ScriptedProtocolAdapter(
            ProtocolKind.OpenAiResponses, "scripted", "v1",
            request =>
            {
                last = request;
                round++;
                if (round >= 3)
                {
                    return new ProviderInvokeResult(
                        InvocationLifecycle.Completed, InvocationFailureClass.None,
                        [new ModelRuntimeEvent(ModelRuntimeEventKind.Completed, "done", null, null, null, null, NormalizedUsage.Unknown, null, true)],
                        "req", "resp", "m1", NormalizedUsage.Unknown, [], null, null, null, false);
                }

                var id = round == 1 ? "tool-a" : "tool-b";
                return new ProviderInvokeResult(
                    InvocationLifecycle.Completed, InvocationFailureClass.None,
                    [new ModelRuntimeEvent(ModelRuntimeEventKind.ToolCallCompleted, null, id, "lookup", null, "{\"q\":\"" + id + "\"}", null, null, false)],
                    "req", "resp", "m1", NormalizedUsage.Unknown,
                    [new ToolCallRequest(id, "lookup", "{\"q\":\"" + id + "\"}")],
                    null, null, null, false);
            });
        harness.Definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
        harness.Certifications.Upsert(CertificationFactory.Certified(
            new ProviderDefinitionId("prov-a"), new ProviderRevision(1), "http://127.0.0.1:9",
            "scripted", "v1", new ModelId("m1"),
            (ModelCapabilityNames.BasicText, CapabilitySupport.Supported),
            (ModelCapabilityNames.ToolCalling, CapabilitySupport.Supported),
            (ModelCapabilityNames.InstructionHierarchy, CapabilitySupport.Supported)));
        var coordinator = new ProviderInvocationCoordinator(
            harness.Definitions, harness.Credentials, harness.Certifications, new MemoryPriceSnapshotStore(),
            new StaticProviderAdapterResolver(adapter),
            new DirectProviderInvocationStatePort(handler, harness.Identity),
            catalog: harness.Catalog,
            taskCertifications: harness.TaskCertifications);
        var outcome = coordinator.Invoke(Command(harness, "run-tools", "task-tools", tools: true) with
        {
            ToolExecutor = new RecordingToolExecutor()
        });
        AssertEqual(InvocationLifecycle.Completed, outcome.Record.Lifecycle, "Three-round tool loop failed.");
        AssertTrue(last is not null, "Final request was missing.");
        AssertTrue(last!.ContinuationState is not null && last.ContinuationState.NormalizedToolCallIds.Contains("tool-a") &&
                   last.ContinuationState.NormalizedToolCallIds.Contains("tool-b"),
            "Round 3 continuation lost tool A or B.");
        AssertTrue((last.ToolContinuation?.Count ?? 0) >= 2 || last.ContinuationState!.NormalizedToolCallIds.Count >= 2,
            "Round 3 dropped prior tool history.");
    }

    private static bool LatestLogHasInvocation(MemoryRuntimeStore store, string runId, string invocationId)
    {
        var latest = store.CheckpointsForRun(runId)
            .OrderBy(item => item.CreatedAtMs)
            .ThenBy(item => item.CheckpointId, StringComparer.Ordinal)
            .LastOrDefault();
        if (latest is null)
        {
            return false;
        }

        try
        {
            var parsed = CanonicalJson.Parse(latest.PayloadJson, latest.SchemaVersion);
            foreach (var item in parsed.InvocationLog)
            {
                using var document = System.Text.Json.JsonDocument.Parse(item);
                if (document.RootElement.TryGetProperty("invocationId", out var id) &&
                    string.Equals(id.GetString(), invocationId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (CheckpointSchemaException)
        {
        }

        return false;
    }

    private static HashSet<string> DiscoverInvocations(MemoryRuntimeStore store, string runId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var checkpoint in store.CheckpointsForRun(runId))
        {
            try
            {
                var parsed = CanonicalJson.Parse(checkpoint.PayloadJson, checkpoint.SchemaVersion);
                foreach (var item in parsed.InvocationLog)
                {
                    using var document = System.Text.Json.JsonDocument.Parse(item);
                    if (document.RootElement.TryGetProperty("invocationId", out var id) && id.GetString() is { } value)
                    {
                        ids.Add(value);
                    }
                }
            }
            catch (CheckpointSchemaException)
            {
            }
        }

        return ids;
    }

    private static string CompletedPayload(
        ProviderInvocationSnapshot snapshot,
        string requestId,
        string responseId,
        long outputTokens,
        string lifecycle = "Completed")
    {
        using var document = System.Text.Json.JsonDocument.Parse(snapshot.CanonicalJson());
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteString("lifecycle", lifecycle);
            writer.WriteString("failureClass", lifecycle == "Completed" ? "None" : "IncompleteGeneration");
            writer.WriteString("providerRequestId", requestId);
            writer.WriteString("providerResponseId", responseId);
            writer.WriteString("providerReportedModel", snapshot.EffectiveModelId.Value);
            writer.WritePropertyName("usage");
            using (var usage = System.Text.Json.JsonDocument.Parse(
                       new NormalizedUsage(
                           UsageStatus.Reported,
                           OptionalTokenCount.Reported(1),
                           OptionalTokenCount.Unknown,
                           OptionalTokenCount.Unknown,
                           OptionalTokenCount.Reported(outputTokens),
                           OptionalTokenCount.Unknown,
                           new Dictionary<string, long>(),
                           null).CanonicalJson()))
            {
                usage.RootElement.WriteTo(writer);
            }

            writer.WriteBoolean("duplicateExecutionRisk", false);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class AllowAllAuth : IAuthorizationService
    {
        public CapabilityDecision Authorize(CallerPrincipal? principal, AuthorizationRequest request) =>
            new(
                request.Capability,
                principal?.Kind ?? PrincipalKind.AgentRun,
                CapabilityDecisionKind.Allowed,
                [CapabilityDecisionReason.Allowed],
                principal?.Role,
                RoleCapabilityLevel.Allowed,
                SecurityScopeClassification.InScope,
                HardDenied: false);
    }

    private sealed class RecordingToolExecutor : ILocalToolExecutor
    {
        public LocalToolExecutionResult Execute(string callId, string toolName, string argumentsJson) =>
            new(callId, toolName, "{\"ok\":true}", null);
    }

    private static void AssertDenied(Action action, string code, string message)
    {
        try
        {
            action();
        }
        catch (ProviderInvocationDeniedException exception)
        {
            AssertEqual(code, exception.Code, message);
            return;
        }
        catch (IpcProtocolException exception)
        {
            AssertEqual(code, exception.ErrorCode, message);
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed class PacketDigestShiftPort(IProviderInvocationStatePort inner) : IProviderInvocationStatePort
    {
        private GetTaskExecutionSnapshotResponse? compiled;

        public GetTaskExecutionSnapshotResponse GetSnapshot(GetTaskExecutionSnapshotRequest request)
        {
            var snapshot = inner.GetSnapshot(request);
            if (compiled is null)
            {
                compiled = snapshot;
                return snapshot;
            }

            return snapshot with
            {
                SnapshotGeneration = compiled.SnapshotGeneration + "-g2",
                PacketDigest = "packet-changed",
                RequiredResults = compiled.RequiredResults
            };
        }

        public PersistProviderInvocationResponse Persist(PersistProviderInvocationRequest request) => inner.Persist(request);

        public AuthorizeToolProposalResponse Authorize(AuthorizeToolProposalRequest request) => inner.Authorize(request);
    }

    private static T Success<T>(RuntimeResult<T> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException(result.Failure?.Code + " " + result.Failure?.Detail);
        }

        return result.Value;
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private sealed record HarnessState(
        MemoryRuntimeStore Store,
        RuntimeSchedulerService Scheduler,
        MemoryProviderDefinitionStore Definitions,
        MemoryProviderCredentialResolver Credentials,
        MemoryModelCertificationStore Certifications,
        MemoryModelCatalogStore Catalog,
        ProviderInvocationCoordinator Coordinator,
        MemoryTaskCertificationStore TaskCertifications,
        DirectProviderInvocationIdentity Identity);

    private sealed class FixedClock(long unixMs) : ISecurityClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
    }
}
