using System.Security.Cryptography;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Security;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private static readonly Guid Wp09ProjectId = Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab");

    private static void RunWp09InfrastructureTests()
    {
        Run(nameof(RunSessionUsesOpaque256BitSecretAndHashOnlyPersistence), RunSessionUsesOpaque256BitSecretAndHashOnlyPersistence);
        Run(nameof(RunSessionValidatesEveryBindingAndAuthoritativeRole), RunSessionValidatesEveryBindingAndAuthoritativeRole);
        Run(nameof(RunSessionExpiryRevocationAndReissueFailClosed), RunSessionExpiryRevocationAndReissueFailClosed);
        Run(nameof(RunSessionUnknownOrMissingRunFailsClosed), RunSessionUnknownOrMissingRunFailsClosed);
    }

    private static void RunSessionUsesOpaque256BitSecretAndHashOnlyPersistence()
    {
        using var database = MigratedDatabase.Create();
        SeedRun(database.Path, "run-wp09-token", "writer");
        var clock = new MutableSecurityClock(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        var service = new RunSessionService(new SqliteRunSessionStore(database.Path), clock);
        var first = SessionSuccess(service.Create(new CreateRunSessionRequest(
            "run-wp09-token", Channel("worker-a", "channel-a", "workspace-a"), clock.UtcNow.AddMinutes(5))));
        var second = SessionSuccess(service.Create(new CreateRunSessionRequest(
            "run-wp09-token", Channel("worker-b", "channel-b", "workspace-a"), clock.UtcNow.AddMinutes(5))));

        var firstToken = first.Token.ExportOnceForAuthenticatedTransport();
        var secondToken = second.Token.ExportOnceForAuthenticatedTransport();
        var decoded = DecodeBase64Url(firstToken);
        Wp09AssertEqual(RunSessionService.TokenByteLength, decoded.Length, "RunSession secret is not 256 bits.");
        AssertFalse(StringComparer.Ordinal.Equals(firstToken, secondToken), "Two session issuances reused an opaque secret.");
        Wp09AssertEqual("[REDACTED RUN SESSION TOKEN]", first.Token.ToString(), "Token ToString leaked plaintext.");
        AssertThrows<InvalidOperationException>(() => first.Token.ExportOnceForAuthenticatedTransport(),
            "RunSession plaintext token could be exported more than once.");
        AssertTrue(Guid.TryParse(first.HandleId, out _), "handle_id is not a canonical UUID string.");
        Wp09AssertEqual('7', first.HandleId[14], "handle_id is not UUIDv7.");

        using var connection = OpenConfigured(database.Path);
        var persisted = Scalar<string>(connection,
            $"SELECT token_hash FROM run_session_handles WHERE handle_id='{first.HandleId}';");
        var expectedHash = Convert.ToHexString(SHA256.HashData(decoded)).ToLowerInvariant();
        Wp09AssertEqual(expectedHash, persisted, "Database token_hash is not SHA-256 of the returned token.");
        AssertFalse(StringComparer.Ordinal.Equals(firstToken, persisted), "Database persisted the plaintext session token.");
        Wp09AssertEqual(0L, Scalar<long>(connection,
            $"SELECT COUNT(*) FROM run_session_handles WHERE token_hash='{firstToken}';"),
            "Plaintext token appeared in the run_session_handles storage column.");
        AssertTokenAbsentFromEveryDatabaseTextField(connection, firstToken);
    }

    private static void RunSessionValidatesEveryBindingAndAuthoritativeRole()
    {
        using var database = MigratedDatabase.Create();
        SeedRun(database.Path, "run-wp09-binding", "writer");
        SeedRun(database.Path, "run-wp09-other", "reviewer");
        var clock = new MutableSecurityClock(DateTimeOffset.FromUnixTimeMilliseconds(20_000));
        var service = new RunSessionService(new SqliteRunSessionStore(database.Path), clock);
        var channel = Channel("worker-a", "channel-a", "workspace-a");
        var issued = SessionSuccess(service.Create(new CreateRunSessionRequest(
            "run-wp09-binding", channel, clock.UtcNow.AddMinutes(5))));
        var token = issued.Token.ExportOnceForAuthenticatedTransport();

        var valid = SessionSuccess(service.Resolve(new ResolveRunSessionRequest("run-wp09-binding", token, channel)));
        Wp09AssertEqual(PrincipalKind.AgentRun, valid.Kind, "Valid session did not resolve an AGENT_RUN principal.");
        Wp09AssertEqual(AgentRole.Writer, valid.Role!.Value, "Durable Writer role was not loaded.");
        Wp09AssertEqual<RunSessionError?>(RunSessionError.InvalidRunSession,
            service.Resolve(new ResolveRunSessionRequest("run-wp09-binding", WrongToken(), channel)).Failure?.Code,
            "Random wrong token did not fail closed.");
        AssertBindingMismatch(service, token, "run-wp09-other", channel, "Cross-run session spoof succeeded.");
        AssertBindingMismatch(service, token, "run-wp09-binding", Channel("worker-b", "channel-a", "workspace-a"), "Wrong worker binding succeeded.");
        AssertBindingMismatch(service, token, "run-wp09-binding", Channel("worker-a", "channel-b", "workspace-a"), "Wrong channel binding succeeded.");
        AssertBindingMismatch(service, token, "run-wp09-binding", Channel("worker-a", "channel-a", "workspace-b"), "Wrong project/workspace binding succeeded.");

        using (var connection = OpenConfigured(database.Path))
        {
            Execute(connection, "UPDATE runs SET role='reviewer' WHERE run_id='run-wp09-binding';");
        }

        var reloaded = SessionSuccess(service.Resolve(new ResolveRunSessionRequest("run-wp09-binding", token, channel)));
        Wp09AssertEqual(AgentRole.Reviewer, reloaded.Role!.Value,
            "Resolved principal trusted an issuance/caller role instead of current durable runs.role.");
    }

    private static void RunSessionExpiryRevocationAndReissueFailClosed()
    {
        using var database = MigratedDatabase.Create();
        SeedRun(database.Path, "run-wp09-lifecycle", "pm");
        var clock = new MutableSecurityClock(DateTimeOffset.FromUnixTimeMilliseconds(30_000));
        var service = new RunSessionService(new SqliteRunSessionStore(database.Path), clock);
        var channel = Channel("worker-a", "channel-a", "workspace-a");
        var first = SessionSuccess(service.Create(new CreateRunSessionRequest(
            "run-wp09-lifecycle", channel, clock.UtcNow.AddMinutes(1))));
        var firstToken = first.Token.ExportOnceForAuthenticatedTransport();
        var second = SessionSuccess(service.Create(new CreateRunSessionRequest(
            "run-wp09-lifecycle", channel, clock.UtcNow.AddMinutes(2))));
        var secondToken = second.Token.ExportOnceForAuthenticatedTransport();

        Wp09AssertEqual<RunSessionError?>(RunSessionError.SessionRevoked,
            service.Resolve(new ResolveRunSessionRequest("run-wp09-lifecycle", firstToken, channel)).Failure?.Code,
            "Reissue did not revoke the previous matching generation.");
        AssertTrue(service.Resolve(new ResolveRunSessionRequest(
            "run-wp09-lifecycle", secondToken, channel)).Succeeded,
            "Replacement session was not usable.");

        service.Revoke(second.HandleId);
        Wp09AssertEqual<RunSessionError?>(RunSessionError.SessionRevoked,
            service.Resolve(new ResolveRunSessionRequest(
                "run-wp09-lifecycle", secondToken, channel)).Failure?.Code,
            "Explicit revocation was not enforced.");

        var expiring = SessionSuccess(service.Create(new CreateRunSessionRequest(
            "run-wp09-lifecycle", Channel("worker-z", "channel-z", "workspace-a"), clock.UtcNow.AddSeconds(1))));
        clock.UtcNow = clock.UtcNow.AddSeconds(2);
        Wp09AssertEqual<RunSessionError?>(RunSessionError.SessionExpired,
            service.Resolve(new ResolveRunSessionRequest(
                "run-wp09-lifecycle",
                expiring.Token.ExportOnceForAuthenticatedTransport(),
                Channel("worker-z", "channel-z", "workspace-a"))).Failure?.Code,
            "Expired session remained valid.");

        var channelBound = Channel("worker-c", "channel-c", "workspace-a");
        var channelSession = SessionSuccess(service.Create(new CreateRunSessionRequest(
            "run-wp09-lifecycle", channelBound, clock.UtcNow.AddMinutes(1))));
        Wp09AssertEqual(1, service.RevokeByChannelWorker(channelBound),
            "Channel/worker revocation did not revoke the active session.");
        Wp09AssertEqual<RunSessionError?>(RunSessionError.SessionRevoked,
            service.Resolve(new ResolveRunSessionRequest(
                "run-wp09-lifecycle", channelSession.Token.ExportOnceForAuthenticatedTransport(), channelBound)).Failure?.Code,
            "Channel/worker revocation was not enforced.");

        var runChannel = Channel("worker-d", "channel-d", "workspace-a");
        var runSession = SessionSuccess(service.Create(new CreateRunSessionRequest(
            "run-wp09-lifecycle", runChannel, clock.UtcNow.AddMinutes(1))));
        AssertTrue(service.RevokeByRun("run-wp09-lifecycle") >= 1,
            "Run cancellation seam did not revoke active sessions.");
        Wp09AssertEqual<RunSessionError?>(RunSessionError.SessionRevoked,
            service.Resolve(new ResolveRunSessionRequest(
                "run-wp09-lifecycle", runSession.Token.ExportOnceForAuthenticatedTransport(), runChannel)).Failure?.Code,
            "Run-wide revocation was not enforced.");
    }

    private static void RunSessionUnknownOrMissingRunFailsClosed()
    {
        using var database = MigratedDatabase.Create();
        var clock = new MutableSecurityClock(DateTimeOffset.FromUnixTimeMilliseconds(40_000));
        var service = new RunSessionService(new SqliteRunSessionStore(database.Path), clock);
        var missing = service.Create(new CreateRunSessionRequest(
            "missing-run", Channel("worker-a", "channel-a", "workspace-a"), clock.UtcNow.AddMinutes(1)));
        Wp09AssertEqual<RunSessionError?>(RunSessionError.RunNotFound, missing.Failure?.Code, "Nonexistent durable run did not fail closed.");

        SeedRun(database.Path, "run-wp09-unknown", "self_declared_admin");
        var unknown = service.Create(new CreateRunSessionRequest(
            "run-wp09-unknown", Channel("worker-a", "channel-a", "workspace-a"), clock.UtcNow.AddMinutes(1)));
        Wp09AssertEqual<RunSessionError?>(RunSessionError.UnknownAgentRole, unknown.Failure?.Code, "Unknown durable role did not fail closed.");
    }

    private static void AssertBindingMismatch(
        RunSessionService service,
        string token,
        string runId,
        AuthenticatedChannelContext channel,
        string message) =>
        Wp09AssertEqual<RunSessionError?>(
            RunSessionError.SessionBindingMismatch,
            service.Resolve(new ResolveRunSessionRequest(runId, token, channel)).Failure?.Code,
            message);

    private static CreateRunSessionResult SessionSuccess(RunSessionResult<CreateRunSessionResult> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected RunSession success, got {result.Failure?.Code}: {result.Failure?.Detail}.");
        }

        return result.Value;
    }

    private static CallerPrincipal SessionSuccess(RunSessionResult<CallerPrincipal> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected principal resolution, got {result.Failure?.Code}: {result.Failure?.Detail}.");
        }

        return result.Value;
    }

    private static AuthenticatedChannelContext Channel(string worker, string channel, string workspace) =>
        new(channel, AuthenticatedClientKind.AgentRuntime, worker, new ProjectScope(Wp09ProjectId, workspace));

    private static void SeedRun(string databasePath, string runId, string role)
    {
        using var connection = OpenConfigured(databasePath);
        Execute(connection,
            $"INSERT OR IGNORE INTO workflow_runs(workflow_run_id,status,created_at_ms,updated_at_ms) VALUES ('workflow-wp09','running',1,1);" +
            $"INSERT INTO runs(run_id,workflow_run_id,role,status,depth,created_at_ms,updated_at_ms) " +
            $"VALUES ('{runId}','workflow-wp09','{role}','running',0,1,1);");
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string WrongToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(RunSessionService.TokenByteLength))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void AssertTokenAbsentFromEveryDatabaseTextField(
        System.Data.Common.DbConnection connection,
        string token)
    {
        List<string> tables = [];
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (var table in tables)
        {
            List<string> textColumns = [];
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\");";
                using var reader = pragma.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(2) && reader.GetString(2).Contains("TEXT", StringComparison.OrdinalIgnoreCase))
                    {
                        textColumns.Add(reader.GetString(1));
                    }
                }
            }

            foreach (var column in textColumns)
            {
                using var scan = connection.CreateCommand();
                var safeTable = table.Replace("\"", "\"\"", StringComparison.Ordinal);
                var safeColumn = column.Replace("\"", "\"\"", StringComparison.Ordinal);
                scan.CommandText = $"SELECT COUNT(*) FROM \"{safeTable}\" WHERE instr(\"{safeColumn}\", $token) > 0;";
                var parameter = scan.CreateParameter();
                parameter.ParameterName = "$token";
                parameter.Value = token;
                scan.Parameters.Add(parameter);
                Wp09AssertEqual(0L, Convert.ToInt64(scan.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture),
                    $"Plaintext RunSession token appeared in {table}.{column}.");
            }
        }
    }

    private static void Wp09AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private sealed class MutableSecurityClock(DateTimeOffset utcNow) : ISecurityClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
