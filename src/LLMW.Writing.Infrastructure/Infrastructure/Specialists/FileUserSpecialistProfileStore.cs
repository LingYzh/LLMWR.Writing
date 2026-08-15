using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Domain.Runtime;

namespace LLMW.Writing.Infrastructure.Specialists;

public sealed class FileUserSpecialistProfileStore : IUserSpecialistProfileStore
{
    private readonly string root;
    private readonly object gate = new();

    public FileUserSpecialistProfileStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(root);
    }

    public IReadOnlyList<DurableProjectSpecialistRecord> List()
    {
        lock (gate)
        {
            if (!Directory.Exists(root))
            {
                return [];
            }

            var list = new List<DurableProjectSpecialistRecord>();
            foreach (var file in Directory.GetFiles(root, "*.json").OrderBy(item => item, StringComparer.Ordinal))
            {
                if (file.EndsWith(".tmp.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var record = ReadFile(file);
                if (record is not null)
                {
                    list.Add(record);
                }
            }

            return list;
        }
    }

    public DurableProjectSpecialistRecord? Find(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        lock (gate)
        {
            return ReadFile(Path.Combine(root, SafeFileName(profileId)));
        }
    }

    public void Upsert(DurableProjectSpecialistRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (gate)
        {
            Directory.CreateDirectory(root);
            var payload = JsonSerializer.Serialize(new FileRecord(
                record.SpecialistProfileId,
                record.ScopeKind,
                record.Name,
                record.Version,
                record.DefinitionJson,
                record.BaseDefinitionDigest,
                record.Enabled,
                record.CreatedAtMs,
                record.UpdatedAtMs));
            var path = Path.Combine(root, SafeFileName(record.SpecialistProfileId));
            var temp = path + ".tmp";
            File.WriteAllText(temp, payload);
            File.Move(temp, path, overwrite: true);
        }
    }

    private static DurableProjectSpecialistRecord? ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var parsed = JsonSerializer.Deserialize<FileRecord>(File.ReadAllText(path));
        if (parsed is null)
        {
            return null;
        }

        return new DurableProjectSpecialistRecord(
            parsed.SpecialistProfileId,
            parsed.ScopeKind,
            null,
            parsed.Name,
            parsed.Version,
            parsed.DefinitionJson,
            parsed.BaseDefinitionDigest,
            parsed.Enabled,
            parsed.CreatedAtMs,
            parsed.UpdatedAtMs);
    }

    private static string SafeFileName(string profileId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileId))).ToLowerInvariant();
        return digest + ".json";
    }

    private sealed record FileRecord(
        string SpecialistProfileId,
        string ScopeKind,
        string Name,
        int Version,
        string DefinitionJson,
        string? BaseDefinitionDigest,
        bool Enabled,
        long CreatedAtMs,
        long UpdatedAtMs);
}
