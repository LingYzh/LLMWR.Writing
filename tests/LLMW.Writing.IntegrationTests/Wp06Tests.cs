using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.NarrativeChange;
using LLMW.Writing.Domain.Narrative;
using LLMW.Writing.Infrastructure.Authority;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.NarrativeChange;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private const string ObjectA = "018f3e78-1234-7abc-8def-0123456789a1";
    private const string ObjectB = "018f3e78-1234-7abc-8def-0123456789a2";
    private const string ObjectC = "018f3e78-1234-7abc-8def-0123456789a3";
    private static readonly List<string> Wp06PassedTests = [];
    private static readonly int[] ExpectedMultiOrdinals = [0, 1, 2];

    private static void RunWp06Tests()
    {
        RunWp06(nameof(WorkingChangeSetIsDurableWithoutChangingCurrentNarrative), WorkingChangeSetIsDurableWithoutChangingCurrentNarrative);
        RunWp06(nameof(FourOperationsApplyAtomicallyAndPreserveHistory), FourOperationsApplyAtomicallyAndPreserveHistory);
        RunWp06(nameof(StaleMultiObjectChangeSetReturnsPreconditionChangedWithoutPartialApply), StaleMultiObjectChangeSetReturnsPreconditionChangedWithoutPartialApply);
        RunWp06(nameof(FreshnessIsRecheckedInsideAuthorityTransaction), FreshnessIsRecheckedInsideAuthorityTransaction);
        RunWp06(nameof(StructuralDependencyTriggersAffectedAnalysisAndAtomicRevalidationMark), StructuralDependencyTriggersAffectedAnalysisAndAtomicRevalidationMark);
        RunWp06(nameof(SemanticFoundTriggersImpactWithoutStructuralEdge), SemanticFoundTriggersImpactWithoutStructuralEdge);
        RunWp06(nameof(SemanticUncertainIsDurableAndNonBlocking), SemanticUncertainIsDurableAndNonBlocking);
        RunWp06(nameof(FailedImpactIsTypedAndRetainsWorkingSet), FailedImpactIsTypedAndRetainsWorkingSet);
        RunWp06(nameof(AgentDelegatedIsRejectedBeforeAssessmentOrAuthorityMutation), AgentDelegatedIsRejectedBeforeAssessmentOrAuthorityMutation);
        RunWp06(nameof(ApplyIsIdempotentAndUsesUuidV7DurableIdentities), ApplyIsIdempotentAndUsesUuidV7DurableIdentities);
        RunWp06(nameof(PreAndPostCommitFaultsRespectAuthorityBoundary), PreAndPostCommitFaultsRespectAuthorityBoundary);

        Console.WriteLine($"WP06 integration tests passed ({Wp06PassedTests.Count}).");
        foreach (var test in Wp06PassedTests)
        {
            Console.WriteLine($"PASS {test}");
        }
    }

    private static void WorkingChangeSetIsDurableWithoutChangingCurrentNarrative()
    {
        using var fixture = Wp06Fixture.Create();
        var baseline = fixture.SeedCurrent(ObjectA, "A v1");
        var created = Wp06Success(fixture.Service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
            "storyline", "storyline-1", "author", "author-1",
            [Modify(ObjectA, baseline, "A proposal")])));

        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM narrative_change_sets;");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM narrative_changes;");
        fixture.AssertScalar("working", $"SELECT status FROM narrative_change_sets WHERE change_set_id='{created.ChangeSetId}';");
        fixture.AssertScalar("current", $"SELECT status FROM objects WHERE object_id='{ObjectA}';");
        fixture.AssertScalar(1L, $"SELECT revision_no FROM objects WHERE object_id='{ObjectA}';");
        fixture.AssertScalar(1L, $"SELECT COUNT(*) FROM narrative_state_revisions WHERE scope_object_id='{ObjectA}';");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM authority_events;");
        var proposedDigest = fixture.Scalar<string>(
            $"SELECT after_payload_digest FROM narrative_changes WHERE change_set_id='{created.ChangeSetId}';");
        AssertWp06True(fixture.BlobStore.Verify(proposedDigest), "Working payload was not staged in the immutable blob store.");
        AssertWp06Equal(Hash("A proposal"), proposedDigest, "Working payload digest is not immutable content addressing.");
    }

    private static void FourOperationsApplyAtomicallyAndPreserveHistory()
    {
        using var fixture = Wp06Fixture.Create();
        var a = fixture.SeedCurrent(ObjectA, "A v1");
        var b = fixture.SeedCurrent(ObjectB, "B v1");
        var created = Wp06Success(fixture.Service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
            "storyline", "storyline-1", "author", "author-1",
            [
                Modify(ObjectA, a, "A v2"),
                Remove(ObjectB, b),
                Add(ObjectC, "character", "C v1")
            ])));

        var applied = Wp06Success(fixture.Service.Apply(Apply(created.ChangeSetId, "multi-success")));
        AssertWp06Equal(AuthorityTransactionState.Complete, applied.TransactionState, "Multi-object Apply did not complete.");
        AssertWp06Equal(0, fixture.Impact.Calls, "No-dependency path unexpectedly called the impact analyzer.");
        fixture.AssertScalar("no_relevant_dependency", "SELECT status FROM impact_analyses;");
        fixture.AssertScalar("applied", $"SELECT status FROM narrative_change_sets WHERE change_set_id='{created.ChangeSetId}';");
        fixture.AssertScalar(applied.TransactionId, $"SELECT transaction_id FROM narrative_change_sets WHERE change_set_id='{created.ChangeSetId}';");
        fixture.AssertScalar("current", $"SELECT status FROM objects WHERE object_id='{ObjectA}';");
        fixture.AssertScalar(2L, $"SELECT revision_no FROM objects WHERE object_id='{ObjectA}';");
        fixture.AssertScalar("removed", $"SELECT status FROM objects WHERE object_id='{ObjectB}';");
        fixture.AssertScalar(2L, $"SELECT revision_no FROM objects WHERE object_id='{ObjectB}';");
        fixture.AssertScalar(1L, $"SELECT COUNT(*) FROM objects WHERE object_id='{ObjectB}';");
        fixture.AssertScalar(1L, $"SELECT COUNT(*) FROM narrative_state_revisions WHERE scope_object_id='{ObjectB}';");
        fixture.AssertScalar("current", $"SELECT status FROM objects WHERE object_id='{ObjectC}';");
        fixture.AssertScalar(1L, $"SELECT revision_no FROM objects WHERE object_id='{ObjectC}';");
        fixture.AssertScalar(2L, $"SELECT COUNT(*) FROM narrative_state_revisions WHERE scope_object_id='{ObjectA}';");
        fixture.AssertScalar(1L, $"SELECT COUNT(*) FROM narrative_state_revisions WHERE scope_object_id='{ObjectC}';");
        fixture.AssertScalar(1L, $"SELECT COUNT(DISTINCT transaction_id) FROM authority_events WHERE transaction_id='{applied.TransactionId}';");
        fixture.AssertScalar(4L, $"SELECT COUNT(*) FROM authority_events WHERE transaction_id='{applied.TransactionId}';");
        AssertWp06Sequence(ExpectedMultiOrdinals, fixture.ReadOrdinals(created.ChangeSetId), "Narrative change ordinals were not durable and stable.");

        var removedState = fixture.ReadCurrentState(ObjectB);
        var reintroduced = Wp06Success(fixture.Service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
            "storyline", "storyline-1", "author", "author-1",
            [Reintroduce(ObjectB, removedState, "B returned")])));
        Wp06Success(fixture.Service.Apply(Apply(reintroduced.ChangeSetId, "reintroduce")));
        fixture.AssertScalar("current", $"SELECT status FROM objects WHERE object_id='{ObjectB}';");
        fixture.AssertScalar(3L, $"SELECT revision_no FROM objects WHERE object_id='{ObjectB}';");
        fixture.AssertScalar(2L, $"SELECT COUNT(*) FROM narrative_state_revisions WHERE scope_object_id='{ObjectB}';");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM authority_events WHERE event_type='narrative_object.reintroduced';");
    }

    private static void StaleMultiObjectChangeSetReturnsPreconditionChangedWithoutPartialApply()
    {
        using var fixture = Wp06Fixture.Create();
        var a = fixture.SeedCurrent(ObjectA, "A v1");
        var b = fixture.SeedCurrent(ObjectB, "B v1");
        var created = Wp06Success(fixture.Service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
            "storyline", "storyline-1", "author", "author-1",
            [Modify(ObjectA, a, "A v2"), Modify(ObjectB, b, "B proposal")])));
        fixture.ExternalAuthorityModify(ObjectB, "B v2 elsewhere");

        var result = fixture.Service.Apply(Apply(created.ChangeSetId, "stale-set"));
        Wp06Failure(NarrativeChangeError.PreconditionChanged, result.Failure);
        AssertWp06Equal(0, fixture.Semantic.Calls, "Stale Apply invoked semantic dependency assessment.");
        AssertWp06Equal(0, fixture.Impact.Calls, "Stale Apply invoked impact analysis.");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM impact_analyses;");
        fixture.AssertScalar(0L, $"SELECT COUNT(*) FROM narrative_change_sets WHERE change_set_id='{created.ChangeSetId}' AND impact_analysis_id IS NOT NULL;");
        AssertWp06Equal(Hash("A v1"), fixture.ReadCurrentState(ObjectA).Digest, "A was partially applied before the stale B check.");
        AssertWp06Equal(Hash("B v2 elsewhere"), fixture.ReadCurrentState(ObjectB).Digest, "Current baseline B was not preserved.");
        fixture.AssertScalar(1L, $"SELECT revision_no FROM objects WHERE object_id='{ObjectA}';");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM authority_events;");
        fixture.AssertScalar(0L, $"SELECT COUNT(*) FROM narrative_change_sets WHERE change_set_id='{created.ChangeSetId}' AND transaction_id IS NOT NULL;");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM authority_transactions WHERE transaction_kind='narrative_change_apply';");
    }

    private static void FreshnessIsRecheckedInsideAuthorityTransaction()
    {
        var race = new Wp06ActionFaultInjector(AuthorityTransactionFaultPoint.BeforeSqliteTransaction);
        using var fixture = Wp06Fixture.Create(race);
        var a = fixture.SeedCurrent(ObjectA, "A v1");
        var created = Wp06Success(fixture.Service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
            "storyline", "storyline-1", "author", "author-1", [Modify(ObjectA, a, "A proposal")] )));
        race.Action = () => fixture.ExternalAuthorityModify(ObjectA, "A v2 elsewhere");

        var result = fixture.Service.Apply(Apply(created.ChangeSetId, "freshness-race"));
        Wp06Failure(NarrativeChangeError.PreconditionChanged, result.Failure);
        AssertWp06Equal(1, race.Calls, "The between-checks mutation hook was not invoked.");
        AssertWp06Equal(1, fixture.Semantic.Calls, "Fresh Apply did not reach dependency assessment before the race.");
        fixture.AssertScalar(Hash("A v2 elsewhere"),
            "SELECT snapshot_digest FROM narrative_state_revisions WHERE scope_object_id='018f3e78-1234-7abc-8def-0123456789a1' ORDER BY created_at_ms DESC,state_revision_id DESC LIMIT 1;");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM authority_events;");
        fixture.AssertScalar(0L, $"SELECT COUNT(*) FROM narrative_change_sets WHERE change_set_id='{created.ChangeSetId}' AND transaction_id IS NOT NULL;");
        fixture.AssertScalar("failed", "SELECT status FROM authority_transactions WHERE transaction_kind='narrative_change_apply';");
    }

    private static void StructuralDependencyTriggersAffectedAnalysisAndAtomicRevalidationMark()
    {
        using var fixture = Wp06Fixture.Create();
        var a = fixture.SeedCurrent(ObjectA, "A v1");
        fixture.SeedCurrent(ObjectB, "B v1");
        fixture.AddDependencyEdge("018f3e78-1234-7abc-8def-0123456789d1", ObjectA, ObjectB);
        fixture.Impact.Next = AffectedImpact(ObjectB);
        var created = Wp06Success(fixture.Service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
            "storyline", "storyline-1", "author", "author-1", [Modify(ObjectA, a, "A v2")] )));

        Wp06Success(fixture.Service.Apply(Apply(created.ChangeSetId, "structural-impact")));
        AssertWp06Equal(1, fixture.Impact.Calls, "Structural dependency did not invoke impact analysis.");
        fixture.AssertScalar("affected", "SELECT status FROM impact_analyses;");
        fixture.AssertScalar("needs_revalidation", "SELECT validation_status FROM dependency_edges;");
        var affectedSet = fixture.Scalar<string>("SELECT affected_set_json FROM impact_analyses;");
        AssertWp06True(affectedSet.Contains(ObjectB, StringComparison.Ordinal), "Affected Object was not persisted.");
        AssertWp06True(affectedSet.Contains("018f3e78-1234-7abc-8def-0123456789d1", StringComparison.Ordinal), "Affected Edge was not persisted.");
    }

    private static void SemanticFoundTriggersImpactWithoutStructuralEdge()
    {
        using var fixture = Wp06Fixture.Create();
        var a = fixture.SeedCurrent(ObjectA, "A v1");
        fixture.Semantic.Next = new SemanticDependencyAssessment(SemanticDependencyFinding.Found, "{\"evidence\":\"semantic\"}");
        fixture.Impact.Next = AffectedImpact();
        var created = Wp06Success(fixture.Service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
            "storyline", "storyline-1", "author", "author-1", [Modify(ObjectA, a, "A v2")] )));

        Wp06Success(fixture.Service.Apply(Apply(created.ChangeSetId, "semantic-found")));
        AssertWp06Equal(1, fixture.Impact.Calls, "Semantic FOUND took the no-dependency fast path.");
        fixture.AssertScalar("affected", "SELECT status FROM impact_analyses;");
    }

    private static void SemanticUncertainIsDurableAndNonBlocking()
    {
        using var fixture = Wp06Fixture.Create();
        var a = fixture.SeedCurrent(ObjectA, "A v1");
        fixture.Semantic.Next = new SemanticDependencyAssessment(
            SemanticDependencyFinding.Uncertain,
            "{\"evidence\":\"partial\"}",
            "candidate retrieval did not cover the final arc",
            "chapters=1-3; final-arc=not-covered");
        var created = Wp06Success(fixture.Service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
            "storyline", "storyline-1", "author", "author-1", [Modify(ObjectA, a, "A v2")] )));

        var applied = Wp06Success(fixture.Service.Apply(Apply(created.ChangeSetId, "semantic-uncertain")));
        AssertWp06Equal(0, fixture.Impact.Calls, "UNCERTAIN without a relevant dependency called heavy impact analysis.");
        AssertWp06True(applied.Warnings.Count == 1 && applied.Warnings[0].Contains("UNCERTAIN", StringComparison.Ordinal),
            "Apply result did not expose the durable uncertainty warning.");
        fixture.AssertScalar("uncertain", "SELECT status FROM impact_analyses;");
        var warnings = fixture.Scalar<string>("SELECT warnings_json FROM impact_analyses;");
        AssertWp06True(warnings.Contains("final arc", StringComparison.Ordinal), "UNCERTAIN reason was not persisted.");
        fixture.AssertScalar("current", $"SELECT status FROM objects WHERE object_id='{ObjectA}';");
        fixture.AssertScalar(2L, $"SELECT revision_no FROM objects WHERE object_id='{ObjectA}';");
        fixture.AssertScalar(2L, "SELECT COUNT(*) FROM authority_events;");
    }

    private static void FailedImpactIsTypedAndRetainsWorkingSet()
    {
        using var fixture = Wp06Fixture.Create();
        var a = fixture.SeedCurrent(ObjectA, "A v1");
        fixture.Semantic.Next = new SemanticDependencyAssessment(SemanticDependencyFinding.Found, "{\"evidence\":\"semantic\"}");
        fixture.Impact.Next = new NarrativeImpactAnalysisResult(
            NarrativeImpactAnalysisStatus.Failed, [], [], "{\"failure\":\"fake\"}", []);
        var created = Wp06Success(fixture.Service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
            "storyline", "storyline-1", "author", "author-1", [Modify(ObjectA, a, "A v2")] )));

        var result = fixture.Service.Apply(Apply(created.ChangeSetId, "impact-failed"));
        Wp06Failure(NarrativeChangeError.ImpactAnalysisFailed, result.Failure);
        fixture.AssertScalar("working", $"SELECT status FROM narrative_change_sets WHERE change_set_id='{created.ChangeSetId}';");
        fixture.AssertScalar("failed", "SELECT status FROM impact_analyses;");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM authority_transactions WHERE transaction_kind='narrative_change_apply';");
        AssertWp06Equal(Hash("A v1"), fixture.ReadCurrentState(ObjectA).Digest, "Failed impact changed Current Narrative.");
    }

    private static void AgentDelegatedIsRejectedBeforeAssessmentOrAuthorityMutation()
    {
        using var fixture = Wp06Fixture.Create();
        var created = Wp06Success(fixture.Service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
            "storyline", "storyline-1", "agent", "agent-1", [Add(ObjectA, "character", "A proposal")] )));

        var result = fixture.Service.Apply(new ApplyNarrativeChangeSetCommand(
            created.ChangeSetId,
            "delegated-not-yet-available",
            NarrativeDecisionKind.AgentDelegated,
            "agent-1"));
        Wp06Failure(NarrativeChangeError.DecisionAuthorityNotAvailable, result.Failure);
        AssertWp06Equal(0, fixture.Semantic.Calls, "Rejected delegated Apply invoked semantic assessment.");
        AssertWp06Equal(0, fixture.Impact.Calls, "Rejected delegated Apply invoked impact analysis.");
        fixture.AssertScalar("working", $"SELECT status FROM narrative_change_sets WHERE change_set_id='{created.ChangeSetId}';");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM impact_analyses;");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM authority_transactions;");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM narrative_state_revisions;");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM authority_events;");
        fixture.AssertScalar("proposed", $"SELECT status FROM objects WHERE object_id='{ObjectA}';");
    }

    private static void ApplyIsIdempotentAndUsesUuidV7DurableIdentities()
    {
        using var fixture = Wp06Fixture.Create();
        var created = Wp06Success(fixture.Service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
            "storyline", "storyline-1", "author", "author-1", [Add(ObjectA, "character", "A v1")] )));

        var first = Wp06Success(fixture.Service.Apply(Apply(created.ChangeSetId, "idempotent")));
        var retry = Wp06Success(fixture.Service.Apply(Apply(created.ChangeSetId, "idempotent")));
        AssertWp06Equal(first.TransactionId, retry.TransactionId, "Idempotent Apply created a second logical transaction.");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM authority_transactions;");
        fixture.AssertScalar(1L, $"SELECT COUNT(*) FROM narrative_state_revisions WHERE scope_object_id='{ObjectA}';");
        fixture.AssertScalar(2L, "SELECT COUNT(*) FROM authority_events;");
        fixture.AssertScalar(1L, $"SELECT revision_no FROM objects WHERE object_id='{ObjectA}';");
        AssertUuidV7(created.ChangeSetId, "Working change set ID");
        AssertUuidV7(fixture.Scalar<string>("SELECT narrative_change_id FROM narrative_changes;"), "Narrative change ID");
        AssertUuidV7(fixture.Scalar<string>("SELECT impact_analysis_id FROM impact_analyses;"), "Impact analysis ID");
        AssertUuidV7(fixture.Scalar<string>("SELECT state_revision_id FROM narrative_state_revisions;"), "Narrative state revision ID");
        AssertUuidV7(fixture.Scalar<string>("SELECT transaction_id FROM authority_transactions;"), "Authority transaction ID");
        foreach (var eventId in fixture.ReadColumn("SELECT event_id FROM authority_events ORDER BY event_seq;"))
        {
            AssertUuidV7(eventId, "Authority event ID");
        }
    }

    private static void PreAndPostCommitFaultsRespectAuthorityBoundary()
    {
        var preCommitFault = new Wp06FaultInjector(AuthorityTransactionFaultPoint.BeforeSqliteCommit);
        using (var fixture = Wp06Fixture.Create(preCommitFault))
        {
            var created = Wp06Success(fixture.Service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
                "storyline", "storyline-1", "author", "author-1", [Add(ObjectA, "character", "A v1")] )));
            var interrupted = fixture.Service.Apply(Apply(created.ChangeSetId, "pre-commit"));
            Wp06Failure(NarrativeChangeError.InfrastructureFailure, interrupted.Failure);
            fixture.AssertScalar("proposed", $"SELECT status FROM objects WHERE object_id='{ObjectA}';");
            fixture.AssertScalar(0L, $"SELECT COUNT(*) FROM narrative_state_revisions WHERE scope_object_id='{ObjectA}';");
            fixture.AssertScalar(0L, "SELECT COUNT(*) FROM authority_events;");
            fixture.AssertScalar("working", $"SELECT status FROM narrative_change_sets WHERE change_set_id='{created.ChangeSetId}';");

            preCommitFault.Enabled = false;
            Wp06Success(fixture.Service.Apply(Apply(created.ChangeSetId, "pre-commit")));
            fixture.AssertScalar("current", $"SELECT status FROM objects WHERE object_id='{ObjectA}';");
        }

        var postCommitFault = new Wp06FaultInjector(AuthorityTransactionFaultPoint.AfterSqliteCommit);
        using (var fixture = Wp06Fixture.Create(postCommitFault))
        {
            var created = Wp06Success(fixture.Service.CreateWorkingChangeSet(new CreateWorkingNarrativeChangeSetCommand(
                "storyline", "storyline-1", "author", "author-1", [Add(ObjectA, "character", "A v1")] )));
            var interrupted = fixture.Service.Apply(Apply(created.ChangeSetId, "post-commit"));
            Wp06Failure(NarrativeChangeError.AuthorityDirty, interrupted.Failure);
            fixture.AssertScalar("current", $"SELECT status FROM objects WHERE object_id='{ObjectA}';");
            fixture.AssertScalar(1L, $"SELECT COUNT(*) FROM narrative_state_revisions WHERE scope_object_id='{ObjectA}';");
            fixture.AssertScalar("applied", $"SELECT status FROM narrative_change_sets WHERE change_set_id='{created.ChangeSetId}';");
            fixture.AssertScalar(2L, "SELECT COUNT(*) FROM authority_events;");
            fixture.AssertScalar("revalidating", "SELECT status FROM authority_transactions;");

            postCommitFault.Enabled = false;
            var recovered = Wp06Success(fixture.Service.Apply(Apply(created.ChangeSetId, "post-commit")));
            AssertWp06Equal(AuthorityTransactionState.Complete, recovered.TransactionState, "Post-commit retry did not recover to COMPLETE.");
            fixture.AssertScalar("complete", "SELECT status FROM authority_transactions;");
            fixture.AssertScalar(2L, "SELECT COUNT(*) FROM authority_events;");
        }
    }

    private static WorkingNarrativeChangeInput Add(string objectId, string objectType, string payload) =>
        new(objectId, objectType, NarrativeChangeKind.Add, AfterPayload: Payload(payload));

    private static WorkingNarrativeChangeInput Modify(string objectId, Wp06State state, string payload) =>
        new(objectId, "character", NarrativeChangeKind.Modify, state.StateRevisionId, state.Digest, Payload(payload));

    private static WorkingNarrativeChangeInput Remove(string objectId, Wp06State state) =>
        new(objectId, "character", NarrativeChangeKind.Remove, state.StateRevisionId, state.Digest);

    private static WorkingNarrativeChangeInput Reintroduce(string objectId, Wp06State state, string payload) =>
        new(objectId, "character", NarrativeChangeKind.Reintroduce, state.StateRevisionId, state.Digest, Payload(payload));

    private static ApplyNarrativeChangeSetCommand Apply(string changeSetId, string idempotencyKey) =>
        new(changeSetId, idempotencyKey, NarrativeDecisionKind.AuthorConfirmed, "author-1");

    private static MemoryStream Payload(string value) => new(Encoding.UTF8.GetBytes(value));

    private static NarrativeImpactAnalysisResult AffectedImpact(params string[] objectIds) =>
        new(NarrativeImpactAnalysisStatus.Affected, objectIds, [], "{\"impact\":\"fake\"}", []);

    private static T Wp06Success<T>(NarrativeChangeResult<T> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected WP06 success, received {result.Failure?.Code}: {result.Failure?.Detail}");
        }

        return result.Value;
    }

    private static void Wp06Failure(NarrativeChangeError expected, NarrativeChangeFailure? failure)
    {
        if (failure is null)
        {
            throw new InvalidOperationException($"Expected WP06 failure {expected}, but the operation succeeded.");
        }

        AssertWp06Equal(expected, failure.Code, "WP06 typed failure code changed.");
    }

    private static void RunWp06(string name, Action test)
    {
        test();
        Wp06PassedTests.Add(name);
    }

    private static void AssertWp06Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private static void AssertWp06True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertWp06Sequence(IReadOnlyList<int> expected, IReadOnlyList<int> actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"{message} Expected: {string.Join(',', expected)}; actual: {string.Join(',', actual)}.");
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void AssertUuidV7(string value, string identityName)
    {
        if (!Guid.TryParseExact(value, "D", out var id))
        {
            throw new InvalidOperationException($"{identityName} is not a canonical UUID.");
        }

        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        AssertWp06Equal(7, bytes[6] >> 4, $"{identityName} must use UUIDv7.");
    }

    private sealed class Wp06Fixture : IDisposable
    {
        private Wp06Fixture(string directory, string databasePath, ImmutableBlobStore blobStore, SemanticFake semantic, ImpactFake impact, NarrativeChangeService service)
        {
            Directory = directory;
            DatabasePath = databasePath;
            BlobStore = blobStore;
            Semantic = semantic;
            Impact = impact;
            Service = service;
        }

        public string Directory { get; }

        public string DatabasePath { get; }

        public ImmutableBlobStore BlobStore { get; }

        public SemanticFake Semantic { get; }

        public ImpactFake Impact { get; }

        public NarrativeChangeService Service { get; }

        public static Wp06Fixture Create(ITransactionFaultInjector? faultInjector = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP06", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "project.db");
            new SqliteMigrationRunner().Migrate(databasePath, "wp06-tests");
            var blobStore = new ImmutableBlobStore(directory);
            var coordinator = new AuthorityTransactionCoordinator(
                databasePath,
                blobStore,
                new AtomicAuthorityMaterializer(directory, blobStore),
                faultInjector: faultInjector);
            var store = new SqliteNarrativeChangeStore(databasePath, blobStore, coordinator);
            var semantic = new SemanticFake();
            var impact = new ImpactFake();
            var service = new NarrativeChangeService(
                blobStore,
                store,
                semantic,
                impact,
                LLMW.Writing.Application.Reconcile.NoOpAuthoritySurfaceHealthGate.Instance);
            return new Wp06Fixture(directory, databasePath, blobStore, semantic, impact, service);
        }

        public Wp06State SeedCurrent(string objectId, string content)
        {
            var digest = Stage(content);
            var transactionId = "seed-" + Guid.NewGuid().ToString("N");
            var stateRevisionId = CreateFixtureUuidV7();
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction,
                "INSERT INTO authority_transactions(transaction_id,transaction_kind,idempotency_key,project_submission_state,status,recovery_state,started_at_ms,committed_at_ms,completed_at_ms) VALUES($id,'seed',$key,'idle','complete','none',1,1,1);",
                ("$id", transactionId), ("$key", transactionId));
            Execute(connection, transaction,
                "INSERT INTO objects(object_id,object_type,schema_version,revision_no,status,created_at_ms,updated_at_ms) VALUES($id,'character',1,1,'current',1,1);",
                ("$id", objectId));
            Execute(connection, transaction,
                "INSERT INTO narrative_state_revisions(state_revision_id,scope_object_id,transaction_id,snapshot_digest,created_at_ms) VALUES($state,$object,$transaction,$digest,1);",
                ("$state", stateRevisionId), ("$object", objectId), ("$transaction", transactionId), ("$digest", digest));
            transaction.Commit();
            return new Wp06State(stateRevisionId, digest);
        }

        public void ExternalAuthorityModify(string objectId, string content)
        {
            var old = ReadCurrentState(objectId);
            var digest = Stage(content);
            var transactionId = "external-" + Guid.NewGuid().ToString("N");
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction,
                "INSERT INTO authority_transactions(transaction_id,transaction_kind,idempotency_key,project_submission_state,status,recovery_state,started_at_ms,committed_at_ms,completed_at_ms) VALUES($id,'external',$key,'idle','complete','none',2,2,2);",
                ("$id", transactionId), ("$key", transactionId));
            Execute(connection, transaction,
                "UPDATE objects SET revision_no=revision_no+1,updated_at_ms=2 WHERE object_id=$id;",
                ("$id", objectId));
            Execute(connection, transaction,
                "INSERT INTO narrative_state_revisions(state_revision_id,scope_object_id,transaction_id,snapshot_digest,supersedes_state_revision_id,created_at_ms) VALUES($state,$object,$transaction,$digest,$previous,2);",
                ("$state", CreateFixtureUuidV7()), ("$object", objectId), ("$transaction", transactionId), ("$digest", digest), ("$previous", old.StateRevisionId));
            transaction.Commit();
        }

        public void AddDependencyEdge(string edgeId, string fromObjectId, string toObjectId)
        {
            using var connection = Open();
            Execute(connection, null,
                "INSERT INTO dependency_edges(edge_id,from_object_id,to_object_id,edge_type,validation_status,created_at_ms,updated_at_ms) VALUES($id,$from,$to,'canon_reference','valid',1,1);",
                ("$id", edgeId), ("$from", fromObjectId), ("$to", toObjectId));
        }

        public Wp06State ReadCurrentState(string objectId)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT state_revision_id,snapshot_digest
                FROM narrative_state_revisions current_state
                WHERE current_state.scope_object_id=$object_id
                  AND NOT EXISTS(SELECT 1 FROM narrative_state_revisions successor
                                 WHERE successor.supersedes_state_revision_id=current_state.state_revision_id)
                ORDER BY created_at_ms DESC,state_revision_id DESC LIMIT 1;
                """;
            Add(command, "$object_id", objectId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException($"No state revision exists for {objectId}.");
            }

            return new Wp06State(reader.GetString(0), reader.GetString(1));
        }

        public T Scalar<T>(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = command.ExecuteScalar();
            return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }

        public void AssertScalar<T>(T expected, string sql)
        {
            AssertWp06Equal(expected, Scalar<T>(sql), $"SQL assertion failed: {sql}");
        }

        public List<int> ReadOrdinals(string changeSetId)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ordinal FROM narrative_changes WHERE change_set_id=$id ORDER BY ordinal;";
            Add(command, "$id", changeSetId);
            List<int> values = [];
            using var reader = command.ExecuteReader();
            while (reader.Read()) values.Add(reader.GetInt32(0));
            return values;
        }

        public List<string> ReadColumn(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            List<string> values = [];
            using var reader = command.ExecuteReader();
            while (reader.Read()) values.Add(reader.GetString(0));
            return values;
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }

        private string Stage(string content)
        {
            using var stream = Payload(content);
            return BlobStore.Stage(stream).Digest;
        }

        private DbConnection Open() => new SqliteDatabaseConnectionFactory().OpenConfigured(DatabasePath);

        private static string CreateFixtureUuidV7()
        {
            Span<byte> bytes = stackalloc byte[16];
            RandomNumberGenerator.Fill(bytes);
            bytes[6] = (byte)((bytes[6] & 0x0f) | 0x70);
            bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
            return new Guid(bytes, bigEndian: true).ToString();
        }

        private static void Execute(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
            command.ExecuteNonQuery();
        }

        private static void Add(DbCommand command, string name, object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }

    private sealed class SemanticFake : ISemanticDependencyAssessor
    {
        public SemanticDependencyAssessment Next { get; set; } =
            new(SemanticDependencyFinding.NoEvidenceFound, "{\"evidence\":\"none\"}");

        public int Calls { get; private set; }

        public SemanticDependencyAssessment Assess(NarrativeChangeSetSnapshot changeSet, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Next;
        }
    }

    private sealed class ImpactFake : INarrativeImpactAnalyzer
    {
        public NarrativeImpactAnalysisResult Next { get; set; } = AffectedImpact();

        public int Calls { get; private set; }

        public NarrativeImpactAnalysisResult Analyze(
            NarrativeChangeSetSnapshot changeSet,
            StructuralDependencyAssessment structuralAssessment,
            SemanticDependencyAssessment semanticAssessment,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Next;
        }
    }

    private sealed class Wp06FaultInjector : ITransactionFaultInjector
    {
        private readonly AuthorityTransactionFaultPoint point;

        public Wp06FaultInjector(AuthorityTransactionFaultPoint point)
        {
            this.point = point;
        }

        public bool Enabled { get; set; } = true;

        public void Inject(AuthorityTransactionFaultPoint observed)
        {
            if (Enabled && observed == point)
            {
                throw new InvalidOperationException($"Injected WP06 fault at {point}.");
            }
        }
    }

    private sealed class Wp06ActionFaultInjector : ITransactionFaultInjector
    {
        private readonly AuthorityTransactionFaultPoint point;

        public Wp06ActionFaultInjector(AuthorityTransactionFaultPoint point)
        {
            this.point = point;
        }

        public Action? Action { get; set; }

        public int Calls { get; private set; }

        public void Inject(AuthorityTransactionFaultPoint observed)
        {
            if (observed != point || Action is null)
            {
                return;
            }

            Calls++;
            Action();
        }
    }

    private sealed record Wp06State(string StateRevisionId, string Digest);
}
