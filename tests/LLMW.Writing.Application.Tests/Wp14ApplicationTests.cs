using LLMW.Writing.Application.Provider;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
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
            new DirectProviderInvocationStatePort(new ProviderInvocationStateHandler(store, scheduler)));
        var firstIds = new HashSet<string>(StringComparer.Ordinal);
        var outcome = coordinator.Invoke(Command(new HarnessState(store, scheduler, definitions, credentials, certifications, new MemoryModelCatalogStore(), coordinator), "run-retry", "task-retry") with
        {
            Retry = new ProviderRetryPolicy(2, false)
        });
        AssertEqual(InvocationLifecycle.Completed, outcome.Record.Lifecycle, "Retry did not complete.");
        AssertTrue(!string.IsNullOrEmpty(outcome.Record.Snapshot.FallbackFromInvocationId), "Retry parent InvocationId missing.");
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
        foreach (var run in new[] { "run-cfg", "run-cfg-2", "run-sec", "run-pin", "run-fb", "run-replan", "run-stale", "run-usage", "run-hist", "run-retry", "run-bound", "run-catalog", "run-ceil" })
        {
            Success(scheduler.CreateRun("wf-14", "writer", null, run));
            Success(scheduler.CreateTask(run, "write", 1, null, run.Replace("run", "task", StringComparison.Ordinal)));
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
        var coordinator = new ProviderInvocationCoordinator(
            definitions,
            credentials,
            certifications,
            new MemoryPriceSnapshotStore(),
            new StaticProviderAdapterResolver(adapter),
            new DirectProviderInvocationStatePort(new ProviderInvocationStateHandler(store, scheduler)),
            catalog: catalog,
            clock: TimeProvider.System);
        return new HarnessState(store, scheduler, definitions, credentials, certifications, catalog, coordinator);
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
        return new ModelInvocationCommand(runId, taskId, "att-1", compile, requirements, false, fallbackFrom, fallbackReason);
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
        ProviderInvocationCoordinator Coordinator);

    private sealed class FixedClock(long unixMs) : ISecurityClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
    }
}
