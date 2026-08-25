using LLMW.Writing.Domain.ProjectPackages;

namespace LLMW.Writing.Domain.Tests;

internal static partial class Program
{
    private const string Wp20DigestA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Wp20DigestB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static void RunWp20ProjectPackageDomainTests()
    {
        Run(nameof(FinalPackageManifestCanonicalizesLogicalFiles), FinalPackageManifestCanonicalizesLogicalFiles);
        Run(nameof(FinalPackageManifestRejectsUnsafeOrDuplicateLogicalFiles), FinalPackageManifestRejectsUnsafeOrDuplicateLogicalFiles);
    }

    private static void FinalPackageManifestCanonicalizesLogicalFiles()
    {
        var manifest = NewManifest(
            [new FinalPackageManifestFile("manuscript/b.md", Wp20DigestB), new FinalPackageManifestFile("manuscript/a.md", Wp20DigestA)]);
        AssertEqual(FinalPackageManifestValidation.NotCanonical, manifest.Validate(),
            "Out-of-order manifest entries must not be treated as canonical.");
        var canonical = manifest.Canonicalize();
        AssertEqual(FinalPackageManifestValidation.Valid, canonical.Validate(),
            "Canonical manifest ordering was not accepted.");
        AssertEqual("manuscript/a.md", canonical.LogicalFiles[0].LogicalPath,
            "Canonicalization did not use logical path ordinal ordering.");
    }

    private static void FinalPackageManifestRejectsUnsafeOrDuplicateLogicalFiles()
    {
        AssertEqual(FinalPackageManifestValidation.Invalid,
            NewManifest([new FinalPackageManifestFile("../escape.md", Wp20DigestA)]).Validate(),
            "Traversal-like logical file names must be rejected.");
        AssertEqual(FinalPackageManifestValidation.NotCanonical,
            NewManifest(
            [
                new FinalPackageManifestFile("manuscript/a.md", Wp20DigestA),
                new FinalPackageManifestFile("manuscript/a.md", Wp20DigestB)
            ]).Validate(),
            "Duplicate logical file names must be rejected as non-canonical.");
        AssertEqual(FinalPackageManifestValidation.Invalid,
            NewManifest([new FinalPackageManifestFile("manuscript/a.md", "not-a-digest")]).Validate(),
            "Malformed content digests must be rejected.");
    }

    private static FinalPackageManifest NewManifest(IReadOnlyList<FinalPackageManifestFile> files) =>
        new(
            FinalPackageManifest.CurrentManifestVersion,
            "018f3e78-1234-7abc-8def-0123456789ad",
            "018f3e78-1234-7abc-8def-0123456789ae",
            "v1.0",
            DateTimeOffset.FromUnixTimeMilliseconds(1735689600000),
            "018f3e78-1234-7abc-8def-0123456789af",
            null,
            files);
}
