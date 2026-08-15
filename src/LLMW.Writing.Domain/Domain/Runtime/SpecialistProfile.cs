using System.Text;
using System.Text.Json;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Domain.Runtime;

public enum SpecialistScopeKind
{
    BuiltIn,
    UserLibrary,
    Project
}

public enum SpecialistContextMode
{
    Isolated,
    Fork
}

public enum SpecialistRouteOutcome
{
    Deterministic,
    AmbiguousOrchestratorLater,
    Excluded,
    Disabled
}

public static class SpecialistScopeKindCodec
{
    public static string ToDurableValue(SpecialistScopeKind scope) => scope switch
    {
        SpecialistScopeKind.BuiltIn => "builtin",
        SpecialistScopeKind.UserLibrary => "user",
        SpecialistScopeKind.Project => "project",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
    };

    public static bool TryParse(string? value, out SpecialistScopeKind scope)
    {
        scope = value switch
        {
            "builtin" => SpecialistScopeKind.BuiltIn,
            "user" => SpecialistScopeKind.UserLibrary,
            "project" => SpecialistScopeKind.Project,
            _ => default
        };
        return value is "builtin" or "user" or "project";
    }

    public static bool IsPersistentForbidden(string? value) =>
        value is "task" or "session";
}

public static class WorkflowStageNames
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "story_intent",
        "ending",
        "outline",
        "arc_planning",
        "chapter_planning",
        "draft",
        "review",
        "revision",
        "final_acceptance"
    };
}

public sealed record SpecialistInputContractV1(
    IReadOnlyList<string> Required,
    IReadOnlyList<string> Advisory,
    IReadOnlyList<string> Optional);

public sealed record SpecialistProfileDefinitionV1(
    int SchemaVersion,
    string ProfileId,
    string Name,
    string DisplayName,
    string Description,
    int Version,
    SpecialistScopeKind ScopeKind,
    IReadOnlyList<string> ApplicableWorkflowStages,
    IReadOnlyList<string> WhenToUse,
    IReadOnlyList<string> WhenNotToUse,
    IReadOnlyList<string> ExampleTasks,
    bool AllowOrchestratorAutoCall,
    string BehavioralPrompt,
    IReadOnlyList<string> PrimaryResponsibilities,
    IReadOnlyList<string> OutOfScope,
    bool RequestsEditCapability,
    bool RequestsDelegationCapability,
    SpecialistInputContractV1 InputContract,
    IReadOnlyList<string> OutputContractKeys,
    TaskCompletionContractV1 CompletionContract,
    IReadOnlyList<string> RequestedCapabilities,
    RuntimePermissionMode? PermissionCeiling,
    bool Enabled,
    string? BaseProfileId,
    string? BaseDefinitionDigest,
    string? OverrideProvenance)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record SpecialistValidationError(string Code, string Path, string Message);

public sealed record SpecialistValidationResult(bool IsValid, IReadOnlyList<SpecialistValidationError> Errors);

public static class SpecialistProfileValidator
{
    public static SpecialistValidationResult Validate(SpecialistProfileDefinitionV1 profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = new List<SpecialistValidationError>();
        if (profile.SchemaVersion != SpecialistProfileDefinitionV1.CurrentSchemaVersion)
        {
            errors.Add(new("unsupported-version", "schemaVersion", "Specialist profile schemaVersion must be 1."));
        }

        if (string.IsNullOrWhiteSpace(profile.ProfileId) || string.IsNullOrWhiteSpace(profile.Name) ||
            string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            errors.Add(new("identity-required", "identity", "Profile id, name, and display name are required."));
        }

        if (profile.Version < 1)
        {
            errors.Add(new("invalid-version", "version", "Profile version must be >= 1."));
        }

        foreach (var stage in profile.ApplicableWorkflowStages)
        {
            if (!WorkflowStageNames.All.Contains(stage))
            {
                errors.Add(new("invalid-stage", "applicableWorkflowStages", "Unsupported workflow stage: " + stage));
            }
        }

        foreach (var capability in profile.RequestedCapabilities)
        {
            if (!TryParseCapability(capability, out _))
            {
                errors.Add(new("invalid-capability", "requestedCapabilities", "Unknown capability: " + capability));
            }
        }

        RequireNames(profile.InputContract.Required, "inputContract.required", errors);
        RequireNames(profile.InputContract.Advisory, "inputContract.advisory", errors);
        RequireNames(profile.InputContract.Optional, "inputContract.optional", errors);
        if (profile.CompletionContract.SchemaVersion != TaskCompletionContractV1.CurrentSchemaVersion)
        {
            errors.Add(new("invalid-completion-contract", "completionContract", "Completion contract schemaVersion must be 1."));
        }

        if (profile.OverrideProvenance is not null &&
            profile.OverrideProvenance.Contains("narrativeAuthority", StringComparison.OrdinalIgnoreCase) &&
            profile.OverrideProvenance.Contains("agent_delegated", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new("narrative-authority-grant-forbidden", "overrideProvenance",
                "A Specialist profile cannot grant Narrative Authority."));
        }

        return new SpecialistValidationResult(errors.Count == 0, errors);
    }

