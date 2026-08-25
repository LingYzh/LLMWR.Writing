namespace LLMW.Writing.Domain.ProjectPackages;

/// <summary>
/// The logical identity of a Final Package.  It is intentionally independent of ZIP bytes,
/// filesystem paths, and signing implementations.
/// </summary>
public sealed record FinalPackageManifest(
    int ManifestVersion,
    string SnapshotId,
    string StorylineId,
    string AcceptedVersion,
    DateTimeOffset AcceptedAt,
    string? FinalReviewId,
    string? WarningsDigest,
    IReadOnlyList<FinalPackageManifestFile> LogicalFiles,
    string? SignatureAlgorithm = null,
    string? KeyId = null,
    string? Signature = null,
    string? TrustedTimestamp = null)
{
    public const int CurrentManifestVersion = 1;

    public FinalPackageManifest Canonicalize() => this with
    {
        LogicalFiles = LogicalFiles
            .OrderBy(file => file.LogicalPath, StringComparer.Ordinal)
            .ToArray()
    };

    public FinalPackageManifestValidation Validate()
    {
        if (ManifestVersion != CurrentManifestVersion ||
            !IsCanonicalGuid(SnapshotId) ||
            !IsCanonicalGuid(StorylineId) ||
            string.IsNullOrWhiteSpace(AcceptedVersion) ||
            AcceptedAt == default ||
            LogicalFiles is null)
        {
            return FinalPackageManifestValidation.Invalid;
        }

        if (FinalReviewId is not null && !IsCanonicalGuid(FinalReviewId) ||
            WarningsDigest is not null && !IsSha256(WarningsDigest))
        {
            return FinalPackageManifestValidation.Invalid;
        }

        string? previousPath = null;
        foreach (var file in LogicalFiles)
        {
            if (file is null || !IsLogicalFileName(file.LogicalPath) || !IsSha256(file.ContentDigest))
            {
                return FinalPackageManifestValidation.Invalid;
            }

            if (previousPath is not null && StringComparer.Ordinal.Compare(previousPath, file.LogicalPath) >= 0)
            {
                return FinalPackageManifestValidation.NotCanonical;
            }

            previousPath = file.LogicalPath;
        }

        return FinalPackageManifestValidation.Valid;
    }

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsCanonicalGuid(string? value) =>
        value is not null && Guid.TryParseExact(value, "D", out var parsed) &&
        StringComparer.Ordinal.Equals(parsed.ToString("D"), value);

    private static bool IsLogicalFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            value.Contains(':', StringComparison.Ordinal) ||
            value.Contains('\\'))
        {
            return false;
        }

        var segments = value.Split('/');
        return segments.Length > 0 && segments.All(segment => segment is not "" and not "." and not "..");
    }
}

public sealed record FinalPackageManifestFile(string LogicalPath, string ContentDigest);

public enum FinalPackageManifestValidation
{
    Valid,
    NotCanonical,
    Invalid
}
