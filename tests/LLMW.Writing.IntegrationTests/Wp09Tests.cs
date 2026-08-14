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
        Console.WriteLine("WP09 security integration tests passed (2).");
        Console.WriteLine("PASS AgentRunAuthorizationPrecedesWp05SideEffects");
        Console.WriteLine("PASS AgentRoleMaximumsRemainEffectiveAcrossPermissionModes");
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

    private sealed class Wp09TestSecurityPolicySource(bool explicitUserTask = false) : ISecurityPolicySource
    {
        public SecurityPolicySnapshot Resolve(CallerPrincipal principal, Capability capability) =>
            new(
                ProductAllowed: true,
                ToolGranted: true,
                ExtensionGranted: true,
                ProjectTrusted: true,
                SecurityScopeClassification.InScope,
                HardDeny.None,
                NarrativeAuthorityAvailable: false,
                ExplicitUserTask: explicitUserTask);
    }

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
