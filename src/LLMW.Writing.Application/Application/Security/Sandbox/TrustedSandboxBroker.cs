using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Security.Sandbox;

public interface ITrustedSandboxBroker
{
    SandboxAvailability Availability { get; }

    SandboxExecutionResult ExecuteShell(SandboxExecutionRequest request);

    SandboxExecutionResult ExecuteScript(SandboxExecutionRequest request);

    SandboxFileReadResult ReadFile(SandboxFileReadRequest request);

    SandboxFileWriteResult WriteSandboxWorkFile(SandboxFileWriteRequest request);
}

public sealed class TrustedSandboxBroker : ITrustedSandboxBroker
{
    private readonly IAuthorizationService authorization;
    private readonly ISandboxHost sandboxHost;
    private readonly ISandboxPathGuard pathGuard;
    private readonly SandboxProjectContext projectContext;
    private readonly ISandboxPrincipalValidator principalValidator;
    private readonly ISandboxFaultInjector faultInjector;
    private readonly IRunSessionRevalidator? sessionRevalidator;

    public TrustedSandboxBroker(
        IAuthorizationService authorization,
        ISandboxHost sandboxHost,
        ISandboxPathGuard pathGuard,
        SandboxProjectContext projectContext,
        ISandboxPrincipalValidator? principalValidator = null,
        ISandboxFaultInjector? faultInjector = null,
        IRunSessionRevalidator? sessionRevalidator = null)
    {
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.sandboxHost = sandboxHost ?? throw new ArgumentNullException(nameof(sandboxHost));
        this.pathGuard = pathGuard ?? throw new ArgumentNullException(nameof(pathGuard));
        this.projectContext = projectContext ?? throw new ArgumentNullException(nameof(projectContext));
        this.principalValidator = principalValidator ?? new AuthorizationPrincipalValidator(authorization);
        this.faultInjector = faultInjector ?? NoSandboxFaultInjector.Instance;
        this.sessionRevalidator = sessionRevalidator;
    }

    public SandboxAvailability Availability => sandboxHost.Availability;

    public SandboxExecutionResult ExecuteShell(SandboxExecutionRequest request) =>
        ExecuteProcess(request, Capability.ShellExecute);

    public SandboxExecutionResult ExecuteScript(SandboxExecutionRequest request) =>
        ExecuteProcess(request, Capability.ScriptExecute);

    public SandboxFileReadResult ReadFile(SandboxFileReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (faultInjector.Fault == SandboxFaultPoint.BrokerUnavailable)
        {
            return SandboxFileReadResult.Fail(SandboxError.BrokerUnavailable);
        }

        if (!TryBindPrincipal(request.Principal, null, null, out var effective, out var bindError, out var bindReason))
        {
            return SandboxFileReadResult.Fail(bindError, bindReason);
        }

        var decision = Authorize(effective, Capability.ProjectFileRead);
        if (decision.Decision != CapabilityDecisionKind.Allowed)
        {
            return SandboxFileReadResult.Fail(MapDecision(decision), string.Join(',', decision.Reasons));
        }

        if (TryDenyCallerClaims(request.ProjectRoot, request.ProjectScope, request.RunId, request.Principal, out var claimDenied))
        {
            return SandboxFileReadResult.Fail(claimDenied);
        }

        if (TryClassifyDenied(projectContext.TrustedProjectRoot, request.RunId, request.LogicalRelativePath, forWrite: false, out var denied))
        {
            return SandboxFileReadResult.Fail(denied);
        }

        if (!TryBindPrincipal(request.Principal, null, null, out effective, out bindError, out bindReason))
        {
            return SandboxFileReadResult.Fail(bindError, bindReason);
        }

        var recheck = Authorize(effective, Capability.ProjectFileRead);
        if (recheck.Decision != CapabilityDecisionKind.Allowed)
        {
            return SandboxFileReadResult.Fail(MapDecision(recheck), string.Join(',', recheck.Reasons));
        }

        var error = pathGuard.TryOpenRead(
            projectContext.TrustedProjectRoot,
            request.RunId,
            request.LogicalRelativePath,
            out var bytes);
        return error is null
            ? SandboxFileReadResult.Ok(bytes)
            : SandboxFileReadResult.Fail(error.Value);
    }

