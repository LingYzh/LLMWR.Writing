namespace LLMW.Writing.Domain.Security;

public enum AgentRole
{
    PmMainOrchestrator,
    DataOps,
    StoryPlanner,
    Writer,
    Reviewer,
    Researcher
}

public static class AgentRoleCodec
{
    public static bool TryParse(string value, out AgentRole role)
    {
        role = value switch
        {
            "pm" => AgentRole.PmMainOrchestrator,
            "data_ops" => AgentRole.DataOps,
            "story_planner" => AgentRole.StoryPlanner,
            "writer" => AgentRole.Writer,
            "reviewer" => AgentRole.Reviewer,
            "researcher" => AgentRole.Researcher,
            _ => default
        };
        return value is "pm" or "data_ops" or "story_planner" or "writer" or "reviewer" or "researcher";
    }

    public static string ToDurableValue(AgentRole role) => role switch
    {
        AgentRole.PmMainOrchestrator => "pm",
        AgentRole.DataOps => "data_ops",
        AgentRole.StoryPlanner => "story_planner",
        AgentRole.Writer => "writer",
        AgentRole.Reviewer => "reviewer",
        AgentRole.Researcher => "researcher",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
    };
}

public enum Capability
{
    ProjectFileRead,
    DraftWrite,
    RawWrite,
    StructuredWrite,
    AuthoritySubmit,
    AuthorityReview,
    AuthorityAccept,
    RegistryQuery,
    RegistryMutate,
    WebSearch,
    NetworkRequest,
    ShellExecute,
    ScriptExecute,
    GitExecute,
    McpCall,
    AgentSpawn
}

public static class CapabilityCodec
{
    public static string ToCanonicalName(Capability capability) => capability switch
    {
        Capability.ProjectFileRead => "ProjectFile.Read",
        Capability.DraftWrite => "Draft.Write",
        Capability.RawWrite => "Raw.Write",
        Capability.StructuredWrite => "Structured.Write",
        Capability.AuthoritySubmit => "Authority.Submit",
        Capability.AuthorityReview => "Authority.Review",
        Capability.AuthorityAccept => "Authority.Accept",
        Capability.RegistryQuery => "Registry.Query",
        Capability.RegistryMutate => "Registry.Mutate",
        Capability.WebSearch => "Web.Search",
        Capability.NetworkRequest => "Network.Request",
        Capability.ShellExecute => "Shell.Execute",
        Capability.ScriptExecute => "Script.Execute",
        Capability.GitExecute => "Git.Execute",
        Capability.McpCall => "MCP.Call",
        Capability.AgentSpawn => "Agent.Spawn",
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
    };
}

public enum RoleCapabilityLevel
{
    Allowed,
    Scoped,
    Denied
}

public enum RuntimePermissionMode
{
    Ask,
    AcceptEdits,
    AutoApproveScoped,
    BypassPermissions
}

public enum PrincipalKind
{
    UserInteractive,
    AgentRun,
    CoreInternal
}

public enum CapabilityDecisionKind
{
    Allowed,
    RequiresApproval,
    Denied
}

public enum CapabilityDecisionReason
{
    Allowed,
    ApprovalRequired,
    InvalidPrincipal,
    SessionInvalid,
    SessionExpired,
    SessionRevoked,
    SessionBindingMismatch,
    RunNotFound,
    UnknownAgentRole,
    ProductDenied,
    RoleDenied,
    RuntimePermissionDenied,
    ToolGrantMissing,
    ExtensionGrantMissing,
    TrustRequired,
    PathOutOfScope,
    HardDeny,
    NarrativeAuthorityRequired,
    ExplicitUserTaskRequired
}

[Flags]
public enum HardDeny
{
    None = 0,
    ProjectTrust = 1 << 0,
    ExtensionActivation = 1 << 1,
    PlaintextSecretAccess = 1 << 2,
    AuthorityWorkflowBypass = 1 << 3,
    OutsideProjectDestructive = 1 << 4,
    RegistryOrSystemWrite = 1 << 5,
    SystemDestructiveOperation = 1 << 6
}

public enum SecurityScopeClassification
{
    NotApplicable,
    InScope,
    OutOfScope
}

public sealed record CapabilityEvaluationRequest(
    Capability Capability,
    PrincipalKind PrincipalKind,
    AgentRole? AgentRole,
    RuntimePermissionMode RuntimePermissionMode,
    bool ProductAllowed = false,
    bool ToolGranted = false,
    bool ExtensionGranted = false,
    bool ProjectTrusted = false,
    SecurityScopeClassification Scope = SecurityScopeClassification.NotApplicable,
    HardDeny HardDeny = HardDeny.None,
    bool NarrativeAuthorityAvailable = false,
    bool ExplicitUserTask = false);

