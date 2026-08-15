using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Security.Sandbox;

public sealed record RunSessionRevalidation(
    bool Succeeded,
    SandboxError? Error,
    CallerPrincipal EffectivePrincipal,
    string? DenyReason)
{
    public static RunSessionRevalidation Ok(CallerPrincipal principal) =>
        new(true, null, principal, null);

    public static RunSessionRevalidation Fail(SandboxError error, CallerPrincipal principal, string? denyReason = null) =>
        new(false, error, principal, denyReason ?? error.ToString());
}

public interface IRunSessionRevalidator
{
    RunSessionRevalidation Revalidate(
        CallerPrincipal principal,
        SandboxProjectContext context,
        string? launchRunId,
        string? launchWorkerInstanceId);
}

public sealed class RunSessionRevalidator : IRunSessionRevalidator
{
    private readonly IRunSessionStore store;
    private readonly ISecurityClock clock;
    private readonly IRunSecurityPolicySource policySource;
    private readonly ISandboxFaultInjector faultInjector;

    public RunSessionRevalidator(
        IRunSessionStore store,
        ISecurityClock? clock = null,
        IRunSecurityPolicySource? policySource = null,
        ISandboxFaultInjector? faultInjector = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? SystemSecurityClock.Instance;
        this.policySource = policySource ?? FailClosedRunSecurityPolicySource.Instance;
        this.faultInjector = faultInjector ?? NoSandboxFaultInjector.Instance;
    }

    public RunSessionRevalidation Revalidate(
        CallerPrincipal principal,
        SandboxProjectContext context,
        string? launchRunId,
        string? launchWorkerInstanceId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(context);
        if (faultInjector.Fault == SandboxFaultPoint.RunSessionRevalidation)
        {
            return RunSessionRevalidation.Fail(
                SandboxError.SessionBindingMismatch,
                principal,
                "Injected RunSession revalidation failure.");
        }

        if (principal.Kind != PrincipalKind.AgentRun)
        {
            return RunSessionRevalidation.Ok(principal);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(principal.SessionHandleId) ||
                string.IsNullOrWhiteSpace(principal.RunId) ||
                principal.ProjectScope is null)
            {
                return RunSessionRevalidation.Fail(SandboxError.SessionBindingMismatch, principal, "AgentRun principal is missing durable binding fields.");
            }

            var session = store.FindByHandleId(principal.SessionHandleId);
            if (session is null)
            {
                return RunSessionRevalidation.Fail(SandboxError.SessionBindingMismatch, principal, "RunSession handle is not durable.");
            }

            if (session.RevokedAtMs is not null)
            {
                return RunSessionRevalidation.Fail(SandboxError.SessionRevoked, principal);
            }

            if (session.ExpiresAtMs <= clock.UtcNow.ToUnixTimeMilliseconds())
            {
                return RunSessionRevalidation.Fail(SandboxError.SessionExpired, principal);
            }

            if (!StringComparer.Ordinal.Equals(principal.RunId, session.RunId) ||
                !StringComparer.Ordinal.Equals(principal.SessionHandleId, session.HandleId) ||
                !StringComparer.Ordinal.Equals(principal.TrustedInstanceId, session.ChannelInstanceId) ||
                !StringComparer.Ordinal.Equals(principal.ProjectScope.ToCanonicalValue(), session.ProjectScope))
            {
                return RunSessionRevalidation.Fail(SandboxError.SessionBindingMismatch, principal, "Principal does not match durable RunSession.");
            }

            if (!StringComparer.Ordinal.Equals(session.ProjectScope, context.TrustedProjectScope.ToCanonicalValue()) ||
                !StringComparer.Ordinal.Equals(principal.ProjectScope.ToCanonicalValue(), context.TrustedProjectScope.ToCanonicalValue()))
            {
                return RunSessionRevalidation.Fail(SandboxError.SessionBindingMismatch, principal, "RunSession project scope does not match Core sandbox context.");
            }

            if (!string.IsNullOrWhiteSpace(launchRunId) && !StringComparer.Ordinal.Equals(session.RunId, launchRunId))
            {
                return RunSessionRevalidation.Fail(SandboxError.SessionBindingMismatch, principal, "Launch RunId does not match the durable session.");
            }

            if (!string.IsNullOrWhiteSpace(launchWorkerInstanceId) &&
                !StringComparer.Ordinal.Equals(session.WorkerInstanceId, launchWorkerInstanceId))
            {
                return RunSessionRevalidation.Fail(SandboxError.SessionBindingMismatch, principal, "Launch WorkerInstanceId does not match the durable session.");
            }

            var run = store.LoadRun(session.RunId);
            if (run is null)
            {
                return RunSessionRevalidation.Fail(SandboxError.SessionBindingMismatch, principal, "Durable run no longer exists.");
            }

            if (!AgentRoleCodec.TryParse(run.RoleValue, out var role))
            {
                return RunSessionRevalidation.Fail(SandboxError.SessionBindingMismatch, principal, "Durable run role is unknown.");
            }

            if (!ProjectScope.TryParseCanonical(session.ProjectScope, out var sessionScope))
            {
                return RunSessionRevalidation.Fail(SandboxError.SessionBindingMismatch, principal, "Durable project scope is not parseable.");
            }

            var channel = new AuthenticatedChannelContext(
                session.ChannelInstanceId,
                AuthenticatedClientKind.AgentRuntime,
                session.WorkerInstanceId,
                sessionScope);
            var fresh = CallerPrincipal.CreateAgentRun(
                run.RunId,
                role,
                policySource.GetRuntimePermissionMode(run.RunId),
                session.HandleId,
                channel);
            return RunSessionRevalidation.Ok(fresh);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return RunSessionRevalidation.Fail(
                SandboxError.BrokerUnavailable,
                principal,
                exception.GetType().Name);
        }
    }
}
