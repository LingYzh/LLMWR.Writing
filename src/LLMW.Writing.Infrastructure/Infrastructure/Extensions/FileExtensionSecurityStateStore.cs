using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMW.Writing.Application.Extensions;
using LLMW.Writing.Domain.Extensions;

namespace LLMW.Writing.Infrastructure.Extensions;

/// <summary>
/// Per-user security state. It is intentionally outside the project, project database, Authority
/// audit, and logs, so cloning/importing a project cannot carry trust or activation with it.
/// </summary>
public sealed class FileExtensionSecurityStateStore : IExtensionSecurityStateStore
{
    private const int SchemaVersion = 1;
    private readonly string statePath;

    public FileExtensionSecurityStateStore(string stateRoot, string projectId, string canonicalProjectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        if (!Guid.TryParseExact(projectId, "D", out _))
        {
            throw new ArgumentException("Project identity must be a canonical UUID.", nameof(projectId));
        }

        var canonicalStateRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateRoot));
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalProjectRoot));
        var binding = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectId + "\u001f" + canonicalRoot)))
            .ToLowerInvariant();
        statePath = Path.Combine(canonicalStateRoot, binding + ".json");
    }

    public ExtensionSecurityState Load()
    {
        if (!File.Exists(statePath))
        {
            return ExtensionSecurityState.Empty;
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(statePath, Encoding.UTF8), SerializerOptions);
            if (persisted is null || persisted.SchemaVersion != SchemaVersion)
            {
                return ExtensionSecurityState.Empty;
            }

            var activations = new Dictionary<string, ExtensionActivationRecord>(StringComparer.Ordinal);
            foreach (var item in persisted.Activations ?? [])
            {
                if (string.IsNullOrWhiteSpace(item.ExtensionId) || item.ExtensionId.Length > 160 ||
                    !Enum.IsDefined(item.State) ||
                    item.ContentDigest is { Length: not 64 } ||
                    item.ContentDigest is not null && !item.ContentDigest.All(Uri.IsHexDigit))
                {
                    return ExtensionSecurityState.Empty;
                }

                activations.Add(item.ExtensionId, new ExtensionActivationRecord(item.State, item.ContentDigest));
            }

            var operations = new Dictionary<string, ExtensionOperationReceipt>(StringComparer.Ordinal);
            foreach (var item in persisted.Operations ?? [])
            {
                if (!Guid.TryParseExact(item.OperationId, "D", out _) ||
                    string.IsNullOrWhiteSpace(item.Fingerprint) || item.Fingerprint.Length > 256 ||
                    item.ExtensionId?.Length > 160)
                {
                    return ExtensionSecurityState.Empty;
                }

                operations.Add(item.OperationId, new ExtensionOperationReceipt(
                    item.Fingerprint, item.ExtensionId, item.Activated, item.ProjectTrusted));
            }

            return new ExtensionSecurityState(persisted.ProjectTrusted, activations, operations);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // A corrupted local trust store fails closed; it never restores a previous trust grant.
            return ExtensionSecurityState.Empty;
        }
    }

    public void Save(ExtensionSecurityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var directory = Path.GetDirectoryName(statePath) ?? throw new InvalidOperationException("State root is invalid.");
        Directory.CreateDirectory(directory);
        var persisted = new PersistedState(
            SchemaVersion,
            state.ProjectTrusted,
            state.Activations
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new PersistedActivation(item.Key, item.Value.State, item.Value.ContentDigest))
                .ToArray(),
            state.Operations
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new PersistedOperation(
                    item.Key,
                    item.Value.Fingerprint,
                    item.Value.ExtensionId,
                    item.Value.Activated,
                    item.Value.ProjectTrusted))
                .ToArray());
        var content = JsonSerializer.SerializeToUtf8Bytes(persisted, SerializerOptions);
        var temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private sealed record PersistedState(
        int SchemaVersion,
        bool ProjectTrusted,
        PersistedActivation[]? Activations,
        PersistedOperation[]? Operations);

    private sealed record PersistedActivation(
        string ExtensionId,
        ExtensionActivationState State,
        string? ContentDigest);

    private sealed record PersistedOperation(
        string OperationId,
        string Fingerprint,
        string? ExtensionId,
        bool Activated,
        bool ProjectTrusted);
}