    public static IReadOnlyList<Capability> RequestedCeiling(SpecialistProfileDefinitionV1 profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var list = new List<Capability>();
        foreach (var name in profile.RequestedCapabilities)
        {
            if (TryParseCapability(name, out var capability))
            {
                list.Add(capability);
            }
        }

        return list;
    }

    public static bool GrantsCapability(SpecialistProfileDefinitionV1 profile, Capability capability)
    {
        _ = profile;
        _ = capability;
        return false;
    }

    private static void RequireNames(IReadOnlyList<string> values, string path, List<SpecialistValidationError> errors)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add(new("invalid-input-name", path, "Input contract names must be non-empty."));
            }
        }
    }

    private static bool TryParseCapability(string name, out Capability capability)
    {
        foreach (var value in Enum.GetValues<Capability>())
        {
            if (StringComparer.Ordinal.Equals(CapabilityCodec.ToCanonicalName(value), name))
            {
                capability = value;
                return true;
            }
        }

        capability = default;
        return false;
    }
}

public static class SpecialistProfileCanonicalJson
{
    public static string Write(SpecialistProfileDefinitionV1 profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SpecialistProfileDefinitionV1.CurrentSchemaVersion);
            writer.WriteString("profileId", profile.ProfileId);
            writer.WriteString("name", profile.Name);
            writer.WriteString("displayName", profile.DisplayName);
            writer.WriteString("description", profile.Description);
            writer.WriteNumber("version", profile.Version);
            writer.WriteString("scopeKind", SpecialistScopeKindCodec.ToDurableValue(profile.ScopeKind));
            WriteStrings(writer, "applicableWorkflowStages", profile.ApplicableWorkflowStages);
            WriteStrings(writer, "whenToUse", profile.WhenToUse);
            WriteStrings(writer, "whenNotToUse", profile.WhenNotToUse);
            WriteStrings(writer, "exampleTasks", profile.ExampleTasks);
            writer.WriteBoolean("allowOrchestratorAutoCall", profile.AllowOrchestratorAutoCall);
            writer.WriteString("behavioralPrompt", profile.BehavioralPrompt);
            WriteStrings(writer, "primaryResponsibilities", profile.PrimaryResponsibilities);
            WriteStrings(writer, "outOfScope", profile.OutOfScope);
            writer.WriteBoolean("requestsEditCapability", profile.RequestsEditCapability);
            writer.WriteBoolean("requestsDelegationCapability", profile.RequestsDelegationCapability);
            writer.WritePropertyName("inputContract");
            writer.WriteStartObject();
            WriteStrings(writer, "required", profile.InputContract.Required);
            WriteStrings(writer, "advisory", profile.InputContract.Advisory);
            WriteStrings(writer, "optional", profile.InputContract.Optional);
            writer.WriteEndObject();
            WriteStrings(writer, "outputContractKeys", profile.OutputContractKeys);
            writer.WritePropertyName("completionContract");
            writer.WriteRawValue(TaskCompletionContractCanonicalJson.Write(profile.CompletionContract));
            WriteStrings(writer, "requestedCapabilities", profile.RequestedCapabilities);
            if (profile.PermissionCeiling is { } ceiling)
            {
                writer.WriteString("permissionCeiling", RuntimePermissionModeDurableCodec.ToDurableValue(ceiling));
            }

            writer.WriteBoolean("enabled", profile.Enabled);
            if (!string.IsNullOrWhiteSpace(profile.BaseProfileId))
            {
                writer.WriteString("baseProfileId", profile.BaseProfileId);
            }

            if (!string.IsNullOrWhiteSpace(profile.BaseDefinitionDigest))
            {
                writer.WriteString("baseDefinitionDigest", profile.BaseDefinitionDigest);
            }

            if (!string.IsNullOrWhiteSpace(profile.OverrideProvenance))
            {
                writer.WriteString("overrideProvenance", profile.OverrideProvenance);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Digest(SpecialistProfileDefinitionV1 profile) => CanonicalJson.Sha256Hex(Write(profile));

    public static SpecialistProfileDefinitionV1 Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!SpecialistScopeKindCodec.TryParse(root.GetProperty("scopeKind").GetString(), out var scope))
        {
            throw new InvalidOperationException("Unsupported specialist scope.");
        }
        RuntimePermissionMode? ceiling = null;
        if (root.TryGetProperty("permissionCeiling", out var ceilingEl) &&
            RuntimePermissionModeDurableCodec.TryParse(ceilingEl.GetString(), out var parsedCeiling))
        {
            ceiling = parsedCeiling;
        }

