using System.Security.Cryptography;
using System.Text;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Domain.Extensions;

public enum ExtensionKind
{
    Skill,
    Plugin,
    McpServer
}

public enum ExtensionScope
{
    Application,
    User,
    Project
}

public enum ExtensionActivationState
{
    Inactive,
    Active,
    Invalidated
}

public enum ExtensionActivationEvent
{
    Activate,
    Deactivate,
    ContentChanged,
    TrustRevoked
}

public enum ExtensionActivationRejection
{
    ProjectTrustRequired,
    AlreadyActive,
    AlreadyInactive,
    IllegalTransition
}

public sealed record ExtensionActivationTransition(
    ExtensionActivationState CurrentState,
    ExtensionActivationEvent Event,
    ExtensionActivationState? NextState,
    ExtensionActivationRejection? Rejection)
{
    public bool Allowed => NextState is not null;
}

/// <summary>
/// Pure activation state transitions. Discovery and persistence remain outside Domain.
/// </summary>
public static class ExtensionActivationStateMachine
{
    public static ExtensionActivationTransition Transition(
        ExtensionActivationState current,
        ExtensionActivationEvent @event,
        bool projectTrusted)
    {
        return @event switch
        {
            ExtensionActivationEvent.Activate when !projectTrusted => Reject(
                current, @event, ExtensionActivationRejection.ProjectTrustRequired),
            ExtensionActivationEvent.Activate when current is ExtensionActivationState.Inactive or ExtensionActivationState.Invalidated =>
                Allow(current, @event, ExtensionActivationState.Active),
            ExtensionActivationEvent.Activate => Reject(current, @event, ExtensionActivationRejection.AlreadyActive),
            ExtensionActivationEvent.Deactivate when current is ExtensionActivationState.Active or ExtensionActivationState.Invalidated =>
                Allow(current, @event, ExtensionActivationState.Inactive),
            ExtensionActivationEvent.Deactivate => Reject(current, @event, ExtensionActivationRejection.AlreadyInactive),
            ExtensionActivationEvent.ContentChanged when current == ExtensionActivationState.Active =>
                Allow(current, @event, ExtensionActivationState.Invalidated),
            ExtensionActivationEvent.ContentChanged => Allow(current, @event, current),
            ExtensionActivationEvent.TrustRevoked => Allow(current, @event, ExtensionActivationState.Inactive),
            _ => Reject(current, @event, ExtensionActivationRejection.IllegalTransition)
        };
    }

    private static ExtensionActivationTransition Allow(
        ExtensionActivationState current,
        ExtensionActivationEvent @event,
        ExtensionActivationState next) => new(current, @event, next, null);

    private static ExtensionActivationTransition Reject(
        ExtensionActivationState current,
        ExtensionActivationEvent @event,
        ExtensionActivationRejection rejection) => new(current, @event, null, rejection);
}

/// <summary>
/// Safe manifest data after Infrastructure has parsed project/application files. It deliberately
/// contains no filesystem paths, process arguments, credentials, or executable handles.
/// </summary>
public sealed record ExtensionManifest(
    ExtensionKind Kind,
    string Name,
    string Version,
    string Description,
    string? Instructions,
    IReadOnlyList<string> Scripts,
    IReadOnlyList<Capability> RequestedPermissions,
    IReadOnlyList<string> Dependencies)
{
    public string Id => ExtensionIdentity.Create(Kind, Name);
}

public sealed record ExtensionDescriptor(
    ExtensionManifest Manifest,
    ExtensionScope Scope,
    string ContentDigest)
{
    public string Id => Manifest.Id;
}

public sealed record ExtensionCatalogDiagnostic(string Code, string ExtensionId);

public sealed record ResolvedExtensionCatalog(
    IReadOnlyList<ExtensionDescriptor> Extensions,
    IReadOnlyList<ExtensionCatalogDiagnostic> Diagnostics);

public static class ExtensionIdentity
{
    public static string Create(ExtensionKind kind, string name) =>
        ToWireKind(kind) + ":" + ValidateName(name);

    public static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
            name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException("Extension names must use ASCII letters, digits, '.', '-' or '_'.", nameof(name));
        }

        return name;
    }

    public static string ToWireKind(ExtensionKind kind) => kind switch
    {
        ExtensionKind.Skill => "skill",
        ExtensionKind.Plugin => "plugin",
        ExtensionKind.McpServer => "mcp",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}

public static class ExtensionCatalogResolver
{
    public static ResolvedExtensionCatalog Resolve(IEnumerable<ExtensionDescriptor> discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        var diagnostics = new List<ExtensionCatalogDiagnostic>();
        var valid = discovered
            .Where(ValidateDescriptor)
            .OrderBy(item => item.Manifest.Kind)
            .ThenBy(item => item.Manifest.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Scope)
            .ThenBy(item => item.Manifest.Version, StringComparer.Ordinal)
            .ToArray();

        var selected = new List<ExtensionDescriptor>();
        foreach (var group in valid.GroupBy(item => (item.Manifest.Kind, item.Manifest.Name)))
        {
            var sameScope = group
                .GroupBy(item => item.Scope)
                .Where(item => item.Count() > 1)
                .ToArray();
            if (sameScope.Length > 0)
            {
                diagnostics.Add(new ExtensionCatalogDiagnostic("EXTENSION_DUPLICATE_SCOPE", group.First().Id));
                continue;
            }

            // Persistent Skills have explicit near-scope replacement semantics. Applying the same
            // rule to an identically identified Plugin/MCP descriptor is conservative: ambiguity
            // never yields two executable activation targets.
            selected.Add(group.OrderByDescending(item => ScopeRank(item.Scope)).First());
        }

        return new ResolvedExtensionCatalog(
            selected
                .OrderBy(item => ScopeRank(item.Scope))
                .ThenBy(item => item.Manifest.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Manifest.Version, StringComparer.Ordinal)
                .ThenBy(item => item.Manifest.Kind)
                .ToArray(),
            diagnostics.OrderBy(item => item.Code, StringComparer.Ordinal).ThenBy(item => item.ExtensionId, StringComparer.Ordinal).ToArray());
    }

    public static string ComposeDigest(IEnumerable<(string Id, string Digest)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var canonical = string.Join("\n", values
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.Id + "\u001f" + item.Digest));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static bool ValidateDescriptor(ExtensionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _ = ExtensionIdentity.ValidateName(descriptor.Manifest.Name);
        if (string.IsNullOrWhiteSpace(descriptor.Manifest.Version) ||
            string.IsNullOrWhiteSpace(descriptor.ContentDigest) || descriptor.ContentDigest.Length != 64 ||
            !descriptor.ContentDigest.All(Uri.IsHexDigit))
        {
            return false;
        }

        return descriptor.Manifest.Scripts.All(IsSafeRelativePath) &&
               descriptor.Manifest.Dependencies.All(dependency =>
                   !string.IsNullOrWhiteSpace(dependency) && dependency.Length <= 128 &&
                   dependency.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or ':'));
    }

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or "..");

    private static int ScopeRank(ExtensionScope scope) => scope switch
    {
        ExtensionScope.Application => 0,
        ExtensionScope.User => 1,
        ExtensionScope.Project => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
    };
}
