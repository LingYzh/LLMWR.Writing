using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LLMW.Writing.Infrastructure.Projection;

internal static class ProjectionCanonicalization
{
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string NormalizeText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC);
    }

    public static string DecodeBody(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return NormalizeText(StrictUtf8.GetString(memory.ToArray()));
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string CanonicalDouble(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);
}

internal static class ProjectionPathPolicy
{
    public const string NarrativeStatePath = "Narrative/state/narrative-state.json";
    public const string DependencyPath = "Narrative/state/dependencies.json";
    public const string RegistryPath = "Narrative/state/registry.json";

    public static string NarrativeObjectPath(string objectType, string objectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectType);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        var slug = SemanticTypeSlug(objectType);
        var path = $"Narrative/objects/{slug}-{objectId}.md";
        Validate(path);
        return path;
    }

    public static void Validate(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\', StringComparison.Ordinal) ||
            relativePath.Split('/').Any(part => part is "" or "." or "..") ||
            relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Projection paths must be canonical project-relative paths.", nameof(relativePath));
        }
    }

    private static string SemanticTypeSlug(string objectType)
    {
        var normalized = objectType.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var previousHyphen = false;
        foreach (var character in normalized)
        {
            var accepted = character is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (accepted)
            {
                builder.Append(character);
                previousHyphen = false;
            }
            else if (!previousHyphen && builder.Length > 0)
            {
                builder.Append('-');
                previousHyphen = true;
            }
        }

        var value = builder.ToString().Trim('-');
        return value.Length == 0 ? "object" : value;
    }
}

internal static class StableProjectionUuidV7
{
    public static string Create(string objectId, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        if (!Guid.TryParseExact(objectId, "D", out var source))
        {
            throw new ArgumentException("Projection identities require a canonical UUID source.", nameof(objectId));
        }

        Span<byte> sourceBytes = stackalloc byte[16];
        source.TryWriteBytes(sourceBytes, bigEndian: true, out _);
        var material = Encoding.UTF8.GetBytes($"llmw-projection-{purpose}-v1\n{objectId}");
        var hash = SHA256.HashData(material);
        Span<byte> result = stackalloc byte[16];
        sourceBytes[..6].CopyTo(result);
        hash.AsSpan(0, 10).CopyTo(result[6..]);
        result[6] = (byte)((result[6] & 0x0f) | 0x70);
        result[8] = (byte)((result[8] & 0x3f) | 0x80);
        return new Guid(result, bigEndian: true).ToString("D");
    }
}
