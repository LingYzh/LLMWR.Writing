using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Security;

public sealed class CallerPrincipal
{
    private CallerPrincipal(
        PrincipalKind kind,
        string trustedInstanceId,
        string? runId,
        AgentRole? role,
        RuntimePermissionMode runtimePermissionMode,
        string? sessionHandleId,
        ProjectScope? projectScope)
    {
        Kind = kind;
        TrustedInstanceId = trustedInstanceId;
        RunId = runId;
        Role = role;
        RuntimePermissionMode = runtimePermissionMode;
        SessionHandleId = sessionHandleId;
        ProjectScope = projectScope;
    }

    public PrincipalKind Kind { get; }

    public string TrustedInstanceId { get; }

    public string? RunId { get; }

    public AgentRole? Role { get; }

    public RuntimePermissionMode RuntimePermissionMode { get; }

    public string? SessionHandleId { get; }

    public ProjectScope? ProjectScope { get; }

    internal static CallerPrincipal CreateAgentRun(
        string runId,
        AgentRole role,
        RuntimePermissionMode runtimePermissionMode,
        string sessionHandleId,
        AuthenticatedChannelContext channel) =>
        new(
            PrincipalKind.AgentRun,
            channel.ChannelInstanceId,
            runId,
            role,
            runtimePermissionMode,
            sessionHandleId,
            channel.ProjectScope);

    internal static CallerPrincipal CreateCoreInternal(string compositionInstanceId) =>
        new(PrincipalKind.CoreInternal, compositionInstanceId, null, null, RuntimePermissionMode.Ask, null, null);

    internal static CallerPrincipal CreateUserInteractive(string nativeHostInstanceId) =>
        new(PrincipalKind.UserInteractive, nativeHostInstanceId, null, null, RuntimePermissionMode.Ask, null, null);

    public override string ToString() => Kind switch
    {
        PrincipalKind.AgentRun => $"AgentRun:{RunId}:{Role}",
        PrincipalKind.UserInteractive => "UserInteractive",
        PrincipalKind.CoreInternal => "CoreInternal",
        _ => "InvalidPrincipal"
    };
}

/// <summary>
/// This is a trusted native/Core composition seam, not an IPC payload converter.
/// </summary>
public sealed class TrustedNativePrincipalSource
{
    private readonly string nativeHostInstanceId;

    public TrustedNativePrincipalSource(string nativeHostInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeHostInstanceId);
        this.nativeHostInstanceId = nativeHostInstanceId;
    }

    public CallerPrincipal ResolveUserInteractive() =>
        CallerPrincipal.CreateUserInteractive(nativeHostInstanceId);
}

public sealed record ProjectScope
{
    public ProjectScope(Guid projectId, string workspaceInstanceId)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project scope requires a non-empty Project UUID.", nameof(projectId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceInstanceId);
        if (workspaceInstanceId.Length > 128 || workspaceInstanceId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException(
                "Workspace instance identity must use ASCII letters, digits, '-' or '_'.",
                nameof(workspaceInstanceId));
        }

        ProjectId = projectId;
        WorkspaceInstanceId = workspaceInstanceId;
    }

    public Guid ProjectId { get; }

    public string WorkspaceInstanceId { get; }

    public string ToCanonicalValue() => $"v1:{ProjectId:D}:{WorkspaceInstanceId}".ToLowerInvariant();

    public static bool TryParseCanonical(string value, out ProjectScope scope)
    {
        scope = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(':');
        if (parts.Length != 3 || !parts[0].Equals("v1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Guid.TryParse(parts[1], out var projectId) || projectId == Guid.Empty)
        {
            return false;
        }

        try
        {
            scope = new ProjectScope(projectId, parts[2]);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public enum AuthenticatedClientKind
{
    NativeUi,
    AgentRuntime,
    Worker
}

/// <summary>
/// Created by the authenticated transport/composition layer; never deserialized from an ordinary command payload.
/// </summary>
public sealed record AuthenticatedChannelContext(
    string ChannelInstanceId,
    AuthenticatedClientKind ClientKind,
    string WorkerInstanceId,
    ProjectScope ProjectScope,
    string? BoundRunId = null)
{
    public void ValidateForAgentRun()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ChannelInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkerInstanceId);
        ArgumentNullException.ThrowIfNull(ProjectScope);
        if (ClientKind is not (AuthenticatedClientKind.AgentRuntime or AuthenticatedClientKind.Worker))
        {
            throw new ArgumentException("Agent Run sessions require an authenticated Runtime or Worker channel.");
        }
    }
}
