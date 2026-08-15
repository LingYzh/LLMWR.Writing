using System.Text.Json;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private static readonly List<string> Wp13PassedTests = [];

    private static void RunWp13Tests()
    {
        RunWp13(nameof(SqliteWp13SemanticsReloadWithoutMigration), SqliteWp13SemanticsReloadWithoutMigration);
        RunWp13(nameof(OpenProjectStillDoesNotGrantProjectTrust), OpenProjectStillDoesNotGrantProjectTrust);
        RunWp13(nameof(NoDirectSpecialistMessageCommandsExist), NoDirectSpecialistMessageCommandsExist);
        Console.WriteLine($"WP13 integration tests passed ({Wp13PassedTests.Count}).");
        foreach (var test in Wp13PassedTests)
        {
            Console.WriteLine($"PASS {test}");
        }
    }

    private static void RunWp13(string name, Action test)
    {
        test();
        Wp13PassedTests.Add(name);
    }

    private static void SqliteWp13SemanticsReloadWithoutMigration()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(databasePath, "wp13-tests", 1735689600000);
        var store = new SqliteRuntimeStore(databasePath);
        var clock = new IntegrationClock(10_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(store, scheduler, clock);
        var workflow = Success(scheduler.CreateWorkflowRun("wf-int"));
        var run = Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-int"));
        Success(scheduler.CreateTask(run.RunId, "write", 1, null, "task-int"));
        Success(wp13.SetOversightOverride(
            new SetOversightOverrideRequest("project", "proj-int", "agent_delegated", "auto_approve_scoped", null),
            Wp09UserPrincipal));
        Success(scheduler.DispatchReadyTask("task-int"));
        var paused = Success(wp13.PauseRuntimeGrill(
            run.RunId,
            "task-int",
            RuntimeGrillPauseReason.NewCreativeDecisionRequired,
            new RuntimeGrillQuestionV1("next", ["continue"], "Choose"),
            "int-base"));
        store.InsertResultArtifact(ResultArtifactCanonicalJson.ToDurable(new TaskResultArtifactV1(
            Guid.NewGuid().ToString("D"),
            "task-int",
            ResultArtifactStatus.Complete,
            "done",
            [],
            [],
            null,
            [],
            [],
            [],
            new ResultFreshnessV1(
                1,
                ResultFreshnessState.Current,
                new ResultProducedAgainstV1(null, [], null, null, null, null, [], null, null, []),
                new ResultProvenanceV1(run.RunId, "task-int", null, null, null, null, null, null)),
            null,
            10_000)));

        var reloaded = new SqliteRuntimeStore(databasePath);
        var reloadedWp13 = new Wp13RuntimeService(
            reloaded,
            new RuntimeSchedulerService(
                reloaded,
                new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
                clock,
                new FakeRunWorkerSupervisor(),
                new AllowIntegrationAuth { Allow = true }),
            clock);
        AssertWp13Equal("pending", reloaded.GetApproval(paused.ApprovalId)?.Status, "Grill pause must survive Core/Runtime restart.");
        AssertWp13Equal("complete", reloaded.GetLatestResultArtifact("task-int")?.Status, "Result Artifact must survive reload.");
        AssertWp13Equal("agent_delegated", Success(reloadedWp13.GetEffectiveOversight("proj-int", null, null)).NarrativeAuthority,
            "Oversight override must survive reload.");
        using var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(databasePath);
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        AssertWp13Equal(1L, (long)(version.ExecuteScalar() ?? 0L), "user_version must remain 1.");
        using var migrations = connection.CreateCommand();
        migrations.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
        AssertWp13Equal(1L, (long)(migrations.ExecuteScalar() ?? 0L), "schema_migrations must remain 1.");
    }

    private static void OpenProjectStillDoesNotGrantProjectTrust()
    {
        var decision = new CoreAuthorizationService().Authorize(
            Wp09UserPrincipal,
            new AuthorizationRequest(Capability.AuthorityAccept));
        AssertWp13Equal(CapabilityDecisionKind.Denied, decision.Decision, "OpenProject-equivalent FailClosed policy must not grant trust.");
        AssertTrue(decision.Reasons.Contains(CapabilityDecisionReason.ProductDenied) ||
                   decision.Reasons.Contains(CapabilityDecisionReason.TrustRequired),
            "Fail-closed production policy must still deny Authority.Accept without an explicit trust source.");
    }

    private static void NoDirectSpecialistMessageCommandsExist()
    {
        AssertTrue(!IpcSemanticTypes.IsKnown("sendMessageToSpecialist"), "sendMessageToSpecialist must not exist.");
        AssertTrue(!IpcSemanticTypes.IsKnown("appendSpecialistInstruction"), "appendSpecialistInstruction must not exist.");
        AssertTrue(!IpcSemanticTypes.IsKnown("forceSpecialistComplete"), "forceSpecialistComplete must not exist.");
        var handler = new Wp13IpcCommandHandler(
            new Wp13RuntimeService(
                new MemoryRuntimeStore(),
                new RuntimeSchedulerService(
                    new MemoryRuntimeStore(),
                    new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
                    new IntegrationClock(1),
                    new FakeRunWorkerSupervisor()),
                new IntegrationClock(1)),
            "workspace-13");
        var result = handler.HandleAsync(new IpcApplicationCommandContext(
            IpcClientKind.Ui,
            "c1",
            null,
            Wp09UserPrincipal,
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            null,
            "sendMessageToSpecialist",
            JsonDocument.Parse("{}").RootElement,
            CancellationToken.None)).GetAwaiter().GetResult();
        AssertTrue(result is null, "Unknown direct-message semantic type must not be handled.");
    }

    private static T Success<T>(RuntimeResult<T> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected success, got {result.Failure?.Code}: {result.Failure?.Detail}");
        }

        return result.Value;
    }

    private static void AssertWp13Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private sealed class IntegrationClock(long unixMs) : ISecurityClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
    }

    private sealed class AllowIntegrationAuth : IAuthorizationService
    {
        public bool Allow { get; set; }

        public CapabilityDecision Authorize(CallerPrincipal? principal, AuthorizationRequest request) =>
            new(
                request.Capability,
                principal?.Kind ?? PrincipalKind.UserInteractive,
                Allow ? CapabilityDecisionKind.Allowed : CapabilityDecisionKind.Denied,
                [Allow ? CapabilityDecisionReason.Allowed : CapabilityDecisionReason.RoleDenied],
                principal?.Role,
                Allow ? RoleCapabilityLevel.Allowed : RoleCapabilityLevel.Denied,
                SecurityScopeClassification.InScope,
                HardDenied: false);
    }
}
