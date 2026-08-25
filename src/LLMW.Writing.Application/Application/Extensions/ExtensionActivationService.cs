using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Extensions;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Extensions;

public enum ExtensionFailureCode
{
    InvalidRequest,
    ProjectBindingInvalid,
    MutationDenied,
    ProjectTrustRequired,
    ExtensionNotFound,
    ExtensionInvalid,
    ExtensionDependencyInactive,
    OperationIdentityConflict,
    StorageFailure
}

public sealed record ExtensionFailure(ExtensionFailureCode Code);

public sealed record ExtensionOperationRequest(string ProjectId, string OperationId);

public sealed record ActivateExtensionCommand(string ProjectId, string ExtensionId, string OperationId);

public sealed record ExtensionActivationRecord(
    ExtensionActivationState State,
    string? ContentDigest);

public sealed record ExtensionOperationReceipt(
    string Fingerprint,
    string? ExtensionId,
    bool Activated,
    bool ProjectTrusted);

public sealed record ExtensionSecurityState(
    bool ProjectTrusted,
    IReadOnlyDictionary<string, ExtensionActivationRecord> Activations,
    IReadOnlyDictionary<string, ExtensionOperationReceipt> Operations)
{
    public static ExtensionSecurityState Empty { get; } = new(
        false,
        new Dictionary<string, ExtensionActivationRecord>(StringComparer.Ordinal),
        new Dictionary<string, ExtensionOperationReceipt>(StringComparer.Ordinal));
}

public sealed record ProjectInstructionSnapshot(
    string AgentsDigest,
    IReadOnlyList<string> Instructions,
    IReadOnlyList<string> Diagnostics);

public sealed record ExtensionCatalogSnapshot(
    ResolvedExtensionCatalog Catalog,
    ProjectInstructionSnapshot Instructions);

/// <summary>
/// File discovery belongs to Infrastructure. This Application port never exposes an extension
/// directory, script path, process argument, credential, or filesystem handle.
/// </summary>
public interface IExtensionCatalog
{
    /// <summary>
    /// The relative target is Core-derived task context, never an IPC filesystem path. An empty
    /// target selects the project root instruction scope.
    /// </summary>
    ExtensionCatalogSnapshot Discover(string relativeProjectPath = "");
}

/// <summary>
/// Per-user trust/activation state is deliberately separate from project Authority data and is
/// keyed by trusted Core composition, never by renderer input.
/// </summary>
public interface IExtensionSecurityStateStore
{
    ExtensionSecurityState Load();

    void Save(ExtensionSecurityState state);
}

public sealed record ExtensionActivationView(
    string ExtensionId,
    string Kind,
    string Scope,
    string Version,
    bool Activated,
    bool Invalidated);

public sealed record ExtensionCatalogView(
    bool ProjectTrusted,
    IReadOnlyList<ExtensionActivationView> Extensions,
    IReadOnlyList<string> Diagnostics);