    public SandboxFileWriteResult WriteSandboxWorkFile(SandboxFileWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (faultInjector.Fault == SandboxFaultPoint.BrokerUnavailable)
        {
            return SandboxFileWriteResult.Fail(SandboxError.BrokerUnavailable);
        }

        if (!TryBindPrincipal(request.Principal, null, null, out var effective, out var bindError, out var bindReason))
        {
            return SandboxFileWriteResult.Fail(bindError, bindReason);
        }

        var decision = Authorize(effective, Capability.RawWrite);
        if (decision.Decision != CapabilityDecisionKind.Allowed)
        {
            return SandboxFileWriteResult.Fail(MapDecision(decision), string.Join(',', decision.Reasons));
        }

        if (TryDenyCallerClaims(request.ProjectRoot, request.ProjectScope, request.RunId, request.Principal, out var claimDenied))
        {
            return SandboxFileWriteResult.Fail(claimDenied);
        }

        if (!SandboxPathPolicy.IsDesignatedWorkRelative(request.LogicalRelativePath, request.RunId))
        {
            return SandboxFileWriteResult.Fail(SandboxError.PathOutOfScope);
        }

        if (TryClassifyDenied(projectContext.TrustedProjectRoot, request.RunId, request.LogicalRelativePath, forWrite: true, out var denied))
        {
            return SandboxFileWriteResult.Fail(denied);
        }

        if (!TryBindPrincipal(request.Principal, null, null, out effective, out bindError, out bindReason))
        {
            return SandboxFileWriteResult.Fail(bindError, bindReason);
        }

        var recheck = Authorize(effective, Capability.RawWrite);
        if (recheck.Decision != CapabilityDecisionKind.Allowed)
        {
            return SandboxFileWriteResult.Fail(MapDecision(recheck), string.Join(',', recheck.Reasons));
        }

        var error = pathGuard.TryOpenWrite(
            projectContext.TrustedProjectRoot,
            request.RunId,
            request.LogicalRelativePath,
            request.Contents.Span);
        return error is null ? SandboxFileWriteResult.Ok() : SandboxFileWriteResult.Fail(error.Value);
    }

