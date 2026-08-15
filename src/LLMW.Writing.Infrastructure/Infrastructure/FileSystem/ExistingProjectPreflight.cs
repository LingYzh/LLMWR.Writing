using System.Text.Json;
using LLMW.Writing.Domain.Narrative;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.Infrastructure.FileSystem;

/// <summary>
/// Existing-project open preflight. RequestedPath is a user request, not Project identity.
/// WP12 does not create a Project, migrate a database, or derive ProjectId from a path.
/// </summary>
public static class ExistingProjectPreflight
{
    public const int SupportedFormatVersion = 1;
    public const int SupportedSchemaVersion = 1;
    public const string DescriptorFileName = "project.llmw.json";
    public const string CanonicalDatabaseRelativePath = ".llmw/project.db";
    public const string ForbiddenRootDatabaseFileName = "project.db";

    public static ExistingProjectBindResult TryBind(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return ExistingProjectBindResult.Deny("The requested project path is empty.");
        }

        string canonicalRoot;
        try
        {
            canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return ExistingProjectBindResult.Deny("The requested project path could not be canonicalized.");
        }

        if (!Directory.Exists(canonicalRoot))
        {
            return ExistingProjectBindResult.Deny("The requested project directory does not exist.");
        }

        if (IsReparsePoint(canonicalRoot))
        {
            return ExistingProjectBindResult.Deny("The requested project directory is a reparse point.");
        }

        ProjectPathResolver resolver;
        string descriptorPath;
        try
        {
            resolver = new ProjectPathResolver(canonicalRoot);
            descriptorPath = resolver.Resolve(DescriptorFileName);
        }
        catch (UnauthorizedAccessException)
        {
            return ExistingProjectBindResult.Deny("The requested project path escapes the trusted root or traverses a reparse point.");
        }

        if (!File.Exists(descriptorPath))
        {
            return ExistingProjectBindResult.Deny("project.llmw.json is required to open an existing LLMW project.");
        }

        if (!TryReadDescriptor(descriptorPath, out var descriptor, out var descriptorDeny))
        {
            return ExistingProjectBindResult.Deny(descriptorDeny);
        }

        if (descriptor.FormatVersion != SupportedFormatVersion || descriptor.SchemaVersion != SupportedSchemaVersion)
        {
            return ExistingProjectBindResult.Deny(
                "Unsupported project formatVersion/schemaVersion; refused without mutation.");
        }

        if (!NarrativeChangeDraft.IsCanonicalUuidV7(descriptor.ProjectIdText))
        {
            return ExistingProjectBindResult.Deny("project.llmw.json projectId is not a canonical UUID v7.");
        }

        var projectId = Guid.ParseExact(descriptor.ProjectIdText, "D");
        if (projectId == Guid.Empty)
        {
            return ExistingProjectBindResult.Deny("project.llmw.json projectId is not a canonical UUID v7.");
        }

        string databasePath;
        try
        {
            databasePath = resolver.Resolve(CanonicalDatabaseRelativePath);
        }
        catch (UnauthorizedAccessException)
        {
            return ExistingProjectBindResult.Deny("The canonical .llmw/project.db path escapes the trusted root or traverses a reparse point.");
        }

        if (!File.Exists(databasePath))
        {
            return ExistingProjectBindResult.Deny("Existing .llmw/project.db is required; OpenProject does not create a database.");
        }

        try
        {
            _ = SqliteMigrationRunner.ValidateExistingV1WithoutMutation(databasePath);
        }
        catch (FutureDatabaseVersionException)
        {
            return ExistingProjectBindResult.Deny("Existing project.db is a future schema; refused without mutation.");
        }
        catch (SqliteDatabaseException exception)
        {
            return ExistingProjectBindResult.Deny("Existing .llmw/project.db is not a valid schema v1 database: " + exception.Message);
        }

        return ExistingProjectBindResult.Ok(projectId, canonicalRoot, descriptorPath, databasePath);
    }

    public static string ForbiddenRootDatabasePath(string canonicalRoot) =>
        Path.Combine(canonicalRoot, ForbiddenRootDatabaseFileName);

    private static bool TryReadDescriptor(string descriptorPath, out DescriptorFields descriptor, out string deny)
    {
        descriptor = default;
        deny = "project.llmw.json is not a valid existing-project descriptor.";
        try
        {
            using var stream = File.OpenRead(descriptorPath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = document.RootElement;
            if (!root.TryGetProperty("projectId", out var projectIdElement) ||
                projectIdElement.ValueKind != JsonValueKind.String)
            {
                deny = "project.llmw.json is missing a string projectId.";
                return false;
            }

            if (!TryReadRequiredInt(root, "formatVersion", out var formatVersion) ||
                !TryReadRequiredInt(root, "schemaVersion", out var schemaVersion))
            {
                deny = "project.llmw.json must declare integer formatVersion and schemaVersion.";
                return false;
            }

            descriptor = new DescriptorFields(projectIdElement.GetString() ?? "", formatVersion, schemaVersion);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            deny = "project.llmw.json could not be read without mutation.";
            return false;
        }
    }

    private static bool TryReadRequiredInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out value);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private readonly record struct DescriptorFields(string ProjectIdText, int FormatVersion, int SchemaVersion);
}

public sealed record ExistingProjectBindResult(
    bool Succeeded,
    string? DenyReason,
    Guid ProjectId,
    string CanonicalRoot,
    string DescriptorPath,
    string DatabasePath)
{
    public static ExistingProjectBindResult Ok(
        Guid projectId,
        string canonicalRoot,
        string descriptorPath,
        string databasePath) =>
        new(true, null, projectId, canonicalRoot, descriptorPath, databasePath);

    public static ExistingProjectBindResult Deny(string reason) =>
        new(false, reason, Guid.Empty, "", "", "");
}
