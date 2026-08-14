using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Security;

public sealed record AuthorizationRequest(Capability Capability);

public sealed record SecurityPolicySnapshot(
    bool ProductAllowed,
    bool ToolGranted,
    bool ExtensionGranted,
    bool ProjectTrusted,
    SecurityScopeClassification Scope,
    HardDeny HardDeny,
    bool NarrativeAuthorityAvailable,
    bool ExplicitUserTask);

/// <summary>
/// Resolved only by trusted Core/native composition. Ordinary command and IPC payloads cannot supply this policy.
/// </summary>
public interface ISecurityPolicySource
{
    SecurityPolicySnapshot? Resolve(CallerPrincipal principal, Capability capability);
}

public sealed class FailClosedSecurityPolicySource : ISecurityPolicySource
{
    public static FailClosedSecurityPolicySource Instance { get; } = new();

    private FailClosedSecurityPolicySource()
    {
    }

    public SecurityPolicySnapshot? Resolve(CallerPrincipal principal, Capability capability) => null;
}

public interface IAuthorizationService
{
    CapabilityDecision Authorize(CallerPrincipal? principal, AuthorizationRequest request);
}

public sealed class CoreAuthorizationService : IAuthorizationService
{
    private readonly ISecurityPolicySource policySource;

    public CoreAuthorizationService(ISecurityPolicySource? policySource = null)
    {
        this.policySource = policySource ?? FailClosedSecurityPolicySource.Instance;
    }

    public CapabilityDecision Authorize(CallerPrincipal? principal, AuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (principal is null)
        {
            return InvalidPrincipal(request.Capability);
        }

        var policy = policySource.Resolve(principal, request.Capability);
        if (policy is null)
        {
            return CapabilityEvaluator.Evaluate(new CapabilityEvaluationRequest(
                request.Capability,
                principal.Kind,
                principal.Role,
                principal.RuntimePermissionMode));
        }

        return CapabilityEvaluator.Evaluate(new CapabilityEvaluationRequest(
            request.Capability,
            principal.Kind,
            principal.Role,
            principal.RuntimePermissionMode,
            policy.ProductAllowed,
            policy.ToolGranted,
            policy.ExtensionGranted,
            policy.ProjectTrusted,
            policy.Scope,
            policy.HardDeny,
            policy.NarrativeAuthorityAvailable,
            policy.ExplicitUserTask));
    }

    private static CapabilityDecision InvalidPrincipal(Capability capability) =>
        new(
            capability,
            PrincipalKind.UserInteractive,
            CapabilityDecisionKind.Denied,
            [CapabilityDecisionReason.InvalidPrincipal],
            null,
            null,
            SecurityScopeClassification.NotApplicable,
            HardDenied: false);
}

public sealed class DenyAllAuthorizationService : IAuthorizationService
{
    public static DenyAllAuthorizationService Instance { get; } = new();

    private DenyAllAuthorizationService()
    {
    }

    public CapabilityDecision Authorize(CallerPrincipal? principal, AuthorizationRequest request) =>
        new(
            request.Capability,
            principal?.Kind ?? PrincipalKind.UserInteractive,
            CapabilityDecisionKind.Denied,
            [CapabilityDecisionReason.RuntimePermissionDenied],
            principal?.Role,
            null,
            SecurityScopeClassification.NotApplicable,
            HardDenied: false);
}