public sealed record ExtensionCommandResult<T>(T? Value, ExtensionFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public sealed record ExtensionActivationResult(
    string ExtensionId,
    bool Activated,
    bool ProjectTrusted);

public sealed record ExtensionFreshnessSnapshot(
    string AgentsDigest,
    IReadOnlyDictionary<string, string> SkillDigests);

public sealed record ExtensionPromptInputs(
    IReadOnlyList<string> ProjectInstructions,
    IReadOnlyList<(string SkillId, string Text)> Skills,
    ExtensionFreshnessSnapshot Freshness,
    IReadOnlyList<string> Diagnostics);

public interface IAgentExtensionFreshnessSource
{
    ExtensionFreshnessSnapshot GetCurrent();
}

public sealed class NoAgentExtensionFreshnessSource : IAgentExtensionFreshnessSource
{
    public static NoAgentExtensionFreshnessSource Instance { get; } = new();

    private NoAgentExtensionFreshnessSource()
    {
    }

    public ExtensionFreshnessSnapshot GetCurrent() =>
        new("", new Dictionary<string, string>(StringComparer.Ordinal));
}

/// <summary>
/// Core-side orchestration for discovery, independent Project Trust, explicit activation, content
/// hash invalidation, and replay-safe user mutations. It never executes project extension content.
/// </summary>
public sealed class ExtensionActivationService : IAgentExtensionFreshnessSource
{
    private readonly IExtensionCatalog catalog;
    private readonly IExtensionSecurityStateStore stateStore;
    private readonly string projectId;
    private readonly object gate = new();

    public ExtensionActivationService(
        IExtensionCatalog catalog,
        IExtensionSecurityStateStore stateStore,
        string projectId)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        if (!Guid.TryParseExact(projectId, "D", out _))
        {
            throw new ArgumentException("Project identity must be a canonical UUID.", nameof(projectId));
        }

        this.projectId = projectId;
    }

    public string ProjectId => projectId;

    public ExtensionCommandResult<ExtensionActivationResult> TrustProject(
        CallerPrincipal? principal,
        ExtensionOperationRequest request) =>
        MutateTrust(principal, request, trusted: true);

    public ExtensionCommandResult<ExtensionActivationResult> RevokeProjectTrust(
        CallerPrincipal? principal,
        ExtensionOperationRequest request) =>
        MutateTrust(principal, request, trusted: false);

    public ExtensionCommandResult<ExtensionActivationResult> Activate(
        CallerPrincipal? principal,
        ActivateExtensionCommand command) =>
        MutateActivation(principal, command, activate: true);

    public ExtensionCommandResult<ExtensionActivationResult> Deactivate(
        CallerPrincipal? principal,
        ActivateExtensionCommand command) =>
        MutateActivation(principal, command, activate: false);

    public ExtensionCatalogView List()
    {
        lock (gate)
        {
            var snapshot = catalog.Discover();
            var state = ReconcileContentChanges(stateStore.Load(), snapshot.Catalog);
            var extensions = snapshot.Catalog.Extensions
                .Select(descriptor =>
                {
                    var record = state.Activations.GetValueOrDefault(descriptor.Id);
                    return new ExtensionActivationView(
                        descriptor.Id,
                        ExtensionIdentity.ToWireKind(descriptor.Manifest.Kind),
                        descriptor.Scope.ToString().ToLowerInvariant(),
                        descriptor.Manifest.Version,
                        record?.State == ExtensionActivationState.Active &&
                        StringComparer.Ordinal.Equals(record.ContentDigest, descriptor.ContentDigest),
                        record?.State == ExtensionActivationState.Invalidated);
                })
                .ToArray();
            return new ExtensionCatalogView(
                state.ProjectTrusted,
                extensions,
                snapshot.Catalog.Diagnostics.Select(item => item.Code).Concat(snapshot.Instructions.Diagnostics)
                    .OrderBy(item => item, StringComparer.Ordinal).ToArray());
        }
    }

    public ExtensionPromptInputs GetPromptInputs(string relativeProjectPath = "")
    {
        lock (gate)
        {
            var snapshot = catalog.Discover(relativeProjectPath);
            var state = ReconcileContentChanges(stateStore.Load(), snapshot.Catalog);
            var activeSkills = snapshot.Catalog.Extensions
                .Where(descriptor => descriptor.Manifest.Kind == ExtensionKind.Skill &&
                                     state.Activations.TryGetValue(descriptor.Id, out var activation) &&
                                     activation.State == ExtensionActivationState.Active &&
                                     StringComparer.Ordinal.Equals(activation.ContentDigest, descriptor.ContentDigest))
                .OrderBy(descriptor => ScopeRank(descriptor.Scope))
                .ThenBy(descriptor => descriptor.Manifest.Name, StringComparer.Ordinal)
                .ThenBy(descriptor => descriptor.Manifest.Version, StringComparer.Ordinal)
                .ToArray();
            var skills = activeSkills
                .Where(descriptor => !string.IsNullOrWhiteSpace(descriptor.Manifest.Instructions))
                .Select(descriptor => (descriptor.Manifest.Name, descriptor.Manifest.Instructions!))
                .ToArray();
            var skillDigests = activeSkills.ToDictionary(
                descriptor => descriptor.Manifest.Name,
                descriptor => descriptor.ContentDigest,
                StringComparer.Ordinal);
            return new ExtensionPromptInputs(
                snapshot.Instructions.Instructions,
                skills,
                new ExtensionFreshnessSnapshot(snapshot.Instructions.AgentsDigest, skillDigests),
                snapshot.Catalog.Diagnostics.Select(item => item.Code).Concat(snapshot.Instructions.Diagnostics)
                    .OrderBy(item => item, StringComparer.Ordinal).ToArray());
        }
    }

    public ExtensionFreshnessSnapshot GetCurrent() => GetPromptInputs().Freshness;

    private ExtensionCommandResult<ExtensionActivationResult> MutateTrust(
        CallerPrincipal? principal,
        ExtensionOperationRequest request,
        bool trusted)
    {
        ArgumentNullException.ThrowIfNull(request);
        var denial = ValidateMutation(principal, request.ProjectId, request.OperationId);
        if (denial is not null)
        {
            return denial;
        }

        lock (gate)
        {
            var state = stateStore.Load();
            var fingerprint = trusted ? "trust" : "revoke-trust";
            if (TryReplay(state, request.OperationId, fingerprint, out var replay))
            {
                return replay;
            }

            var activations = CopyActivations(state.Activations);
            if (!trusted)
            {
                foreach (var pair in activations.ToArray())
                {
                    var transition = ExtensionActivationStateMachine.Transition(
                        pair.Value.State, ExtensionActivationEvent.TrustRevoked, projectTrusted: false);
                    activations[pair.Key] = pair.Value with { State = transition.NextState!.Value };
                }
            }

            var result = new ExtensionActivationResult("project", Activated: false, ProjectTrusted: trusted);
            SaveWithOperation(state, trusted, activations, request.OperationId, fingerprint, result);
            return Success(result);
        }
    }

    private ExtensionCommandResult<ExtensionActivationResult> MutateActivation(
        CallerPrincipal? principal,
        ActivateExtensionCommand command,
        bool activate)
    {
        ArgumentNullException.ThrowIfNull(command);
        var denial = ValidateMutation(principal, command.ProjectId, command.OperationId);
        if (denial is not null)
        {
            return denial;
        }

        if (string.IsNullOrWhiteSpace(command.ExtensionId) || command.ExtensionId.Length > 160)
        {
            return Fail<ExtensionActivationResult>(ExtensionFailureCode.InvalidRequest);
        }

        lock (gate)
        {
            var snapshot = catalog.Discover();
            var state = ReconcileContentChanges(stateStore.Load(), snapshot.Catalog);
            var fingerprint = (activate ? "activate" : "deactivate") + "\u001f" + command.ExtensionId;
            if (TryReplay(state, command.OperationId, fingerprint, out var replay))
            {
                return replay;
            }

            var descriptor = snapshot.Catalog.Extensions.SingleOrDefault(item =>
                StringComparer.Ordinal.Equals(item.Id, command.ExtensionId));
            if (descriptor is null)
            {
                return Fail<ExtensionActivationResult>(ExtensionFailureCode.ExtensionNotFound);
            }

            if (snapshot.Catalog.Diagnostics.Any(item => StringComparer.Ordinal.Equals(item.ExtensionId, descriptor.Id)))
            {
                return Fail<ExtensionActivationResult>(ExtensionFailureCode.ExtensionInvalid);
            }

            var activations = CopyActivations(state.Activations);
            var current = activations.GetValueOrDefault(descriptor.Id) ??
                new ExtensionActivationRecord(ExtensionActivationState.Inactive, null);
            if (activate && !DependenciesActive(descriptor, snapshot.Catalog.Extensions, activations))
            {
                return Fail<ExtensionActivationResult>(ExtensionFailureCode.ExtensionDependencyInactive);
            }

            var @event = activate ? ExtensionActivationEvent.Activate : ExtensionActivationEvent.Deactivate;
            var transition = ExtensionActivationStateMachine.Transition(current.State, @event, state.ProjectTrusted);
            if (!transition.Allowed)
            {
                return transition.Rejection == ExtensionActivationRejection.ProjectTrustRequired
                    ? Fail<ExtensionActivationResult>(ExtensionFailureCode.ProjectTrustRequired)
                    : Success(new ExtensionActivationResult(
                        descriptor.Id,
                        current.State == ExtensionActivationState.Active,
                        state.ProjectTrusted));
            }

            var next = current with
            {
                State = transition.NextState!.Value,
                ContentDigest = transition.NextState == ExtensionActivationState.Inactive ? current.ContentDigest : descriptor.ContentDigest
            };
            activations[descriptor.Id] = next;
            var result = new ExtensionActivationResult(
                descriptor.Id,
                next.State == ExtensionActivationState.Active,
                state.ProjectTrusted);
            SaveWithOperation(state, state.ProjectTrusted, activations, command.OperationId, fingerprint, result);
            return Success(result);
        }
    }

    private ExtensionSecurityState ReconcileContentChanges(
        ExtensionSecurityState original,
        ResolvedExtensionCatalog catalogSnapshot)
    {
        var descriptors = catalogSnapshot.Extensions.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var activations = CopyActivations(original.Activations);
        var changed = false;
        foreach (var pair in activations.ToArray())
        {
            if (!descriptors.TryGetValue(pair.Key, out var descriptor) ||
                !StringComparer.Ordinal.Equals(pair.Value.ContentDigest, descriptor.ContentDigest))
            {
                var transition = ExtensionActivationStateMachine.Transition(
                    pair.Value.State, ExtensionActivationEvent.ContentChanged, original.ProjectTrusted);
                activations[pair.Key] = pair.Value with
                {
                    State = transition.NextState!.Value,
                    ContentDigest = descriptor?.ContentDigest ?? pair.Value.ContentDigest
                };
                changed = true;
            }
        }

        if (!changed)
        {
            return original;
        }

        var updated = new ExtensionSecurityState(original.ProjectTrusted, activations, CopyOperations(original.Operations));
        stateStore.Save(updated);
        return updated;
    }

    private void SaveWithOperation(
        ExtensionSecurityState prior,
        bool projectTrusted,
        Dictionary<string, ExtensionActivationRecord> activations,
        string operationId,
        string fingerprint,
        ExtensionActivationResult result)
    {
        var operations = CopyOperations(prior.Operations);
        operations.Add(operationId, new ExtensionOperationReceipt(
            fingerprint,
            result.ExtensionId == "project" ? null : result.ExtensionId,
            result.Activated,
            result.ProjectTrusted));
        stateStore.Save(new ExtensionSecurityState(projectTrusted, activations, operations));
    }

    private static bool DependenciesActive(
        ExtensionDescriptor descriptor,
        IReadOnlyList<ExtensionDescriptor> descriptors,
        Dictionary<string, ExtensionActivationRecord> activations)
    {
        foreach (var dependency in descriptor.Manifest.Dependencies)
        {
            var resolved = descriptors.SingleOrDefault(candidate =>
                StringComparer.Ordinal.Equals(candidate.Id, dependency) ||
                StringComparer.Ordinal.Equals(candidate.Manifest.Name, dependency));
            if (resolved is null || !activations.TryGetValue(resolved.Id, out var activation) ||
                activation.State != ExtensionActivationState.Active ||
                !StringComparer.Ordinal.Equals(activation.ContentDigest, resolved.ContentDigest))
            {
                return false;
            }
        }

        return true;
    }

    private ExtensionCommandResult<ExtensionActivationResult>? ValidateMutation(
        CallerPrincipal? principal,
        string requestedProjectId,
        string operationId)
    {
        if (principal is not { Kind: PrincipalKind.UserInteractive })
        {
            return Fail<ExtensionActivationResult>(ExtensionFailureCode.MutationDenied);
        }

        if (!StringComparer.Ordinal.Equals(projectId, requestedProjectId))
        {
            return Fail<ExtensionActivationResult>(ExtensionFailureCode.ProjectBindingInvalid);
        }

        if (!Guid.TryParseExact(operationId, "D", out _))
        {
            return Fail<ExtensionActivationResult>(ExtensionFailureCode.InvalidRequest);
        }

        return null;
    }

    private static bool TryReplay(
        ExtensionSecurityState state,
        string operationId,
        string fingerprint,
        out ExtensionCommandResult<ExtensionActivationResult> result)
    {
        if (!state.Operations.TryGetValue(operationId, out var prior))
        {
            result = null!;
            return false;
        }

        if (!StringComparer.Ordinal.Equals(prior.Fingerprint, fingerprint))
        {
            result = Fail<ExtensionActivationResult>(ExtensionFailureCode.OperationIdentityConflict);
            return true;
        }

        result = Success(new ExtensionActivationResult(
            prior.ExtensionId ?? "project",
            prior.Activated,
            prior.ProjectTrusted));
        return true;
    }

    private static Dictionary<string, ExtensionActivationRecord> CopyActivations(
        IReadOnlyDictionary<string, ExtensionActivationRecord> source) =>
        source.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    private static Dictionary<string, ExtensionOperationReceipt> CopyOperations(
        IReadOnlyDictionary<string, ExtensionOperationReceipt> source) =>
        source.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    private static int ScopeRank(ExtensionScope scope) => scope switch
    {
        ExtensionScope.Application => 0,
        ExtensionScope.User => 1,
        ExtensionScope.Project => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
    };

    private static ExtensionCommandResult<T> Success<T>(T value) => new(value, null);

    private static ExtensionCommandResult<T> Fail<T>(ExtensionFailureCode code) =>
        new(default, new ExtensionFailure(code));
}

public sealed class ExtensionActivationServiceHolder
{
    private ExtensionActivationService? current;

    public ExtensionActivationService? Current => Volatile.Read(ref current);

    public void PublishOnce(ExtensionActivationService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (Interlocked.CompareExchange(ref current, service, null) is not null)
        {
            throw new InvalidOperationException("Extension activation service is already published.");
        }
    }

    public bool TryAbandon(ExtensionActivationService expected) =>
        ReferenceEquals(Interlocked.CompareExchange(ref current, null, expected), expected);
}