public sealed record CapabilityDecision(
    Capability RequestedCapability,
    PrincipalKind PrincipalKind,
    CapabilityDecisionKind Decision,
    IReadOnlyList<CapabilityDecisionReason> Reasons,
    AgentRole? EvaluatedRole,
    RoleCapabilityLevel? RoleMaximum,
    SecurityScopeClassification Scope,
    bool HardDenied)
{
    public bool IsAllowed => Decision == CapabilityDecisionKind.Allowed;
}

public static class RoleCapabilityMatrix
{
    public static RoleCapabilityLevel Get(AgentRole role, Capability capability) => role switch
    {
        AgentRole.PmMainOrchestrator => Pm(capability),
        AgentRole.DataOps => DataOps(capability),
        AgentRole.StoryPlanner => StoryPlanner(capability),
        AgentRole.Writer => Writer(capability),
        AgentRole.Reviewer => Reviewer(capability),
        AgentRole.Researcher => Researcher(capability),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
    };

    private static RoleCapabilityLevel Pm(Capability capability) => capability switch
    {
        Capability.ProjectFileRead or Capability.RegistryQuery or Capability.WebSearch or Capability.AgentSpawn =>
            RoleCapabilityLevel.Allowed,
        Capability.DraftWrite or Capability.RawWrite or Capability.StructuredWrite or Capability.AuthoritySubmit or
        Capability.AuthorityAccept or Capability.NetworkRequest or Capability.ShellExecute or Capability.ScriptExecute or
        Capability.GitExecute or Capability.McpCall => RoleCapabilityLevel.Scoped,
        Capability.AuthorityReview or Capability.RegistryMutate => RoleCapabilityLevel.Denied,
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
    };

    private static RoleCapabilityLevel DataOps(Capability capability) => capability switch
    {
        Capability.ProjectFileRead or Capability.RawWrite or Capability.StructuredWrite or Capability.RegistryQuery or
        Capability.RegistryMutate or Capability.WebSearch => RoleCapabilityLevel.Allowed,
        Capability.NetworkRequest or Capability.ShellExecute or Capability.ScriptExecute or Capability.McpCall or
        Capability.AgentSpawn => RoleCapabilityLevel.Scoped,
        Capability.DraftWrite or Capability.AuthoritySubmit or Capability.AuthorityReview or Capability.AuthorityAccept or
        Capability.GitExecute => RoleCapabilityLevel.Denied,
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
    };

    private static RoleCapabilityLevel StoryPlanner(Capability capability) => capability switch
    {
        Capability.ProjectFileRead or Capability.StructuredWrite or Capability.RegistryQuery or Capability.WebSearch =>
            RoleCapabilityLevel.Allowed,
        Capability.NetworkRequest or Capability.ShellExecute or Capability.ScriptExecute or Capability.McpCall or
        Capability.AgentSpawn => RoleCapabilityLevel.Scoped,
        Capability.DraftWrite or Capability.RawWrite or Capability.AuthoritySubmit or Capability.AuthorityReview or
        Capability.AuthorityAccept or Capability.RegistryMutate or Capability.GitExecute => RoleCapabilityLevel.Denied,
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
    };

    private static RoleCapabilityLevel Writer(Capability capability) => capability switch
    {
        Capability.ProjectFileRead or Capability.DraftWrite or Capability.RegistryQuery or Capability.WebSearch =>
            RoleCapabilityLevel.Allowed,
        Capability.AuthoritySubmit or Capability.NetworkRequest or Capability.ShellExecute or Capability.ScriptExecute or
        Capability.McpCall or Capability.AgentSpawn => RoleCapabilityLevel.Scoped,
        Capability.RawWrite or Capability.StructuredWrite or Capability.AuthorityReview or Capability.AuthorityAccept or
        Capability.RegistryMutate or Capability.GitExecute => RoleCapabilityLevel.Denied,
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
    };

    private static RoleCapabilityLevel Reviewer(Capability capability) => capability switch
    {
        Capability.ProjectFileRead or Capability.AuthorityReview or Capability.RegistryQuery or Capability.WebSearch =>
            RoleCapabilityLevel.Allowed,
        Capability.NetworkRequest or Capability.ShellExecute or Capability.ScriptExecute or Capability.McpCall or
        Capability.AgentSpawn => RoleCapabilityLevel.Scoped,
        Capability.DraftWrite or Capability.RawWrite or Capability.StructuredWrite or Capability.AuthoritySubmit or
        Capability.AuthorityAccept or Capability.RegistryMutate or Capability.GitExecute => RoleCapabilityLevel.Denied,
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
    };

