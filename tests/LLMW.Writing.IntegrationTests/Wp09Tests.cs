using LLMW.Writing.Application.ChapterAuthority;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Authority;
using LLMW.Writing.Domain.Security;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private static void RunWp09Tests()
    {
        AgentRunAuthorizationPrecedesWp05SideEffects();
        AgentRoleMaximumsRemainEffectiveAcrossPermissionModes();
        FinalAcceptanceAuthorizationDenialLeavesWorkflowUnchanged();
        Console.WriteLine("WP09 security integration tests passed (3).");
        Console.WriteLine("PASS AgentRunAuthorizationPrecedesWp05SideEffects");
        Console.WriteLine("PASS AgentRoleMaximumsRemainEffectiveAcrossPermissionModes");
        Console.WriteLine("PASS FinalAcceptanceAuthorizationDenialLeavesWorkflowUnchanged");
    }

    private static void AgentRunAuthorizationPrecedesWp05SideEffects()
    {
        using var fixture = Wp05Fixture.Create(ChapterReviewOutcome.Pass);
        File.WriteAllText(fixture.DraftPath, "wp09 agent security");
        var writer = CreateAgentPrincipal(fixture.DatabasePath, "wp09-writer", "writer", "worker-writer", "channel-writer", RuntimePermissionMode.AutoApproveScoped);
        var reviewer = CreateAgentPrincipal(fixture.DatabasePath, "wp09-reviewer", "reviewer", "worker-reviewer", "channel-reviewer", RuntimePermissionMode.BypassPermissions);

        var deniedSubmit = fixture.Service.SubmitChapterDraft(new SubmitChapterDraftCommand(
            fixture.ChapterId,
            fixture.DraftPath,
            "wp09-reviewer-submit",
            Principal: reviewer));
        Wp09Equal(ChapterAuthorityError.CapabilityDenied, deniedSubmit.Failure?.Code,
            "Reviewer role DENY did not precede Chapter submission side effects.");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM authority_transactions;");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM candidates;");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM authority_events;");

        var submitted = Wp09Success(fixture.Service.SubmitChapterDraft(new SubmitChapterDraftCommand(
            fixture.ChapterId,
            fixture.DraftPath,
            "wp09-writer-submit",
            Principal: writer)));
        var deniedReview = fixture.Service.ReviewChapterCandidate(new ReviewChapterCandidateCommand(
            submitted.CandidateId,
            writer));
        Wp09Equal(ChapterAuthorityError.CapabilityDenied, deniedReview.Failure?.Code,
            "Writer BYPASS incorrectly gained Authority.Review.");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM review_attempts;");

        Wp09Success(fixture.Service.ReviewChapterCandidate(new ReviewChapterCandidateCommand(
            submitted.CandidateId,
            reviewer)));
        var eventsBeforeDenial = fixture.ScalarForWp09<long>("SELECT COUNT(*) FROM authority_events;");
        var deniedAccept = fixture.Service.AcceptChapterCandidate(new AcceptChapterCandidateCommand(
            submitted.CandidateId,
            "wp09-writer-submit",
            "forged-author",
            Principal: writer));
        Wp09Equal(ChapterAuthorityError.CapabilityDenied, deniedAccept.Failure?.Code,
            "AgentRun impersonated AuthorConfirmed acceptance.");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM acceptance_records;");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM manuscript_revisions;");
        fixture.AssertScalar(eventsBeforeDenial, "SELECT COUNT(*) FROM authority_events;");

        var accepted = Wp09Success(fixture.Service.AcceptChapterCandidate(new AcceptChapterCandidateCommand(
            submitted.CandidateId,
            "wp09-writer-submit",
            "test/user-interactive",
            Principal: Wp09UserPrincipal)));
        Wp09Equal(AuthorityTransactionState.Complete, accepted.TransactionState,
            "USER_INTERACTIVE AuthorConfirmed acceptance regressed.");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM acceptance_records WHERE accepted_by_kind='AUTHOR_CONFIRMED';");
    }

    private static void AgentRoleMaximumsRemainEffectiveAcrossPermissionModes()
    {
        using var fixture = Wp05Fixture.Create(ChapterReviewOutcome.Pass);
        var dataOps = CreateAgentPrincipal(fixture.DatabasePath, "wp09-dataops", "data_ops", "worker-data", "channel-data", RuntimePermissionMode.Ask);
        var researcher = CreateAgentPrincipal(fixture.DatabasePath, "wp09-researcher", "researcher", "worker-research", "channel-research", RuntimePermissionMode.BypassPermissions);
        var pm = CreateAgentPrincipal(fixture.DatabasePath, "wp09-pm", "pm", "worker-pm", "channel-pm", RuntimePermissionMode.BypassPermissions);

        var dataMutation = Wp09Authorization.Authorize(
            dataOps,
            new AuthorizationRequest(Capability.RegistryMutate));
        var researcherSpawn = Wp09Authorization.Authorize(
            researcher,
            new AuthorizationRequest(Capability.AgentSpawn));
        var pmAccept = Wp09Authorization.Authorize(
            pm,
            new AuthorizationRequest(Capability.AuthorityAccept));
        var pmGitWithoutTask = Wp09Authorization.Authorize(
            pm,
            new AuthorizationRequest(Capability.GitExecute));
        var explicitTaskAuthorization = new CoreAuthorizationService(new Wp09TestSecurityPolicySource(explicitUserTask: true));
        var pmGitWithTask = explicitTaskAuthorization.Authorize(
            pm,
            new AuthorizationRequest(Capability.GitExecute));

        Wp09Equal(CapabilityDecisionKind.Allowed, dataMutation.Decision, "DataOps Registry.Mutate maximum regressed.");
        Wp09Equal(CapabilityDecisionReason.RoleDenied, researcherSpawn.Reasons.Single(),
            "Researcher BYPASS gained Agent.Spawn.");
        Wp09Equal(CapabilityDecisionReason.NarrativeAuthorityRequired, pmAccept.Reasons.Single(),
            "PM BYPASS established Narrative Authority.");
        Wp09Equal(CapabilityDecisionReason.ExplicitUserTaskRequired, pmGitWithoutTask.Reasons.Single(),
            "PM Git did not require an explicit user task.");
        Wp09Equal(CapabilityDecisionKind.Allowed, pmGitWithTask.Decision,
            "Explicit-user-task PM Git remained incorrectly denied.");
    }

    private static void FinalAcceptanceAuthorizationDenialLeavesWorkflowUnchanged()
    {
        var policy = new CountingAcceptanceSecurityPolicySource(denyOnAcceptCall: 3);
        var authorization = new CoreAuthorizationService(policy);
        using var fixture = Wp05Fixture.Create(
            ChapterReviewOutcome.Pass,
            authorizationService: authorization);
        var draftBytes = System.Text.Encoding.UTF8.GetBytes("wp09 final authorization TOCTOU");
        File.WriteAllBytes(fixture.DraftPath, draftBytes);
        const string idempotencyKey = "wp09-final-recheck";

        var submitted = Wp09Success(fixture.Service.SubmitChapterDraft(new SubmitChapterDraftCommand(
            fixture.ChapterId,
            fixture.DraftPath,
            idempotencyKey,
            Principal: Wp09UserPrincipal)));
        Wp09Success(fixture.Service.ReviewChapterCandidate(new ReviewChapterCandidateCommand(
            submitted.CandidateId,
            Wp09UserPrincipal)));
        var baseline = CaptureAcceptanceBaseline(fixture, submitted.CandidateId, idempotencyKey);
        Wp09Equal("under_review", baseline.CandidateState, "TOCTOU baseline Candidate is not acceptance-eligible.");
        Wp09Equal("under_review", baseline.ChapterState, "TOCTOU baseline Chapter state drifted.");
        Wp09Equal("resolving", baseline.TransactionStatus, "TOCTOU baseline transaction is not resolving.");
        Wp09Equal("resolving", baseline.ProjectSubmissionState, "TOCTOU baseline project submission is not resolving.");
        Wp09Equal("pending", baseline.RecoveryState, "TOCTOU baseline recovery state drifted.");
        Wp09Equal(0L, baseline.AcceptanceCount, "TOCTOU baseline already contains Acceptance.");
        Wp09Equal(0L, baseline.RevisionCount, "TOCTOU baseline already contains a Manuscript revision.");
        Wp09Equal(0L, baseline.AuthorityEventCount, "TOCTOU baseline already contains Authority events.");
        Wp09Equal<string?>(null, baseline.CurrentPointer, "TOCTOU baseline already has a current pointer.");
        Wp09Equal<string?>(null, baseline.MaterializedDigest, "TOCTOU baseline already has materialized bytes.");

        var denied = fixture.Service.AcceptChapterCandidate(new AcceptChapterCandidateCommand(
            submitted.CandidateId,
            idempotencyKey,
            "test/user-interactive",
            Principal: Wp09UserPrincipal));

        Wp09Equal(ChapterAuthorityError.CapabilityDenied, denied.Failure?.Code,
            "Final pre-side-effect authorization denial did not stop Acceptance.");
        Wp09Equal(3, policy.AcceptAuthorizationCalls,
            "The mutable policy did not deny the final Accept recheck.");
        AssertAcceptanceBaselineUnchanged(
            baseline,
            CaptureAcceptanceBaseline(fixture, submitted.CandidateId, idempotencyKey));

        policy.AllowFutureAccept();
        var accepted = Wp09Success(fixture.Service.AcceptChapterCandidate(new AcceptChapterCandidateCommand(
            submitted.CandidateId,
            idempotencyKey,
            "test/user-interactive",
            Principal: Wp09UserPrincipal)));
        Wp09Equal(AuthorityTransactionState.Complete, accepted.TransactionState,
            "The denied Candidate could not be accepted after authorization was restored.");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM acceptance_records;");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM manuscript_revisions;");
        fixture.AssertScalar("accepted", $"SELECT status FROM candidates WHERE candidate_id='{submitted.CandidateId}';");
        fixture.AssertScalar("materialized", $"SELECT workflow_state FROM chapters WHERE chapter_id='{fixture.ChapterId}';");
        Wp09Equal(true, File.Exists(fixture.CurrentManuscriptPath),
            "Successful retry did not materialize Current Manuscript.");
        Wp09Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(draftBytes)).ToLowerInvariant(),
            FileDigest(fixture.CurrentManuscriptPath),
            "Successful retry materialized unexpected bytes.");
    }

    private static CallerPrincipal CreateAgentPrincipal(
        string databasePath,
        string runId,
        string role,
        string worker,
        string channel,
        RuntimePermissionMode permissionMode)
    {
        using (var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(databasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR IGNORE INTO workflow_runs(workflow_run_id,status,created_at_ms,updated_at_ms)
                VALUES ('wp09-workflow','running',1,1);
                INSERT INTO runs(run_id,workflow_run_id,role,status,depth,created_at_ms,updated_at_ms)
                VALUES ($run_id,'wp09-workflow',$role,'running',0,1,1);
                """;
            AddWp09(command, "$run_id", runId);
            AddWp09(command, "$role", role);
            command.ExecuteNonQuery();
        }

        var channelContext = new AuthenticatedChannelContext(
            channel,
            AuthenticatedClientKind.AgentRuntime,
            worker,
            new ProjectScope(Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab"), "integration"));
        var sessions = new RunSessionService(
            new SqliteRunSessionStore(databasePath),
            policySource: new FixedRunSecurityPolicySource(permissionMode));
        var issued = sessions.Create(new CreateRunSessionRequest(runId, channelContext, DateTimeOffset.UtcNow.AddMinutes(5)));
        if (!issued.Succeeded || issued.Value is null)
        {
            throw new InvalidOperationException($"Session issuance failed: {issued.Failure?.Code}.");
        }

        var resolved = sessions.Resolve(new ResolveRunSessionRequest(
            runId,
            issued.Value.Token.ExportOnceForAuthenticatedTransport(),
            channelContext));
        if (!resolved.Succeeded || resolved.Value is null)
        {
            throw new InvalidOperationException($"Session resolution failed: {resolved.Failure?.Code}.");
        }

        return resolved.Value;
    }

    private sealed class FixedRunSecurityPolicySource(RuntimePermissionMode permissionMode) : IRunSecurityPolicySource
    {
        public RuntimePermissionMode GetRuntimePermissionMode(string runId) => permissionMode;
    }

    private sealed class CountingAcceptanceSecurityPolicySource(int denyOnAcceptCall) : ISecurityPolicySource
    {
        private int? denyOnCall = denyOnAcceptCall;

        public int AcceptAuthorizationCalls { get; private set; }

        public SecurityPolicySnapshot Resolve(CallerPrincipal principal, Capability capability)
        {
            if (capability == Capability.AuthorityAccept)
            {
                AcceptAuthorizationCalls++;
            }

            return TrustedPolicySnapshot(
                productAllowed: capability != Capability.AuthorityAccept ||
                                denyOnCall is null ||
                                AcceptAuthorizationCalls != denyOnCall.Value);
        }

        public void AllowFutureAccept() => denyOnCall = null;
    }

    private sealed class Wp09TestSecurityPolicySource(bool explicitUserTask = false) : ISecurityPolicySource
    {
        public SecurityPolicySnapshot Resolve(CallerPrincipal principal, Capability capability) =>
            TrustedPolicySnapshot(explicitUserTask: explicitUserTask);
    }

    private static SecurityPolicySnapshot TrustedPolicySnapshot(
        bool productAllowed = true,
        bool explicitUserTask = false) =>
        new(
            ProductAllowed: productAllowed,
            ToolGranted: true,
            ExtensionGranted: true,
            ProjectTrusted: true,
            SecurityScopeClassification.InScope,
            HardDeny.None,
            NarrativeAuthorityAvailable: false,
            ExplicitUserTask: explicitUserTask);

    private sealed record AcceptanceBaseline(
        string CandidateState,
        string ChapterState,
        long AcceptanceCount,
        long RevisionCount,
        long AuthorityEventCount,
        string? CurrentPointer,
        string TransactionStatus,
        string ProjectSubmissionState,
        string RecoveryState,
        string? MaterializedDigest);

    private static AcceptanceBaseline CaptureAcceptanceBaseline(
        Wp05Fixture fixture,
        string candidateId,
        string idempotencyKey) =>
        new(
            fixture.ScalarForWp09<string>($"SELECT status FROM candidates WHERE candidate_id='{candidateId}';"),
            fixture.ScalarForWp09<string>($"SELECT workflow_state FROM chapters WHERE chapter_id='{fixture.ChapterId}';"),
            fixture.ScalarForWp09<long>("SELECT COUNT(*) FROM acceptance_records;"),
            fixture.ScalarForWp09<long>("SELECT COUNT(*) FROM manuscript_revisions;"),
            fixture.ScalarForWp09<long>("SELECT COUNT(*) FROM authority_events;"),
            NullableScalarForWp09(fixture, $"SELECT current_manuscript_revision_id FROM chapters WHERE chapter_id='{fixture.ChapterId}';"),
            fixture.ScalarForWp09<string>($"SELECT status FROM authority_transactions WHERE idempotency_key='{idempotencyKey}';"),
            fixture.ScalarForWp09<string>($"SELECT project_submission_state FROM authority_transactions WHERE idempotency_key='{idempotencyKey}';"),
            fixture.ScalarForWp09<string>($"SELECT recovery_state FROM authority_transactions WHERE idempotency_key='{idempotencyKey}';"),
            FileDigest(fixture.CurrentManuscriptPath));

    private static void AssertAcceptanceBaselineUnchanged(AcceptanceBaseline expected, AcceptanceBaseline actual)
    {
        Wp09Equal(expected.CandidateState, actual.CandidateState, "Denied Accept changed Candidate state.");
        Wp09Equal(expected.ChapterState, actual.ChapterState, "Denied Accept changed Chapter Authority state.");
        Wp09Equal(expected.AcceptanceCount, actual.AcceptanceCount, "Denied Accept created an acceptance record.");
        Wp09Equal(expected.RevisionCount, actual.RevisionCount, "Denied Accept created a manuscript revision.");
        Wp09Equal(expected.AuthorityEventCount, actual.AuthorityEventCount, "Denied Accept appended an Authority event.");
        Wp09Equal(expected.CurrentPointer, actual.CurrentPointer, "Denied Accept changed the current manuscript pointer.");
        Wp09Equal(expected.TransactionStatus, actual.TransactionStatus, "Denied Accept advanced the Authority transaction state.");
        Wp09Equal(expected.ProjectSubmissionState, actual.ProjectSubmissionState, "Denied Accept changed project_submission_state.");
        Wp09Equal(expected.RecoveryState, actual.RecoveryState, "Denied Accept created dirty/recovery state.");
        Wp09Equal(expected.MaterializedDigest, actual.MaterializedDigest, "Denied Accept changed filesystem materialization.");
    }

    private static string? NullableScalarForWp09(Wp05Fixture fixture, string sql)
    {
        using var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(fixture.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? FileDigest(string path) => File.Exists(path)
        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
        : null;

    private static void AddWp09(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static T Wp09Success<T>(ChapterAuthorityResult<T> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected WP09 success, got {result.Failure?.Code}: {result.Failure?.Detail}.");
        }

        return result.Value;
    }

    private static void Wp09Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private static T ScalarForWp09<T>(this Wp05Fixture fixture, string sql)
    {
        using var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(fixture.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
