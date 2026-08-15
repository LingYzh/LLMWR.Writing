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
    private readonly ISandboxPrincipalValidator principalValidator;
    private readonly ISandboxFaultInjector faultInjector;

    public TrustedSandboxBroker(
        IAuthorizationService authorization,
        ISandboxHost sandboxHost,
        ISandboxPathGuard pathGuard,
        ISandboxPrincipalValidator? principalValidator = null,
        ISandboxFaultInjector? faultInjector = null)
    {
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.sandboxHost = sandboxHost ?? throw new ArgumentNullException(nameof(sandboxHost));
        this.pathGuard = pathGuard ?? throw new ArgumentNullException(nameof(pathGuard));
        this.principalValidator = principalValidator ?? new AuthorizationPrincipalValidator(authorization);
        this.faultInjector = faultInjector ?? NoSandboxFaultInjector.Instance;
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

        var decision = Authorize(request.Principal, Capability.ProjectFileRead);
        if (decision.Decision != CapabilityDecisionKind.Allowed)
        {
            return SandboxFileReadResult.Fail(MapDecision(decision), string.Join(',', decision.Reasons));
        }

        if (TryClassifyDenied(request.ProjectRoot, request.RunId, request.LogicalRelativePath, forWrite: false, out var denied))
        {
            return SandboxFileReadResult.Fail(denied);
        }

        var recheck = Authorize(request.Principal, Capability.ProjectFileRead);
        if (recheck.Decision != CapabilityDecisionKind.Allowed)
        {
            return SandboxFileReadResult.Fail(MapDecision(recheck), string.Join(',', recheck.Reasons));
        }

        var error = pathGuard.TryOpenRead(request.ProjectRoot, request.RunId, request.LogicalRelativePath, out var bytes);
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

        var decision = Authorize(request.Principal, Capability.RawWrite);
        if (decision.Decision != CapabilityDecisionKind.Allowed)
        {
            return SandboxFileWriteResult.Fail(MapDecision(decision), string.Join(',', decision.Reasons));
        }

        if (!SandboxPathPolicy.IsDesignatedWorkRelative(request.LogicalRelativePath, request.RunId))
        {
            return SandboxFileWriteResult.Fail(SandboxError.PathOutOfScope);
        }

        if (TryClassifyDenied(request.ProjectRoot, request.RunId, request.LogicalRelativePath, forWrite: true, out var denied))
        {
            return SandboxFileWriteResult.Fail(denied);
        }

        var recheck = Authorize(request.Principal, Capability.RawWrite);
        if (recheck.Decision != CapabilityDecisionKind.Allowed)
        {
            return SandboxFileWriteResult.Fail(MapDecision(recheck), string.Join(',', recheck.Reasons));
        }

        var error = pathGuard.TryOpenWrite(
            request.ProjectRoot,
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

        if (sandboxHost.Availability is not SandboxAvailability.Available)
        {
            return sandboxHost.Execute(request);
        }

        var entry = principalValidator.Validate(request.Principal, required);
        if (entry.Decision != CapabilityDecisionKind.Allowed)
        {
            return SandboxExecutionResult.Fail(request, MapDecision(entry), string.Join(',', entry.Reasons));
        }

        if (request.NetworkRequired)
        {
            var network = principalValidator.Validate(request.Principal, Capability.NetworkRequest);
            if (network.Decision != CapabilityDecisionKind.Allowed)
            {
                return SandboxExecutionResult.Fail(request, SandboxError.NetworkDenied, string.Join(',', network.Reasons));
            }
        }

        if (IsProjectExecutable(request.ProjectRoot, request.ExecutablePath))
        {
            return SandboxExecutionResult.Fail(request, SandboxError.CapabilityDenied, "Untrusted project executables are not activated in WP10.");
        }

        _ = ExactCommand.Fingerprint(request.ExecutablePath, request.Arguments);

        var final = principalValidator.Validate(request.Principal, required);
        if (final.Decision != CapabilityDecisionKind.Allowed)
        {
            return SandboxExecutionResult.Fail(request, MapDecision(final), string.Join(',', final.Reasons));
        }

        if (request.NetworkRequired)
        {
            var networkRecheck = principalValidator.Validate(request.Principal, Capability.NetworkRequest);
            if (networkRecheck.Decision != CapabilityDecisionKind.Allowed)
            {
                return SandboxExecutionResult.Fail(request, SandboxError.NetworkDenied, string.Join(',', networkRecheck.Reasons));
            }
        }

        return sandboxHost.Execute(request);
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