    private static RoleCapabilityLevel Researcher(Capability capability) => capability switch
    {
        Capability.ProjectFileRead or Capability.RegistryQuery or Capability.WebSearch => RoleCapabilityLevel.Allowed,
        Capability.RawWrite or Capability.NetworkRequest or Capability.ShellExecute or Capability.ScriptExecute or
        Capability.McpCall => RoleCapabilityLevel.Scoped,
        Capability.DraftWrite or Capability.StructuredWrite or Capability.AuthoritySubmit or Capability.AuthorityReview or
        Capability.AuthorityAccept or Capability.RegistryMutate or Capability.GitExecute or Capability.AgentSpawn =>
            RoleCapabilityLevel.Denied,
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
    };
}

public static class CapabilityEvaluator
{
    public static CapabilityDecision Evaluate(CapabilityEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RoleCapabilityLevel? roleMaximum = request.PrincipalKind == PrincipalKind.AgentRun && request.AgentRole is not null
            ? RoleCapabilityMatrix.Get(request.AgentRole.Value, request.Capability)
            : null;

        if (request.PrincipalKind == PrincipalKind.AgentRun && request.AgentRole is null)
        {
            return Denied(request, roleMaximum, CapabilityDecisionReason.UnknownAgentRole);
        }

        if (!request.ProductAllowed)
        {
            return Denied(request, roleMaximum, CapabilityDecisionReason.ProductDenied);
        }

        if (roleMaximum == RoleCapabilityLevel.Denied)
        {
            return Denied(request, roleMaximum, CapabilityDecisionReason.RoleDenied);
        }

        if (request.HardDeny != HardDeny.None)
        {
            return Denied(request, roleMaximum, CapabilityDecisionReason.HardDeny, hardDenied: true);
        }

        if (!request.ProjectTrusted)
        {
            return Denied(request, roleMaximum, CapabilityDecisionReason.TrustRequired);
        }

        if (request.Scope == SecurityScopeClassification.OutOfScope)
        {
            return Denied(request, roleMaximum, CapabilityDecisionReason.PathOutOfScope);
        }

        if (!request.ToolGranted)
        {
            return Denied(request, roleMaximum, CapabilityDecisionReason.ToolGrantMissing);
        }

        if (!request.ExtensionGranted)
        {
            return Denied(request, roleMaximum, CapabilityDecisionReason.ExtensionGrantMissing);
        }

        if (request.Capability == Capability.AuthorityAccept &&
            request.PrincipalKind == PrincipalKind.AgentRun &&
            !request.NarrativeAuthorityAvailable)
        {
            return Denied(request, roleMaximum, CapabilityDecisionReason.NarrativeAuthorityRequired);
        }

        if (request.Capability == Capability.GitExecute &&
            request.PrincipalKind == PrincipalKind.AgentRun &&
            !request.ExplicitUserTask)
        {
            return Denied(request, roleMaximum, CapabilityDecisionReason.ExplicitUserTaskRequired);
        }

        if (roleMaximum == RoleCapabilityLevel.Scoped && !ScopedPermissionAllows(request))
        {
            return new CapabilityDecision(
                request.Capability,
                request.PrincipalKind,
                CapabilityDecisionKind.RequiresApproval,
                [CapabilityDecisionReason.ApprovalRequired],
                request.AgentRole,
                roleMaximum,
                request.Scope,
                HardDenied: false);
        }

        return new CapabilityDecision(
            request.Capability,
            request.PrincipalKind,
            CapabilityDecisionKind.Allowed,
            [CapabilityDecisionReason.Allowed],
            request.AgentRole,
            roleMaximum,
            request.Scope,
            HardDenied: false);
    }

    private static bool ScopedPermissionAllows(CapabilityEvaluationRequest request) =>
        request.RuntimePermissionMode switch
        {
            RuntimePermissionMode.Ask => false,
            RuntimePermissionMode.AcceptEdits => request.Capability is Capability.DraftWrite or Capability.RawWrite or
                Capability.StructuredWrite or Capability.AuthoritySubmit,
            RuntimePermissionMode.AutoApproveScoped => true,
            RuntimePermissionMode.BypassPermissions => true,
            _ => false
        };

    private static CapabilityDecision Denied(
        CapabilityEvaluationRequest request,
        RoleCapabilityLevel? roleMaximum,
        CapabilityDecisionReason reason,
        bool hardDenied = false) =>
        new(
            request.Capability,
            request.PrincipalKind,
            CapabilityDecisionKind.Denied,
            [reason],
            request.AgentRole,
            roleMaximum,
            request.Scope,
            hardDenied);
}
