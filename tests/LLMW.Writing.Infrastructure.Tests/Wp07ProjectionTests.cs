using System.Security.Cryptography;
using System.Text;
using LLMW.Writing.Application.Projection;
using LLMW.Writing.Infrastructure.Projection;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private const string Wp07ObjectId = "018f3e78-1234-7abc-8def-0123456789b1";
    private const string Wp07StateId = "018f3e78-1234-7abc-8def-0123456789b2";
    private const string Wp07Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static void RunWp07ProjectionInfrastructureTests()
    {
        Run(nameof(MarkdownProjectionMatchesExactGoldenBytes), MarkdownProjectionMatchesExactGoldenBytes);
        Run(nameof(JsonProjectionMatchesExactGoldenBytesAndStableOrdering), JsonProjectionMatchesExactGoldenBytesAndStableOrdering);
        Run(nameof(ProjectionParserPreservesNamespacedUnknownFieldsWithoutAuthorityMutation),
            ProjectionParserPreservesNamespacedUnknownFieldsWithoutAuthorityMutation);
        Run(nameof(ProjectionSerializerRejectsUnsafeTargetPath), ProjectionSerializerRejectsUnsafeTargetPath);
    }

    private static void MarkdownProjectionMatchesExactGoldenBytes()
    {
        var serializer = new DeterministicProjectionSerializer();
        var source = new ProjectionNarrativeObject(
            Wp07ObjectId,
            "character",
            1,
            2,
            "current",
            Wp07StateId,
            Wp07Digest,
            "Cafe\u0301\r\nLine\r\n\r\n",
            $"Narrative/objects/character-{Wp07ObjectId}.md");
        var first = serializer.SerializeNarrativeMarkdown(source);
        var second = serializer.SerializeNarrativeMarkdown(source);
        var expected =
            "---\n" +
            "schemaVersion: 1\n" +
            $"objectId: \"{Wp07ObjectId}\"\n" +
            "objectType: \"character\"\n" +
            "revision: 2\n" +
            "status: \"current\"\n" +
            $"stateRevisionId: \"{Wp07StateId}\"\n" +
            $"artifactDigest: \"{Wp07Digest}\"\n" +
            "extensions: {}\n" +
            "---\n" +
            "Café\n" +
            "Line\n";

        AssertTrue(first.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(expected)),
            "Markdown projection did not match the exact canonical golden bytes.");
        AssertTrue(first.Bytes.AsSpan().SequenceEqual(second.Bytes),
            "Repeated Markdown projection was not byte-identical.");
        AssertEqual(Hash(first.Bytes), first.PhysicalDigest, "Markdown physical digest did not match materialized bytes.");
        AssertFalse(first.Bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), "Markdown projection contains a BOM.");
        AssertFalse(first.Bytes.Contains((byte)'\r'), "Markdown projection contains CR line endings.");
        AssertEqual(1, CountTrailingLf(first.Bytes), "Markdown projection must have exactly one trailing LF.");
    }

    private static void JsonProjectionMatchesExactGoldenBytesAndStableOrdering()
    {
        var serializer = new DeterministicProjectionSerializer();
        var item = new ProjectionNarrativeObject(
            Wp07ObjectId,
            "character",
            1,
            2,
            "current",
            Wp07StateId,
            Wp07Digest,
            "ignored body",
            $"Narrative/objects/character-{Wp07ObjectId}.md");
        var snapshot = new ProjectionSnapshot([item], [], []);
        var first = serializer.SerializeNarrativeState(snapshot);
        var second = serializer.SerializeNarrativeState(snapshot);
        var expected =
            "{\n" +
            "  \"schemaVersion\": 1,\n" +
            "  \"objects\": [\n" +
            "    {\n" +
            $"      \"objectId\": \"{Wp07ObjectId}\",\n" +
            "      \"objectType\": \"character\",\n" +
            "      \"objectSchemaVersion\": 1,\n" +
            "      \"revision\": 2,\n" +
            "      \"status\": \"current\",\n" +
            $"      \"stateRevisionId\": \"{Wp07StateId}\",\n" +
            $"      \"artifactDigest\": \"{Wp07Digest}\",\n" +
            $"      \"canonicalPath\": \"Narrative/objects/character-{Wp07ObjectId}.md\"\n" +
            "    }\n" +
            "  ]\n" +
            "}\n";

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var firstDifference = Enumerable.Range(0, Math.Min(first.Bytes.Length, expectedBytes.Length))
            .FirstOrDefault(index => first.Bytes[index] != expectedBytes[index], -1);
        AssertTrue(first.Bytes.AsSpan().SequenceEqual(expectedBytes),
            $"Narrative State JSON did not match the exact canonical golden bytes. ExpectedLength={expectedBytes.Length}, ActualLength={first.Bytes.Length}, FirstDifference={firstDifference}.");
        AssertTrue(first.Bytes.AsSpan().SequenceEqual(second.Bytes),
            "Repeated JSON projection was not byte-identical.");
        AssertFalse(first.Bytes.Contains((byte)'\r'), "JSON projection contains CR line endings.");
        AssertEqual(1, CountTrailingLf(first.Bytes), "JSON projection must have exactly one trailing LF.");

        var edge = new ProjectionDependencyEdge(
            "018f3e78-1234-7abc-8def-0123456789d1",
            Wp07ObjectId,
            "018f3e78-1234-7abc-8def-0123456789b3",
            "canon_reference",
            "needs_revalidation",
            0.75,
            null,
            Wp07StateId,
            42);
        var dependency = serializer.SerializeDependencies(snapshot with { DependencyEdges = [edge] });
        var expectedDependency =
            "{\n" +
            "  \"schemaVersion\": 1,\n" +
            "  \"edges\": [\n" +
            "    {\n" +
            "      \"edgeId\": \"018f3e78-1234-7abc-8def-0123456789d1\",\n" +
            $"      \"fromObjectId\": \"{Wp07ObjectId}\",\n" +
            "      \"toObjectId\": \"018f3e78-1234-7abc-8def-0123456789b3\",\n" +
            "      \"edgeType\": \"canon_reference\",\n" +
            "      \"validationStatus\": \"needs_revalidation\",\n" +
            "      \"confidence\": 0.75,\n" +
            "      \"provenanceRef\": null,\n" +
            $"      \"sourceRevisionId\": \"{Wp07StateId}\",\n" +
            "      \"lastValidatedAtMs\": 42\n" +
            "    }\n" +
            "  ]\n" +
            "}\n";
        AssertTrue(dependency.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(expectedDependency)),
            "Dependency JSON did not match the exact canonical golden bytes.");

        var registryEntry = new ProjectionRegistryEntry(
            "018f3e78-1234-7abc-8def-0123456789e1",
            Wp07ObjectId,
            "character",
            1,
            "018f3e78-1234-7abc-8def-0123456789e2",
            $"Narrative/objects/character-{Wp07ObjectId}.md",
            "narrative_projection",
            true,
            "registered",
            "available",
            "clean",
            Wp07Digest,
            Wp07Digest);
        var registry = serializer.SerializeRegistry(snapshot with { RegistryEntries = [registryEntry] });
        var expectedRegistry =
            "{\n" +
            "  \"schemaVersion\": 1,\n" +
            "  \"entries\": [\n" +
            "    {\n" +
            "      \"registryEntryId\": \"018f3e78-1234-7abc-8def-0123456789e1\",\n" +
            $"      \"objectId\": \"{Wp07ObjectId}\",\n" +
            "      \"objectType\": \"character\",\n" +
            "      \"objectSchemaVersion\": 1,\n" +
            "      \"pathId\": \"018f3e78-1234-7abc-8def-0123456789e2\",\n" +
            $"      \"relativePath\": \"Narrative/objects/character-{Wp07ObjectId}.md\",\n" +
            "      \"pathKind\": \"narrative_projection\",\n" +
            "      \"isCanonical\": true,\n" +
            "      \"registrationState\": \"registered\",\n" +
            "      \"retrievalAvailability\": \"available\",\n" +
            "      \"reconcileState\": \"clean\",\n" +
            $"      \"trustedPhysicalDigest\": \"{Wp07Digest}\",\n" +
            $"      \"trustedSemanticDigest\": \"{Wp07Digest}\"\n" +
            "    }\n" +
            "  ]\n" +
            "}\n";
        AssertTrue(registry.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(expectedRegistry)),
            "Registry JSON did not match the exact canonical golden bytes.");
    }

    private static void ProjectionParserPreservesNamespacedUnknownFieldsWithoutAuthorityMutation()
    {
        var parser = new ProjectionFrontmatterParser();
        var bytes = Encoding.UTF8.GetBytes(
            "---\n" +
            "schemaVersion: 1\n" +
            $"objectId: \"{Wp07ObjectId}\"\n" +
            "objectType: \"character\"\n" +
            "revision: 1\n" +
            "status: \"current\"\n" +
            "stateRevisionId: null\n" +
            "artifactDigest: null\n" +
            "extensions: {}\n" +
            "x-example.note: \"retained\"\n" +
            "---\nbody\n");
        var result = parser.Parse(bytes);

        AssertTrue(result.Succeeded, $"Compatible unknown field was rejected: {result.Failure?.Detail}");
        AssertTrue(
            string.Equals("retained", result.Value!.CompatibleUnknownFields["x-example.note"], StringComparison.Ordinal),
            "Compatible unknown projection field was not preserved.");
        AssertEqual(1, result.Value.Warnings.Count, "Compatible unknown field did not produce a warning.");
    }

    private static void ProjectionSerializerRejectsUnsafeTargetPath()
    {
        var serializer = new DeterministicProjectionSerializer();
        AssertThrows<ArgumentException>(
            () => serializer.SerializeNarrativeMarkdown(new ProjectionNarrativeObject(
                Wp07ObjectId,
                "character",
                1,
                1,
                "current",
                Wp07StateId,
                Wp07Digest,
                "body",
                "Narrative/../escape.md")),
            "Projection serializer accepted a path containing '..'.");
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static int CountTrailingLf(byte[] bytes)
    {
        var count = 0;
        for (var index = bytes.Length - 1; index >= 0 && bytes[index] == (byte)'\n'; index--)
        {
            count++;
        }

        return count;
    }
}
