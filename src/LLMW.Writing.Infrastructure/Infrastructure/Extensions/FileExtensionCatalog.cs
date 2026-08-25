using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMW.Writing.Application.Extensions;
using LLMW.Writing.Domain.Extensions;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Infrastructure.Extensions;

/// <summary>
/// Trusted composition supplies the three roots. The scanner reads metadata and instruction text
/// only; it never loads an assembly, launches a process, evaluates a script, or follows a reparse
/// point.
/// </summary>
public sealed record ExtensionCatalogRoots(
    string ApplicationRoot,
    string UserRoot,
    string ProjectExtensionsRoot,
    string ProjectInstructionRoot);

public sealed class FileExtensionCatalog : IExtensionCatalog
{
    public const string ManifestFileName = "extension.llmw.json";
    public const string AgentsFileName = "AGENTS.md";
    public const string ClaudeFileName = "CLAUDE.md";

    private readonly ExtensionCatalogRoots roots;

    public FileExtensionCatalog(ExtensionCatalogRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        this.roots = roots with
        {
            ApplicationRoot = CanonicalRoot(roots.ApplicationRoot, nameof(roots.ApplicationRoot)),
            UserRoot = CanonicalRoot(roots.UserRoot, nameof(roots.UserRoot)),
            ProjectExtensionsRoot = CanonicalRoot(roots.ProjectExtensionsRoot, nameof(roots.ProjectExtensionsRoot)),
            ProjectInstructionRoot = CanonicalRoot(roots.ProjectInstructionRoot, nameof(roots.ProjectInstructionRoot))
        };
    }

    public ExtensionCatalogSnapshot Discover(string relativeProjectPath = "") =>
        DiscoverForProjectPath(relativeProjectPath);

    public ExtensionCatalogSnapshot DiscoverForProjectPath(string relativeProjectPath)
    {
        var diagnostics = new List<ExtensionCatalogDiagnostic>();
        var descriptors = new List<ExtensionDescriptor>();
        ScanRoot(roots.ApplicationRoot, ExtensionScope.Application, descriptors, diagnostics);
        ScanRoot(roots.UserRoot, ExtensionScope.User, descriptors, diagnostics);
        ScanRoot(roots.ProjectExtensionsRoot, ExtensionScope.Project, descriptors, diagnostics);

        ResolvedExtensionCatalog catalog;
        try
        {
            var resolved = ExtensionCatalogResolver.Resolve(descriptors);
            catalog = new ResolvedExtensionCatalog(
                resolved.Extensions,
                resolved.Diagnostics.Concat(diagnostics)
                    .OrderBy(item => item.Code, StringComparer.Ordinal)
                    .ThenBy(item => item.ExtensionId, StringComparer.Ordinal)
                    .ToArray());
        }
        catch (ArgumentException)
        {
            catalog = new ResolvedExtensionCatalog([], [new ExtensionCatalogDiagnostic("EXTENSION_MANIFEST_INVALID", "unknown")]);
        }

        return new ExtensionCatalogSnapshot(catalog, ReadInstructions(relativeProjectPath));
    }

    private static void ScanRoot(
        string root,
        ExtensionScope scope,
        List<ExtensionDescriptor> descriptors,
        List<ExtensionCatalogDiagnostic> diagnostics)
    {
        if (!Directory.Exists(root) || IsReparsePoint(root))
        {
            return;
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToArray();
        }
        catch (IOException)
        {
            diagnostics.Add(new ExtensionCatalogDiagnostic("EXTENSION_DISCOVERY_UNAVAILABLE", ScopeName(scope)));
            return;
        }
        catch (UnauthorizedAccessException)
        {
            diagnostics.Add(new ExtensionCatalogDiagnostic("EXTENSION_DISCOVERY_UNAVAILABLE", ScopeName(scope)));
            return;
        }

        foreach (var directory in directories)
        {
            if (IsReparsePoint(directory))
            {
                diagnostics.Add(new ExtensionCatalogDiagnostic("EXTENSION_REPARSE_DENIED", ScopeName(scope)));
                continue;
            }

            var manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath) || IsReparsePoint(manifestPath))
            {
                continue;
            }