        var input = root.GetProperty("inputContract");
        return new SpecialistProfileDefinitionV1(
            root.GetProperty("schemaVersion").GetInt32(),
            root.GetProperty("profileId").GetString() ?? "",
            root.GetProperty("name").GetString() ?? "",
            root.GetProperty("displayName").GetString() ?? "",
            root.GetProperty("description").GetString() ?? "",
            root.GetProperty("version").GetInt32(),
            scope,
            ReadStrings(root, "applicableWorkflowStages"),
            ReadStrings(root, "whenToUse"),
            ReadStrings(root, "whenNotToUse"),
            ReadStrings(root, "exampleTasks"),
            root.GetProperty("allowOrchestratorAutoCall").GetBoolean(),
            root.GetProperty("behavioralPrompt").GetString() ?? "",
            ReadStrings(root, "primaryResponsibilities"),
            ReadStrings(root, "outOfScope"),
            root.GetProperty("requestsEditCapability").GetBoolean(),
            root.GetProperty("requestsDelegationCapability").GetBoolean(),
            new SpecialistInputContractV1(
                ReadStrings(input, "required"),
                ReadStrings(input, "advisory"),
                ReadStrings(input, "optional")),
            ReadStrings(root, "outputContractKeys"),
            root.TryGetProperty("completionContract", out var completionEl)
                ? TaskCompletionContractCanonicalJson.Parse(completionEl.GetRawText())
                : TaskCompletionContractV1.Empty,
            ReadStrings(root, "requestedCapabilities"),
            ceiling,
            root.GetProperty("enabled").GetBoolean(),
            Optional(root, "baseProfileId"),
            Optional(root, "baseDefinitionDigest"),
            Optional(root, "overrideProvenance"));
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values.OrderBy(item => item, StringComparer.Ordinal))
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string[] ReadStrings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
    }

    private static string? Optional(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetString() : null;
}

public sealed record SpecialistRouteDecision(
    SpecialistRouteOutcome Outcome,
    string? ProfileId,
    string Reason);

public static class SpecialistRouter
{
    public static SpecialistRouteDecision RouteDeterministic(
        string workflowStage,
        IReadOnlyList<SpecialistProfileDefinitionV1> profiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowStage);
        ArgumentNullException.ThrowIfNull(profiles);
        var candidates = profiles
            .Where(profile => profile.Enabled)
            .Where(profile => profile.AllowOrchestratorAutoCall)
            .Where(profile => profile.ApplicableWorkflowStages.Contains(workflowStage, StringComparer.Ordinal))
            .Where(profile => !profile.WhenNotToUse.Contains(workflowStage, StringComparer.Ordinal))
            .OrderBy(profile => profile.ProfileId, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 1)
        {
            return new SpecialistRouteDecision(SpecialistRouteOutcome.Deterministic, candidates[0].ProfileId, "workflow-stage");
        }

        if (candidates.Length == 0)
        {
            var excluded = profiles.Any(profile =>
                profile.Enabled && profile.WhenNotToUse.Contains(workflowStage, StringComparer.Ordinal));
            if (excluded)
            {
                return new SpecialistRouteDecision(SpecialistRouteOutcome.Excluded, null, "when-not-to-use");
            }

            var disabled = profiles.Any(profile =>
                !profile.Enabled &&
                profile.ApplicableWorkflowStages.Contains(workflowStage, StringComparer.Ordinal));
            return new SpecialistRouteDecision(
                disabled ? SpecialistRouteOutcome.Disabled : SpecialistRouteOutcome.AmbiguousOrchestratorLater,
                null,
                disabled ? "disabled" : "no-deterministic-match");
        }

        return new SpecialistRouteDecision(SpecialistRouteOutcome.AmbiguousOrchestratorLater, null, "multiple-matches");
    }
}

public sealed record SpecialistTaskPacketV1(
    int SchemaVersion,
    string RunId,
    string TaskId,
    SpecialistContextMode ContextMode,
    string? ProfileId,
    string? TemporaryInstructions,
    IReadOnlyList<string> ProjectInstructionRefs,
    string TaskContractJson,
    IReadOnlyList<string> NarrativeObjectRefs,
    IReadOnlyList<string> RequiredResultArtifactIds,
    IReadOnlyList<string> AdvisoryWarnings,
    ResultFreshnessV1? Freshness)
{
    public const int CurrentSchemaVersion = 1;

    public static SpecialistTaskPacketV1 Isolated(
        string runId,
        string taskId,
        string? profileId,
        string? temporaryInstructions,
        string taskContractJson,
        IReadOnlyList<string> requiredResults,
        IReadOnlyList<string> warnings,
        ResultFreshnessV1? freshness) =>
        new(
            CurrentSchemaVersion,
            runId,
            taskId,
            SpecialistContextMode.Isolated,
            profileId,
            temporaryInstructions,
            [],
            taskContractJson,
            [],
            requiredResults,
            warnings,
            freshness);
}
