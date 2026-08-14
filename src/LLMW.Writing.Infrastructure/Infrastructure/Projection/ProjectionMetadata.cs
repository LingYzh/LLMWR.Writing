using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.Projection;
using LLMW.Writing.Infrastructure.Persistence;

namespace LLMW.Writing.Infrastructure.Projection;

internal sealed record ProjectionRegistrationMetadata(
    string RegistryEntryId,
    string ObjectId,
    string ObjectType,
    int SchemaVersion,
    string PathId,
    string RelativePath,
    string PhysicalDigest,
    string SemanticDigest,
    string? ArtifactDigest,
    string Status,
    string RegistrationState,
    string RetrievalAvailability,
    string ReconcileState);

internal sealed record ProjectionRecoveryMetadata(IReadOnlyList<ProjectionRegistrationMetadata> Registrations);

internal sealed record PreparedNarrativeProjection(
    ProjectionBuild Build,
    IReadOnlyList<AuthorityMaterializationPlan> Plans,
    AuthorityEventData RecoveryEvent,
    ProjectionRecoveryMetadata Metadata,
    IReadOnlyDictionary<string, string> NewStateRevisionIds);

internal static class ProjectionMetadataCodec
{
    public const string EventType = "wp07.projection_metadata";

    public static AuthorityEventData CreateEvent(string transactionId, ProjectionRecoveryMetadata metadata) =>
        new(
            DurableUuidV7.Create().ToString(),
            "authority_transaction",
            transactionId,
            EventType,
            Serialize(metadata));

    public static string Serialize(ProjectionRecoveryMetadata metadata)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   Indented = false
               }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            writer.WriteStartArray("registrations");
            foreach (var item in metadata.Registrations
                         .OrderBy(value => value.ObjectId, StringComparer.Ordinal)
                         .ThenBy(value => value.RelativePath, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("registryEntryId", item.RegistryEntryId);
                writer.WriteString("objectId", item.ObjectId);
                writer.WriteString("objectType", item.ObjectType);
                writer.WriteNumber("objectSchemaVersion", item.SchemaVersion);
                writer.WriteString("pathId", item.PathId);
                writer.WriteString("relativePath", item.RelativePath);
                writer.WriteString("physicalDigest", item.PhysicalDigest);
                writer.WriteString("semanticDigest", item.SemanticDigest);
                if (item.ArtifactDigest is null)
                {
                    writer.WriteNull("artifactDigest");
                }
                else
                {
                    writer.WriteString("artifactDigest", item.ArtifactDigest);
                }

                writer.WriteString("status", item.Status);
                writer.WriteString("registrationState", item.RegistrationState);
                writer.WriteString("retrievalAvailability", item.RetrievalAvailability);
                writer.WriteString("reconcileState", item.ReconcileState);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    public static ProjectionRecoveryMetadata Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var schemaVersion = document.RootElement.GetProperty("schemaVersion").GetInt32();
        if (schemaVersion is not 1 and not 2)
        {
            throw new InvalidOperationException("Unsupported projection recovery metadata version.");
        }

        List<ProjectionRegistrationMetadata> registrations = [];
        foreach (var item in document.RootElement.GetProperty("registrations").EnumerateArray())
        {
            var objectType = item.GetProperty("objectType").GetString()!;
            registrations.Add(new ProjectionRegistrationMetadata(
                item.GetProperty("registryEntryId").GetString()!,
                item.GetProperty("objectId").GetString()!,
                objectType,
                item.GetProperty("objectSchemaVersion").GetInt32(),
                item.GetProperty("pathId").GetString()!,
                item.GetProperty("relativePath").GetString()!,
                item.GetProperty("physicalDigest").GetString()!,
                item.GetProperty("semanticDigest").GetString()!,
                item.GetProperty("artifactDigest").ValueKind == JsonValueKind.Null
                    ? null
                    : item.GetProperty("artifactDigest").GetString(),
                item.GetProperty("status").GetString()!,
                schemaVersion == 1 ? "registered" : item.GetProperty("registrationState").GetString()!,
                schemaVersion == 1
                    ? StringComparer.OrdinalIgnoreCase.Equals(objectType, "raw") ? "unavailable" : "available"
                    : item.GetProperty("retrievalAvailability").GetString()!,
                schemaVersion == 1 ? "clean" : item.GetProperty("reconcileState").GetString()!));
        }

        return new ProjectionRecoveryMetadata(registrations);
    }
}
