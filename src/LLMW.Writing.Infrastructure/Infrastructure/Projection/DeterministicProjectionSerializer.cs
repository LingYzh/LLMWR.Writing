using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LLMW.Writing.Application.Projection;

namespace LLMW.Writing.Infrastructure.Projection;

public sealed class DeterministicProjectionSerializer : IDeterministicProjectionSerializer
{
    private static readonly JsonWriterOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false
    };

    private static readonly JsonWriterOptions PrettyJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = true,
        SkipValidation = false
    };

    public ProjectionArtifact SerializeNarrativeMarkdown(ProjectionNarrativeObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ProjectionPathPolicy.Validate(source.CanonicalRelativePath);
        var normalized = source with
        {
            ObjectType = ProjectionCanonicalization.NormalizeText(source.ObjectType),
            Status = ProjectionCanonicalization.NormalizeText(source.Status),
            Body = ProjectionCanonicalization.NormalizeText(source.Body)
        };
        var body = normalized.Body.TrimEnd('\n');
        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("schemaVersion: ").Append(normalized.SchemaVersion).Append('\n');
        builder.Append("objectId: ").Append(Quote(normalized.ObjectId)).Append('\n');
        builder.Append("objectType: ").Append(Quote(normalized.ObjectType)).Append('\n');
        builder.Append("revision: ").Append(normalized.Revision).Append('\n');
        builder.Append("status: ").Append(Quote(normalized.Status)).Append('\n');
        builder.Append("stateRevisionId: ").Append(NullableScalar(normalized.StateRevisionId)).Append('\n');
        builder.Append("artifactDigest: ").Append(NullableScalar(normalized.ArtifactDigest)).Append('\n');
        builder.Append("extensions: {}\n");
        builder.Append("---\n");
        if (body.Length > 0)
        {
            builder.Append(body).Append('\n');
        }

        var bytes = ProjectionCanonicalization.StrictUtf8.GetBytes(builder.ToString());
        var semanticBytes = WriteObjectSemantic(normalized);
        return new ProjectionArtifact(
            ProjectionArtifactKind.NarrativeMarkdown,
            normalized.CanonicalRelativePath,
            bytes,
            ProjectionCanonicalization.Sha256(bytes),
            ProjectionCanonicalization.Sha256(semanticBytes),
            normalized.ObjectId,
            normalized.ObjectType,
            normalized.SchemaVersion,
            normalized.ArtifactDigest,
            normalized.Status);
    }

    public ProjectionArtifact SerializeNarrativeState(ProjectionSnapshot source) =>
        SerializeJson(
            ProjectionArtifactKind.NarrativeStateJson,
            ProjectionPathPolicy.NarrativeStatePath,
            writer => WriteNarrativeState(writer, source));

    public ProjectionArtifact SerializeDependencies(ProjectionSnapshot source) =>
        SerializeJson(
            ProjectionArtifactKind.DependencyJson,
            ProjectionPathPolicy.DependencyPath,
            writer => WriteDependencies(writer, source));

    public ProjectionArtifact SerializeRegistry(ProjectionSnapshot source) =>
        SerializeJson(
            ProjectionArtifactKind.RegistryJson,
            ProjectionPathPolicy.RegistryPath,
            writer => WriteRegistry(writer, source));

    private static ProjectionArtifact SerializeJson(
        ProjectionArtifactKind kind,
        string targetRelativePath,
        Action<Utf8JsonWriter> write)
    {
        ProjectionPathPolicy.Validate(targetRelativePath);
        var compact = WriteJson(CompactJson, write, trailingNewline: false);
        var pretty = WriteJson(PrettyJson, write, trailingNewline: true);
        return new ProjectionArtifact(
            kind,
            targetRelativePath,
            pretty,
            ProjectionCanonicalization.Sha256(pretty),
            ProjectionCanonicalization.Sha256(compact));
    }

    private static byte[] WriteObjectSemantic(ProjectionNarrativeObject source) =>
        WriteJson(
            CompactJson,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", source.SchemaVersion);
                writer.WriteString("objectId", source.ObjectId);
                writer.WriteString("objectType", source.ObjectType);
                writer.WriteNumber("revision", source.Revision);
                writer.WriteString("status", source.Status);
                WriteNullableString(writer, "stateRevisionId", source.StateRevisionId);
                WriteNullableString(writer, "artifactDigest", source.ArtifactDigest);
                writer.WriteString("body", source.Body.TrimEnd('\n'));
                writer.WriteStartObject("extensions");
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            trailingNewline: false);

    private static void WriteNarrativeState(Utf8JsonWriter writer, ProjectionSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteStartArray("objects");
        foreach (var item in source.NarrativeObjects.OrderBy(value => value.ObjectId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("objectId", item.ObjectId);
            writer.WriteString("objectType", ProjectionCanonicalization.NormalizeText(item.ObjectType));
            writer.WriteNumber("objectSchemaVersion", item.SchemaVersion);
            writer.WriteNumber("revision", item.Revision);
            writer.WriteString("status", item.Status);
            WriteNullableString(writer, "stateRevisionId", item.StateRevisionId);
            WriteNullableString(writer, "artifactDigest", item.ArtifactDigest);
            writer.WriteString("canonicalPath", item.CanonicalRelativePath);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteDependencies(Utf8JsonWriter writer, ProjectionSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteStartArray("edges");
        foreach (var edge in source.DependencyEdges
                     .OrderBy(value => value.FromObjectId, StringComparer.Ordinal)
                     .ThenBy(value => value.ToObjectId, StringComparer.Ordinal)
                     .ThenBy(value => value.EdgeType, StringComparer.Ordinal)
                     .ThenBy(value => value.EdgeId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("edgeId", edge.EdgeId);
            writer.WriteString("fromObjectId", edge.FromObjectId);
            writer.WriteString("toObjectId", edge.ToObjectId);
            writer.WriteString("edgeType", edge.EdgeType);
            writer.WriteString("validationStatus", edge.ValidationStatus);
            if (edge.Confidence is null)
            {
                writer.WriteNull("confidence");
            }
            else
            {
                writer.WriteNumber("confidence", edge.Confidence.Value);
            }

            WriteNullableString(writer, "provenanceRef", edge.ProvenanceRef);
            WriteNullableString(writer, "sourceRevisionId", edge.SourceRevisionId);
            if (edge.LastValidatedAtMs is null)
            {
                writer.WriteNull("lastValidatedAtMs");
            }
            else
            {
                writer.WriteNumber("lastValidatedAtMs", edge.LastValidatedAtMs.Value);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRegistry(Utf8JsonWriter writer, ProjectionSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteStartArray("entries");
        foreach (var entry in source.RegistryEntries
                     .OrderBy(value => value.ObjectId, StringComparer.Ordinal)
                     .ThenBy(value => value.RelativePath, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("registryEntryId", entry.RegistryEntryId);
            writer.WriteString("objectId", entry.ObjectId);
            writer.WriteString("objectType", entry.ObjectType);
            writer.WriteNumber("objectSchemaVersion", entry.SchemaVersion);
            writer.WriteString("pathId", entry.PathId);
            writer.WriteString("relativePath", entry.RelativePath);
            writer.WriteString("pathKind", entry.PathKind);
            writer.WriteBoolean("isCanonical", entry.IsCanonical);
            writer.WriteString("registrationState", entry.RegistrationState);
            writer.WriteString("retrievalAvailability", entry.RetrievalAvailability);
            writer.WriteString("reconcileState", entry.ReconcileState);
            writer.WriteString("trustedPhysicalDigest", entry.TrustedPhysicalDigest);
            writer.WriteString("trustedSemanticDigest", entry.TrustedSemanticDigest);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static byte[] WriteJson(JsonWriterOptions options, Action<Utf8JsonWriter> write, bool trailingNewline)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory, options))
        {
            write(writer);
            writer.Flush();
        }

        var normalized = ProjectionCanonicalization.StrictUtf8.GetBytes(
            ProjectionCanonicalization.NormalizeText(
                ProjectionCanonicalization.StrictUtf8.GetString(memory.ToArray())));
        if (!trailingNewline)
        {
            return normalized;
        }

        var withTrailingNewline = new byte[normalized.Length + 1];
        normalized.CopyTo(withTrailingNewline, 0);
        withTrailingNewline[^1] = (byte)'\n';
        return withTrailingNewline;
    }

    private static string NullableScalar(string? value) => value is null ? "null" : Quote(value);

    private static string Quote(string value) => JsonSerializer.Serialize(ProjectionCanonicalization.NormalizeText(value));

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, ProjectionCanonicalization.NormalizeText(value));
        }
    }
}
