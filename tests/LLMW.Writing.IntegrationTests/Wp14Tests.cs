using LLMW.Writing.Application.Provider;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Prompt;
using LLMW.Writing.Domain.Provider;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private static readonly List<string> Wp14PassedTests = [];

    private static void RunWp14Tests()
    {
        RunWp14(nameof(SqliteInvocationProvenanceSurvivesReloadWithoutSecretOrMigration), SqliteInvocationProvenanceSurvivesReloadWithoutSecretOrMigration);
        RunWp14(nameof(SqlitePromptProviderChangeReplans), SqlitePromptProviderChangeReplans);
        Console.WriteLine($"WP14 integration tests passed ({Wp14PassedTests.Count}).");
        foreach (var test in Wp14PassedTests)
        {
            Console.WriteLine($"PASS {test}");
        }
    }

    private static void RunWp14(string name, Action test)
    {
        test();
        Wp14PassedTests.Add(name);
    }

    private static void SqliteInvocationProvenanceSurvivesReloadWithoutSecretOrMigration()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP14", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        var migrated = new SqliteMigrationRunner().Migrate(databasePath, "wp14-tests", 1735689600000);
        AssertEqual(1, migrated.UserVersion, "WP14 must not migrate schema.");
        AssertEqual(1, migrated.MigrationCount, "WP14 must keep schema_migrations=1.");

        var store = new SqliteRuntimeStore(databasePath);
        var clock = new Wp14Clock(20_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor());
        var definitions = new MemoryProviderDefinitionStore();
        var credentials = new MemoryProviderCredentialResolver();
        credentials.Store(new CredentialRef("cred-a"), "sk-wp14-canary-9f3a2c");
        definitions.Upsert(ProviderDefinitionFactory.Create("prov-a", ProtocolKind.OpenAiResponses, "http://127.0.0.1:9", "cred-a", "m1"));
        var certifications = new MemoryModelCertificationStore();
        certifications.Upsert(CertificationFactory.Certified(
            new ProviderDefinitionId("prov-a"), new ProviderRevision(1), "http://127.0.0.1:9",
            "scripted", "v1", new ModelId("m1"),
            (ModelCapabilityNames.BasicText, CapabilitySupport.Supported)));
        var adapter = new ScriptedProtocolAdapter(
            ProtocolKind.OpenAiResponses, "scripted", "v1",
            _ => new ProviderInvokeResult(
                InvocationLifecycle.Completed, InvocationFailureClass.None,
                [new ModelRuntimeEvent(ModelRuntimeEventKind.Completed, "ok", null, null, null, null, NormalizedUsage.Unknown, null, true)],
                "req", "resp", "m1-actual", NormalizedUsage.Unknown, [], null, null, null, false));
        var coordinator = new ProviderInvocationCoordinator(
            definitions, credentials, certifications, new MemoryPriceSnapshotStore(),
            new StaticProviderAdapterResolver(adapter), store, scheduler, TimeProvider.System);
        Success(scheduler.CreateWorkflowRun("wf-14"));
        Success(scheduler.CreateRun("wf-14", "writer", null, "run-14"));
        Success(scheduler.CreateTask("run-14", "write", 1, null, "task-14"));
        var outcome = coordinator.Invoke(new ModelInvocationCommand(
            "run-14",
            "task-14",
            "att-14",
            new PromptCompileRequest(
                PromptCompilerVersions.Current, "writer", "role", "behavior", BehavioralOverrideMode.Default, null,
                ContentBehaviorMode.Sfw, ["AGENTS"], [], "wf", "task", [], [], "user", [],
                new PromptOutputContract(OutputContractKind.PlainText, null, []), null, 16),
            RouteRequirementProfile.TextOnly with { RequiresInstructionHierarchy = false },
            null,
            false));
        AssertEqual(InvocationLifecycle.Completed.ToString(), outcome.Record.Lifecycle.ToString(), "Invocation failed.");
        AssertTrue(!string.IsNullOrWhiteSpace(outcome.Prompt?.EffectivePromptDigest), "EffectivePromptDigest missing.");

        using (var connection = new SqliteConnection("Data Source=" + databasePath))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            AssertEqual(1L, (long)(command.ExecuteScalar() ?? 0L), "user_version changed.");
            command.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
            AssertEqual(1L, (long)(command.ExecuteScalar() ?? 0L), "schema_migrations changed.");
            command.CommandText = "SELECT provider_id, model_id, prompt_config_id, effective_prompt_digest FROM runs WHERE run_id='run-14';";
            using var reader = command.ExecuteReader();
            AssertTrue(reader.Read(), "Run row missing.");
            AssertEqual("prov-a", reader.GetString(0), "provider_id not persisted.");
            AssertTrue(!string.IsNullOrWhiteSpace(reader.GetString(3)), "effective_prompt_digest empty.");
        }

        var reloaded = new SqliteRuntimeStore(databasePath);
        var run = reloaded.GetRun("run-14")!;
        AssertEqual("prov-a", run.ProviderId ?? "", "Provider identity lost after reload.");
        AssertTrue(!string.IsNullOrWhiteSpace(run.EffectivePromptDigest), "Prompt digest lost after reload.");
        var checkpoints = reloaded.CheckpointsForRun("run-14");
        AssertTrue(checkpoints.Count > 0, "Invocation checkpoint missing.");
        foreach (var checkpoint in checkpoints)
        {
            AssertTrue(!checkpoint.PayloadJson.Contains("sk-wp14-canary-9f3a2c", StringComparison.Ordinal),
                "API secret persisted in project.db.");
            var parsed = CanonicalJson.Parse(checkpoint.PayloadJson, checkpoint.SchemaVersion);
            AssertTrue(parsed.InvocationLog.Count > 0, "Invocation log empty after reload.");
        }

        SqliteConnection.ClearAllPools();
        byte[] bytes;
        var attempts = 0;
        while (true)
        {
            try
            {
                bytes = File.ReadAllBytes(databasePath);
                break;
            }
            catch (IOException) when (attempts++ < 20)
            {
                SqliteConnection.ClearAllPools();
                Thread.Sleep(25);
            }
        }

        var haystack = System.Text.Encoding.UTF8.GetString(bytes);
        AssertTrue(!haystack.Contains("sk-wp14-canary-9f3a2c", StringComparison.Ordinal), "Canary present in DB file.");
    }

    private static void SqlitePromptProviderChangeReplans()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP14", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(databasePath, "wp14-tests", 1735689600000);
        var store = new SqliteRuntimeStore(databasePath);
        var clock = new Wp14Clock(30_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor());
        Success(scheduler.CreateWorkflowRun("wf-14b"));
        var run = Success(scheduler.CreateRun("wf-14b", "writer", null, "run-14b"));
        Success(scheduler.CreateTask(run.RunId, "write", 1, null, "task-14b"));
        store.UpdateRun(run with { PromptConfigId = "P1", EffectivePromptDigest = "E1", ProviderId = "A", ModelId = "M1" });
        var payload = CanonicalJson.WriteCheckpoint(CheckpointV1.Create(
            "plan", "d", "{}", "{}", "sum", [], [], [], [], [], [], "P1", "A", "M1", "E1"));
        Success(scheduler.PersistCheckpoint(run.RunId, "task-14b", 1, payload,
            "{\"effectivePromptDigest\":\"E1\",\"modelId\":\"M1\",\"promptConfigId\":\"P1\",\"providerId\":\"A\"}"));
        var checkpoint = store.CheckpointsForRun(run.RunId)[0];
        var continueDecision = ResumeClassifier.Classify(
            store.GetRun(run.RunId)!,
            checkpoint,
            new FreshnessInputs(null, new Dictionary<string, string>(), "P1", "E1", null, new Dictionary<string, string>(), "A", "M1", new Dictionary<string, string>(), false, false, false, false));
        AssertEqual(ResumeDecisionKind.Continue.ToString(), continueDecision.Kind.ToString(), "Unchanged identity should CONTINUE.");
        var replanPrompt = ResumeClassifier.Classify(
            store.GetRun(run.RunId)!,
            checkpoint,
            new FreshnessInputs(null, new Dictionary<string, string>(), "P2", "E1", null, new Dictionary<string, string>(), "A", "M1", new Dictionary<string, string>(), false, false, false, false));
        AssertEqual(ResumeDecisionKind.Replan.ToString(), replanPrompt.Kind.ToString(), "PromptConfig change must REPLAN.");
        var replanProvider = ResumeClassifier.Classify(
            store.GetRun(run.RunId)!,
            checkpoint,
            new FreshnessInputs(null, new Dictionary<string, string>(), "P1", "E1", null, new Dictionary<string, string>(), "B", "M1", new Dictionary<string, string>(), false, false, false, false));
        AssertEqual(ResumeDecisionKind.Replan.ToString(), replanProvider.Kind.ToString(), "Provider change must REPLAN.");
        var replanModel = ResumeClassifier.Classify(
            store.GetRun(run.RunId)!,
            checkpoint,
            new FreshnessInputs(null, new Dictionary<string, string>(), "P1", "E1", null, new Dictionary<string, string>(), "A", "M2", new Dictionary<string, string>(), false, false, false, false));
        AssertEqual(ResumeDecisionKind.Replan.ToString(), replanModel.Kind.ToString(), "Model change must REPLAN.");
    }

    private sealed class Wp14Clock(long unixMs) : ISecurityClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
    }
}