    private SandboxExecutionResult ExecuteProcess(SandboxExecutionRequest request, Capability required)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Capability != required)
        {
            return SandboxExecutionResult.Fail(request, SandboxError.CapabilityDenied, "Shell and Script remain distinct capabilities.");
        }

        if (faultInjector.Fault == SandboxFaultPoint.BrokerUnavailable)
        {
            return SandboxExecutionResult.Fail(request, SandboxError.BrokerUnavailable);
        }

        if (faultInjector.Fault == SandboxFaultPoint.RunSessionRevalidation)
        {
            return SandboxExecutionResult.Fail(request, SandboxError.SessionBindingMismatch, "Injected RunSession revalidation failure.");
        }

        if (sandboxHost.Availability is not SandboxAvailability.Available)
        {
            return sandboxHost.Execute(request);
        }

        if (!TryBindPrincipal(
                request.Principal,
                request.Binding.RunId,
                request.Binding.WorkerInstanceId,
                out var effective,
                out var bindError,
                out var bindReason))
        {
            return SandboxExecutionResult.Fail(request, bindError, bindReason);
        }

        var entry = principalValidator.Validate(effective, required);
        if (entry.Decision != CapabilityDecisionKind.Allowed)
        {
            return SandboxExecutionResult.Fail(request, MapDecision(entry), string.Join(',', entry.Reasons));
        }

        if (request.NetworkRequired)
        {
            var network = principalValidator.Validate(effective, Capability.NetworkRequest);
            if (network.Decision != CapabilityDecisionKind.Allowed)
            {
                return SandboxExecutionResult.Fail(request, SandboxError.NetworkDenied, string.Join(',', network.Reasons));
            }
        }

        if (TryDenyCallerClaims(request.ProjectRoot, request.Binding.ProjectScope, request.Binding.RunId, request.Principal, out var claimDenied))
        {
            return SandboxExecutionResult.Fail(request, claimDenied, "Caller project/run claims do not match Core sandbox context.");
        }

        var extraError = SandboxEnvironmentPolicy.ValidateExtraEnvironment(request.ExtraEnvironment);
        if (extraError is not null)
        {
            return SandboxExecutionResult.Fail(request, extraError.Value, "ExtraEnvironment is not on the independent allowlist.");
        }

        if (IsProjectExecutable(projectContext.TrustedProjectRoot, request.ExecutablePath))
        {
            return SandboxExecutionResult.Fail(request, SandboxError.CapabilityDenied, "Untrusted project executables are not activated in WP10.");
        }

        _ = ExactCommand.Fingerprint(request.ExecutablePath, request.Arguments);

        if (!TryBindPrincipal(
                request.Principal,
                request.Binding.RunId,
                request.Binding.WorkerInstanceId,
                out effective,
                out bindError,
                out bindReason))
        {
            return SandboxExecutionResult.Fail(request, bindError, bindReason);
        }

        var final = principalValidator.Validate(effective, required);
        if (final.Decision != CapabilityDecisionKind.Allowed)
        {
            return SandboxExecutionResult.Fail(request, MapDecision(final), string.Join(',', final.Reasons));
        }

        if (request.NetworkRequired)
        {
            var networkRecheck = principalValidator.Validate(effective, Capability.NetworkRequest);
            if (networkRecheck.Decision != CapabilityDecisionKind.Allowed)
            {
                return SandboxExecutionResult.Fail(request, SandboxError.NetworkDenied, string.Join(',', networkRecheck.Reasons));
            }
        }

        return sandboxHost.Execute(request);
    }

    private bool TryBindPrincipal(
        CallerPrincipal principal,
        string? launchRunId,
        string? launchWorkerInstanceId,
        out CallerPrincipal effective,
        out SandboxError error,
        out string? reason)
    {
        effective = principal;
        error = SandboxError.SessionBindingMismatch;
        reason = null;
        if (faultInjector.Fault == SandboxFaultPoint.RunSessionRevalidation)
        {
            reason = "Injected RunSession revalidation failure.";
            return false;
        }

        if (principal.Kind != PrincipalKind.AgentRun)
        {
            return true;
        }

        if (sessionRevalidator is null)
        {
            reason = "AgentRun sandbox operations require Core RunSession revalidation.";
            return false;
        }

        var revalidation = sessionRevalidator.Revalidate(principal, projectContext, launchRunId, launchWorkerInstanceId);
        effective = revalidation.EffectivePrincipal;
        if (!revalidation.Succeeded)
        {
            error = revalidation.Error ?? SandboxError.SessionBindingMismatch;
            reason = revalidation.DenyReason;
            return false;
        }

        return true;
    }

    private bool TryDenyCallerClaims(
        string claimedProjectRoot,
        ProjectScope claimedScope,
        string claimedRunId,
        CallerPrincipal principal,
        out SandboxError error)
    {
        error = SandboxError.PathOutOfScope;
        if (!SandboxPathPolicy.PathsEqual(claimedProjectRoot, projectContext.TrustedProjectRoot))
        {
            return true;
        }

        if (!string.Equals(
                claimedScope.ToCanonicalValue(),
                projectContext.TrustedProjectScope.ToCanonicalValue(),
                StringComparison.Ordinal))
        {
            error = SandboxError.SessionBindingMismatch;
            return true;
        }

        if (principal.Kind == PrincipalKind.AgentRun)
        {
            if (!string.Equals(principal.RunId, claimedRunId, StringComparison.Ordinal) ||
                principal.ProjectScope is null ||
                !string.Equals(
                    principal.ProjectScope.ToCanonicalValue(),
                    projectContext.TrustedProjectScope.ToCanonicalValue(),
                    StringComparison.Ordinal))
            {
                error = SandboxError.SessionBindingMismatch;
                return true;
            }
        }

        return false;
    }

    private CapabilityDecision Authorize(CallerPrincipal? principal, Capability capability) =>
        authorization.Authorize(principal, new AuthorizationRequest(capability));

    private static bool IsProjectExecutable(string projectRoot, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return true;
        }

        try
        {
            return SandboxPathPolicy.IsInside(projectRoot, Path.GetFullPath(executablePath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    private static bool TryClassifyDenied(
        string projectRoot,
        string runId,
        string logicalRelativePath,
        bool forWrite,
        out SandboxError error)
    {
        error = SandboxError.PathOutOfScope;
        try
        {
            var relative = SandboxPathPolicy.NormalizeRelative(logicalRelativePath);
            if (SandboxPathPolicy.IsAuthorityTree(relative))
            {
                error = SandboxError.PathOutOfScope;
                return true;
            }

            if (SandboxPathPolicy.IsInternalSandboxTree(relative) &&
                !(forWrite && SandboxPathPolicy.IsDesignatedWorkRelative(relative, runId)))
            {
                error = SandboxError.PathOutOfScope;
                return true;
            }

            if (forWrite && !SandboxPathPolicy.IsDesignatedWorkRelative(relative, runId))
            {
                error = SandboxError.PathOutOfScope;
                return true;
            }

            var fullPath = SandboxPathPolicy.CombineRelative(projectRoot, relative);
            if (!SandboxPathPolicy.IsInside(projectRoot, fullPath))
            {
                error = SandboxError.PathOutOfScope;
                return true;
            }

            if (SandboxPathPolicy.IsWindowsSystemLocation(fullPath))
            {
                error = SandboxError.PathOutOfScope;
                return true;
            }

            return false;
        }
        catch (ArgumentException)
        {
            error = SandboxError.PathOutOfScope;
            return true;
        }
    }

    private static SandboxError MapDecision(CapabilityDecision decision)
    {
        if (decision.Reasons.Contains(CapabilityDecisionReason.TrustRequired))
        {
            return SandboxError.TrustRequired;
        }

        if (decision.Decision == CapabilityDecisionKind.RequiresApproval)
        {
            return SandboxError.ApprovalRequired;
        }

        if (decision.Reasons.Contains(CapabilityDecisionReason.PathOutOfScope) ||
            decision.Reasons.Contains(CapabilityDecisionReason.HardDeny))
        {
            return SandboxError.PathOutOfScope;
        }

        return SandboxError.CapabilityDenied;
    }
}