            if (TryReadDescriptor(directory, manifestPath, scope, out var descriptor, out var code))
            {
                descriptors.Add(descriptor!);
            }
            else
            {
                diagnostics.Add(new ExtensionCatalogDiagnostic(code ?? "EXTENSION_MANIFEST_INVALID", ScopeName(scope)));
            }
        }
    }

    private ProjectInstructionSnapshot ReadInstructions(string relativeProjectPath)
    {
        var allFiles = EnumerateInstructionFiles(roots.ProjectInstructionRoot).ToArray();
        var allDigest = HashInstructionFiles(allFiles);
        if (!TryNormalizeRelativePath(relativeProjectPath, out var normalized))
        {
            return new ProjectInstructionSnapshot(allDigest, [], ["AGENTS_PATH_DENIED"]);
        }

        var segments = normalized.Length == 0
            ? []
            : normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1).ToArray();
        var instructions = new List<string>();
        var diagnostics = new List<string>();
        var current = roots.ProjectInstructionRoot;
        ReadScopeInstruction(current, instructions, diagnostics);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) || IsReparsePoint(current))
            {
                diagnostics.Add("AGENTS_PATH_DENIED");
                break;
            }

            ReadScopeInstruction(current, instructions, diagnostics);
        }

        return new ProjectInstructionSnapshot(allDigest, instructions, diagnostics.OrderBy(item => item, StringComparer.Ordinal).ToArray());
    }

    private static IEnumerable<InstructionFile> EnumerateInstructionFiles(string root)
    {
        if (!Directory.Exists(root) || IsReparsePoint(root))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (IsReparsePoint(directory))
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(path => path, StringComparer.Ordinal).ToArray();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (!IsReparsePoint(child))
                {
                    pending.Push(child);
                }
            }

            foreach (var name in new[] { AgentsFileName, ClaudeFileName })
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path) || IsReparsePoint(path))
                {
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(path, Encoding.UTF8);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                yield return new InstructionFile(
                    Path.GetRelativePath(root, path).Replace('\\', '/'),
                    name,
                    text);
            }
        }
    }

    private static void ReadScopeInstruction(string directory, List<string> instructions, List<string> diagnostics)
    {
        var agents = Path.Combine(directory, AgentsFileName);
        var claude = Path.Combine(directory, ClaudeFileName);
        var hasAgents = File.Exists(agents) && !IsReparsePoint(agents);
        var hasClaude = File.Exists(claude) && !IsReparsePoint(claude);
        if (hasAgents && hasClaude)
        {
            diagnostics.Add("AGENTS_CLAUDE_CONFLICT");
        }

        var selected = hasAgents ? agents : hasClaude ? claude : null;
        if (selected is null)
        {
            return;
        }

        try
        {
            instructions.Add(File.ReadAllText(selected, Encoding.UTF8));
        }
        catch (IOException)
        {
            diagnostics.Add("AGENTS_READ_UNAVAILABLE");
        }
        catch (UnauthorizedAccessException)
        {
            diagnostics.Add("AGENTS_READ_UNAVAILABLE");
        }
    }

    private static bool TryReadDescriptor(
        string extensionDirectory,
        string manifestPath,
        ExtensionScope scope,
        out ExtensionDescriptor? descriptor,
        out string? code)
    {
        descriptor = null;
        code = null;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryReadManifest(document.RootElement, out var manifest))
            {
                code = "EXTENSION_MANIFEST_INVALID";
                return false;
            }

            if (!TryHashExtensionDirectory(extensionDirectory, out var digest))
            {
                code = "EXTENSION_REPARSE_DENIED";
                return false;
            }

            descriptor = new ExtensionDescriptor(manifest!, scope, digest!);
            return true;
        }
        catch (JsonException)
        {
            code = "EXTENSION_MANIFEST_INVALID";
            return false;
        }
        catch (IOException)
        {
            code = "EXTENSION_MANIFEST_UNAVAILABLE";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            code = "EXTENSION_MANIFEST_UNAVAILABLE";
            return false;
        }
        catch (ArgumentException)
        {
            code = "EXTENSION_MANIFEST_INVALID";
            return false;
        }
    }

    private static bool TryReadManifest(JsonElement root, out ExtensionManifest? manifest)
    {
        manifest = null;
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "kind", "name", "version", "description", "instructions", "scripts", "requestedPermissions", "dependencies"
        };
        var properties = root.EnumerateObject().ToArray();
        if (properties.Any(property => !allowed.Contains(property.Name)) ||
            properties.GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1) ||
            !TryRequiredString(root, "kind", out var kindText) ||
            !TryRequiredString(root, "name", out var name) ||
            !TryRequiredString(root, "version", out var version) ||
            !TryRequiredString(root, "description", out var description) ||
            !TryOptionalString(root, "instructions", out var instructions) ||
            !TryStringArray(root, "scripts", out var scripts) ||
            !TryStringArray(root, "requestedPermissions", out var permissionTexts) ||
            !TryStringArray(root, "dependencies", out var dependencies) ||
            !TryKind(kindText, out var kind) ||
            !TryCapabilities(permissionTexts, out var permissions))
        {
            return false;
        }

        _ = ExtensionIdentity.ValidateName(name);
        if (version.Length > 128 || description.Length > 4096 || instructions?.Length > 65536 ||
            scripts.Count > 128 || dependencies.Count > 128 ||
            scripts.Any(path => !IsSafeRelativePath(path)))
        {
            return false;
        }

        manifest = new ExtensionManifest(kind, name, version, description, instructions, scripts, permissions, dependencies);
        return true;
    }

    private static bool TryHashExtensionDirectory(string root, out string? digest)
    {
        digest = null;
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (IsReparsePoint(directory))
            {
                return false;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (IsReparsePoint(child))
                    {
                        return false;
                    }

                    pending.Push(child);
                }

                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (IsReparsePoint(file))
                    {
                        return false;
                    }

                    files.Add(file);
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\u001f"));
            using var stream = File.OpenRead(file);
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }
        }

        digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return true;
    }

    private static string HashInstructionFiles(IReadOnlyList<InstructionFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.RelativePath + "\u001f" + file.Content + "\u001e"));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool TryNormalizeRelativePath(string candidate, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrEmpty(candidate))
        {
            return true;
        }

        if (Path.IsPathRooted(candidate))
        {
            return false;
        }

        var parts = candidate.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".."))
        {
            return false;
        }

        normalized = string.Join('/', parts);
        return true;
    }

    private static bool TryRequiredString(JsonElement root, string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryOptionalString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var property))
        {
            return true;
        }

        if (property.ValueKind is JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static bool TryStringArray(JsonElement root, string name, out IReadOnlyList<string> values)
    {
        values = [];
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                return false;
            }

            parsed.Add(item.GetString()!);
        }

        values = parsed;
        return true;
    }

    private static bool TryKind(string value, out ExtensionKind kind)
    {
        kind = value switch
        {
            "skill" => ExtensionKind.Skill,
            "plugin" => ExtensionKind.Plugin,
            "mcp" => ExtensionKind.McpServer,
            _ => default
        };
        return value is "skill" or "plugin" or "mcp";
    }

    private static bool TryCapabilities(IReadOnlyList<string> values, out IReadOnlyList<Capability> capabilities)
    {
        var parsed = new List<Capability>();
        foreach (var value in values)
        {
            if (!TryCapability(value, out var capability) || parsed.Contains(capability))
            {
                capabilities = [];
                return false;
            }

            parsed.Add(capability);
        }

        capabilities = parsed;
        return true;
    }

    private static bool TryCapability(string value, out Capability capability)
    {
        capability = value switch
        {
            "ProjectFile.Read" => Capability.ProjectFileRead,
            "Draft.Write" => Capability.DraftWrite,
            "Raw.Write" => Capability.RawWrite,
            "Structured.Write" => Capability.StructuredWrite,
            "Authority.Submit" => Capability.AuthoritySubmit,
            "Authority.Review" => Capability.AuthorityReview,
            "Authority.Accept" => Capability.AuthorityAccept,
            "Registry.Query" => Capability.RegistryQuery,
            "Registry.Mutate" => Capability.RegistryMutate,
            "Web.Search" => Capability.WebSearch,
            "Network.Request" => Capability.NetworkRequest,
            "Shell.Execute" => Capability.ShellExecute,
            "Script.Execute" => Capability.ScriptExecute,
            "Git.Execute" => Capability.GitExecute,
            "MCP.Call" => Capability.McpCall,
            "Agent.Spawn" => Capability.AgentSpawn,
            _ => default
        };
        return value is "ProjectFile.Read" or "Draft.Write" or "Raw.Write" or "Structured.Write" or
            "Authority.Submit" or "Authority.Review" or "Authority.Accept" or "Registry.Query" or
            "Registry.Mutate" or "Web.Search" or "Network.Request" or "Shell.Execute" or "Script.Execute" or
            "Git.Execute" or "MCP.Call" or "Agent.Spawn";
    }

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) &&
        !path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or "..");

    private static string CanonicalRoot(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string ScopeName(ExtensionScope scope) => scope.ToString().ToLowerInvariant();

    private sealed record InstructionFile(string RelativePath, string Name, string Content);
}
