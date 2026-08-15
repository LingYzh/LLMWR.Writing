using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Security.Sandbox;

public interface ISandboxFaultInjector
{
    SandboxFaultPoint Fault { get; }
}

public sealed class NoSandboxFaultInjector : ISandboxFaultInjector
{
    public static NoSandboxFaultInjector Instance { get; } = new();

    private NoSandboxFaultInjector()
    {
    }

    public SandboxFaultPoint Fault => SandboxFaultPoint.None;
}

public sealed class MutableSandboxFaultInjector : ISandboxFaultInjector
{
    public SandboxFaultPoint Fault { get; set; } = SandboxFaultPoint.None;
}

public interface ISandboxPrincipalValidator
{
    CapabilityDecision Validate(CallerPrincipal? principal, Capability capability);
}

public sealed class AuthorizationPrincipalValidator : ISandboxPrincipalValidator
{
    private readonly IAuthorizationService authorization;

    public AuthorizationPrincipalValidator(IAuthorizationService authorization)
    {
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
    }

    public CapabilityDecision Validate(CallerPrincipal? principal, Capability capability) =>
        authorization.Authorize(principal, new AuthorizationRequest(capability));
}

public interface ISandboxPathGuard
{
    SandboxError? TryOpenRead(string projectRoot, string runId, string logicalRelativePath, out byte[] bytes);

    SandboxError? TryOpenWrite(string projectRoot, string runId, string logicalRelativePath, ReadOnlySpan<byte> contents);
}

public interface ISandboxHost
{
    SandboxAvailability Availability { get; }

    SandboxIdentity? Identity { get; }

    SandboxExecutionResult Execute(SandboxExecutionRequest request);
}

public sealed class UnsupportedSandboxHost : ISandboxHost
{
    public static UnsupportedSandboxHost Instance { get; } = new();

    private UnsupportedSandboxHost()
    {
    }

    public SandboxAvailability Availability => SandboxAvailability.UnsupportedPlatform;

    public SandboxIdentity? Identity => null;

    public SandboxExecutionResult Execute(SandboxExecutionRequest request) =>
        SandboxExecutionResult.Fail(request, SandboxError.PlatformUnsupported);
}

public sealed class UnavailableSandboxHost : ISandboxHost
{
    private readonly SandboxError error;

    public UnavailableSandboxHost(SandboxError error)
    {
        this.error = error;
    }

    public SandboxAvailability Availability => SandboxAvailability.Unavailable;

    public SandboxIdentity? Identity => null;

    public SandboxExecutionResult Execute(SandboxExecutionRequest request) =>
        SandboxExecutionResult.Fail(request, error);
}
