using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Runtime;
using AppCreateRunSessionRequest = LLMW.Writing.Application.Security.CreateRunSessionRequest;

namespace LLMW.Writing.Application.Provider;

/// <summary>
/// In-process RunSession store backed by durable Run rows. Session rows stay in memory;
/// this is not a <c>project.db</c> writer and is not used by Agent Runtime.
/// </summary>
public sealed class RuntimePersistenceRunSessionStore : IRunSessionStore
{
    private readonly IRuntimePersistence runtime;
    private readonly object gate = new();
    private readonly Dictionary<string, StoredRunSession> byHandle = new(StringComparer.Ordinal);

    public RuntimePersistenceRunSessionStore(IRuntimePersistence runtime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public DurableRunIdentity? LoadRun(string runId)
    {
        var run = runtime.GetRun(runId);
        return run is null ? null : new DurableRunIdentity(run.RunId, run.Role);
    }

    public StoredRunSession IssueReplacingActive(PersistRunSessionRequest request)
    {
        lock (gate)
        {
            var stored = new StoredRunSession(
                Guid.NewGuid().ToString("D"),
                request.RunId,
                request.WorkerInstanceId,
                request.ChannelInstanceId,
                request.ProjectScope,
                request.TokenHash,
                request.ExpiresAtMs,
                null,
                request.CreatedAtMs);
            byHandle[stored.HandleId] = stored;
            return stored;
        }
    }

    public StoredRunSession? FindByTokenHash(string tokenHash)
    {
        lock (gate)
        {
            return byHandle.Values.FirstOrDefault(item =>
                StringComparer.Ordinal.Equals(item.TokenHash, tokenHash));
        }
    }

    public StoredRunSession? FindByHandleId(string handleId)
    {
        lock (gate)
        {
            return byHandle.TryGetValue(handleId, out var session) ? session : null;
        }
    }

    public int RevokeHandle(string handleId, long revokedAtMs)
    {
        lock (gate)
        {
            if (!byHandle.TryGetValue(handleId, out var session) || session.RevokedAtMs is not null)
            {
                return 0;
            }

            byHandle[handleId] = session with { RevokedAtMs = revokedAtMs };
            return 1;
        }
    }

    public int RevokeByRun(string runId, long revokedAtMs)
    {
        lock (gate)
        {
            var count = 0;
            foreach (var pair in byHandle.ToArray())
            {
                if (StringComparer.Ordinal.Equals(pair.Value.RunId, runId) && pair.Value.RevokedAtMs is null)
                {
                    byHandle[pair.Key] = pair.Value with { RevokedAtMs = revokedAtMs };
                    count++;
                }
            }

            return count;
        }
    }

    public int RevokeByChannelWorker(string channelInstanceId, string workerInstanceId, long revokedAtMs)
    {
        lock (gate)
        {
            var count = 0;
            foreach (var pair in byHandle.ToArray())
            {
                if (StringComparer.Ordinal.Equals(pair.Value.ChannelInstanceId, channelInstanceId) &&
                    StringComparer.Ordinal.Equals(pair.Value.WorkerInstanceId, workerInstanceId) &&
                    pair.Value.RevokedAtMs is null)
                {
                    byHandle[pair.Key] = pair.Value with { RevokedAtMs = revokedAtMs };
                    count++;
                }
            }

            return count;
        }
    }
}

/// <summary>
/// Issues Core RunSession principals for in-process WP14 Direct ports. Production Agent Runtime
/// uses <see cref="AuthenticatedProviderInvocationStateClient"/> over WP11 framing instead.
/// </summary>
public sealed class DirectProviderInvocationIdentity
{
    public static readonly Guid TestProjectId = Guid.Parse("018f3e78-aaaa-7abc-8def-0123456789ab");

    private readonly Dictionary<string, CallerPrincipal> principals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunSessionProof> proofs = new(StringComparer.Ordinal);

    public DirectProviderInvocationIdentity(IRuntimePersistence runtime, ISecurityClock? clock = null)
    {
        Channel = new AuthenticatedChannelContext(
            "wp14-direct-channel",
            AuthenticatedClientKind.AgentRuntime,
            "wp14-direct-worker",
            new ProjectScope(TestProjectId, "workspace-01"));
        Sessions = new RunSessionService(new RuntimePersistenceRunSessionStore(runtime), clock);
    }

    public AuthenticatedChannelContext Channel { get; }

    public RunSessionService Sessions { get; }

    public CallerPrincipal Bind(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (principals.TryGetValue(runId, out var existing))
        {
            return existing;
        }

        var created = Sessions.Create(new AppCreateRunSessionRequest(runId, Channel, null));
        if (!created.Succeeded || created.Value is null)
        {
            throw new InvalidOperationException(created.Failure?.Code + " " + created.Failure?.Detail);
        }

        var token = created.Value.Token.ExportOnceForAuthenticatedTransport();
        var resolved = Sessions.Resolve(new ResolveRunSessionRequest(runId, token, Channel));
        if (!resolved.Succeeded || resolved.Value is null)
        {
            throw new InvalidOperationException(resolved.Failure?.Code + " " + resolved.Failure?.Detail);
        }

        principals[runId] = resolved.Value;
        proofs[runId] = new RunSessionProof(runId, token);
        return resolved.Value;
    }

    public CallerPrincipal? PrincipalFor(string runId) =>
        principals.TryGetValue(runId, out var principal) ? principal : null;

    public RunSessionProof? ProofFor(string runId) =>
        proofs.TryGetValue(runId, out var proof) ? proof : null;
}

public sealed class ProviderInvocationDeniedException : InvalidOperationException
{
    public ProviderInvocationDeniedException(string code, string message)
        : base(code)
    {
        Code = code;
        Detail = message;
    }

    public string Code { get; }

    public string Detail { get; }
}
