using System.Security.Cryptography;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Security;

public enum RunSessionError
{
    InvalidPrincipal,
    InvalidRunSession,
    SessionExpired,
    SessionRevoked,
    SessionBindingMismatch,
    RunNotFound,
    UnknownAgentRole,
    InfrastructureFailure
}

public sealed record RunSessionFailure(RunSessionError Code, string? Detail = null);

public sealed record RunSessionResult<T>(T? Value, RunSessionFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public static class RunSessionResults
{
    public static RunSessionResult<T> Success<T>(T value) => new(value, null);

    public static RunSessionResult<T> Fail<T>(RunSessionError code, string? detail = null) =>
        new(default, new RunSessionFailure(code, detail));
}

public sealed class OpaqueRunSessionToken
{
    private string? value;

    internal OpaqueRunSessionToken(string value)
    {
        this.value = value;
    }

    public string ExportOnceForAuthenticatedTransport() =>
        Interlocked.Exchange(ref value, null) ??
        throw new InvalidOperationException("The opaque RunSession token has already been exported.");

    public override string ToString() => "[REDACTED RUN SESSION TOKEN]";
}

public sealed record CreateRunSessionRequest(
    string RunId,
    AuthenticatedChannelContext Channel,
    DateTimeOffset ExpiresAt);

public sealed record CreateRunSessionResult(
    string HandleId,
    string RunId,
    OpaqueRunSessionToken Token,
    DateTimeOffset ExpiresAt);

public sealed record ResolveRunSessionRequest(
    string RunId,
    string OpaqueToken,
    AuthenticatedChannelContext Channel)
{
    public override string ToString() =>
        $"ResolveRunSessionRequest {{ RunId = {RunId}, OpaqueToken = [REDACTED], Channel = {Channel.ChannelInstanceId} }}";
}

public sealed record DurableRunIdentity(string RunId, string RoleValue);

public sealed record StoredRunSession(
    string HandleId,
    string RunId,
    string WorkerInstanceId,
    string ChannelInstanceId,
    string ProjectScope,
    string TokenHash,
    long ExpiresAtMs,
    long? RevokedAtMs,
    long CreatedAtMs);

public sealed record PersistRunSessionRequest(
    string RunId,
    string WorkerInstanceId,
    string ChannelInstanceId,
    string ProjectScope,
    string TokenHash,
    long ExpiresAtMs,
    long CreatedAtMs);

public interface IRunSessionStore
{
    DurableRunIdentity? LoadRun(string runId);

    StoredRunSession IssueReplacingActive(PersistRunSessionRequest request);

    StoredRunSession? FindByTokenHash(string tokenHash);

    int RevokeHandle(string handleId, long revokedAtMs);

    int RevokeByRun(string runId, long revokedAtMs);

    int RevokeByChannelWorker(string channelInstanceId, string workerInstanceId, long revokedAtMs);
}

public interface ISecurityClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IRunSecurityPolicySource
{
    RuntimePermissionMode GetRuntimePermissionMode(string runId);
}

public sealed class FailClosedRunSecurityPolicySource : IRunSecurityPolicySource
{
    public static FailClosedRunSecurityPolicySource Instance { get; } = new();

    private FailClosedRunSecurityPolicySource()
    {
    }

    public RuntimePermissionMode GetRuntimePermissionMode(string runId) => RuntimePermissionMode.Ask;
}

public sealed class SystemSecurityClock : ISecurityClock
{
    public static SystemSecurityClock Instance { get; } = new();

    private SystemSecurityClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class RunSessionService
{
    public const int TokenByteLength = 32;

    private readonly IRunSessionStore store;
    private readonly ISecurityClock clock;
    private readonly IRunSecurityPolicySource policySource;

    public RunSessionService(
        IRunSessionStore store,
        ISecurityClock? clock = null,
        IRunSecurityPolicySource? policySource = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? SystemSecurityClock.Instance;
        this.policySource = policySource ?? FailClosedRunSecurityPolicySource.Instance;
    }

    public RunSessionResult<CreateRunSessionResult> Create(CreateRunSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            request.Channel.ValidateForAgentRun();
            if (request.ExpiresAt <= clock.UtcNow)
            {
                return RunSessionResults.Fail<CreateRunSessionResult>(RunSessionError.SessionExpired);
            }

            var run = store.LoadRun(request.RunId);
            if (run is null)
            {
                return RunSessionResults.Fail<CreateRunSessionResult>(RunSessionError.RunNotFound);
            }

            if (!AgentRoleCodec.TryParse(run.RoleValue, out _))
            {
                return RunSessionResults.Fail<CreateRunSessionResult>(RunSessionError.UnknownAgentRole);
            }

            var tokenBytes = RandomNumberGenerator.GetBytes(TokenByteLength);
            var token = Base64UrlEncode(tokenBytes);
            var tokenHash = Convert.ToHexString(SHA256.HashData(tokenBytes)).ToLowerInvariant();
            var createdAtMs = clock.UtcNow.ToUnixTimeMilliseconds();
            var stored = store.IssueReplacingActive(new PersistRunSessionRequest(
                run.RunId,
                request.Channel.WorkerInstanceId,
                request.Channel.ChannelInstanceId,
                request.Channel.ProjectScope.ToCanonicalValue(),
                tokenHash,
                request.ExpiresAt.ToUnixTimeMilliseconds(),
                createdAtMs));

            return RunSessionResults.Success(new CreateRunSessionResult(
                stored.HandleId,
                stored.RunId,
                new OpaqueRunSessionToken(token),
                DateTimeOffset.FromUnixTimeMilliseconds(stored.ExpiresAtMs)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return RunSessionResults.Fail<CreateRunSessionResult>(RunSessionError.InfrastructureFailure, exception.GetType().Name);
        }
    }

    public RunSessionResult<CallerPrincipal> Resolve(ResolveRunSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            request.Channel.ValidateForAgentRun();
            if (!TryDecodeToken(request.OpaqueToken, out var tokenBytes) || tokenBytes.Length != TokenByteLength)
            {
                return RunSessionResults.Fail<CallerPrincipal>(RunSessionError.InvalidRunSession);
            }

            var computedHashBytes = SHA256.HashData(tokenBytes);
            var tokenHash = Convert.ToHexString(computedHashBytes).ToLowerInvariant();
            var session = store.FindByTokenHash(tokenHash);
            if (session is null || !TryDecodeHash(session.TokenHash, out var persistedHashBytes) ||
                !CryptographicOperations.FixedTimeEquals(computedHashBytes, persistedHashBytes))
            {
                return RunSessionResults.Fail<CallerPrincipal>(RunSessionError.InvalidRunSession);
            }

            if (session.RevokedAtMs is not null)
            {
                return RunSessionResults.Fail<CallerPrincipal>(RunSessionError.SessionRevoked);
            }

            if (session.ExpiresAtMs <= clock.UtcNow.ToUnixTimeMilliseconds())
            {
                return RunSessionResults.Fail<CallerPrincipal>(RunSessionError.SessionExpired);
            }

            if (!StringComparer.Ordinal.Equals(session.RunId, request.RunId) ||
                !StringComparer.Ordinal.Equals(session.WorkerInstanceId, request.Channel.WorkerInstanceId) ||
                !StringComparer.Ordinal.Equals(session.ChannelInstanceId, request.Channel.ChannelInstanceId) ||
                !StringComparer.Ordinal.Equals(session.ProjectScope, request.Channel.ProjectScope.ToCanonicalValue()))
            {
                return RunSessionResults.Fail<CallerPrincipal>(RunSessionError.SessionBindingMismatch);
            }

            var run = store.LoadRun(session.RunId);
            if (run is null)
            {
                return RunSessionResults.Fail<CallerPrincipal>(RunSessionError.RunNotFound);
            }

            if (!AgentRoleCodec.TryParse(run.RoleValue, out var role))
            {
                return RunSessionResults.Fail<CallerPrincipal>(RunSessionError.UnknownAgentRole);
            }

            return RunSessionResults.Success(CallerPrincipal.CreateAgentRun(
                run.RunId,
                role,
                policySource.GetRuntimePermissionMode(run.RunId),
                session.HandleId,
                request.Channel));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return RunSessionResults.Fail<CallerPrincipal>(RunSessionError.InfrastructureFailure, exception.GetType().Name);
        }
    }

    public int Revoke(string handleId) => store.RevokeHandle(handleId, clock.UtcNow.ToUnixTimeMilliseconds());

    public int RevokeByRun(string runId) => store.RevokeByRun(runId, clock.UtcNow.ToUnixTimeMilliseconds());

    public int RevokeByChannelWorker(AuthenticatedChannelContext channel) =>
        store.RevokeByChannelWorker(
            channel.ChannelInstanceId,
            channel.WorkerInstanceId,
            clock.UtcNow.ToUnixTimeMilliseconds());

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecodeToken(string value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value) || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryDecodeHash(string value, out byte[] bytes)
    {
        bytes = [];
        try
        {
            bytes = Convert.FromHexString(value);
            return bytes.Length == SHA256.HashSizeInBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
