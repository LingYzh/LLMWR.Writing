namespace LLMW.Writing.Application.Projection;

public enum ProjectionError
{
    ProjectionSourceMissing,
    ProjectionSerializationFailed,
    ProjectionMaterializationFailed,
    ProjectionVerificationFailed,
    InvalidProjectionPath,
    RebuildFailed,
    AuthorityDirty,
    RecoveryRequired
}

public sealed record ProjectionFailure(ProjectionError Code, string? Detail = null);

public sealed record ProjectionResult<T>(T? Value, ProjectionFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public static class ProjectionResults
{
    public static ProjectionResult<T> Success<T>(T value) => new(value, null);

    public static ProjectionResult<T> Fail<T>(ProjectionError code, string? detail = null) =>
        new(default, new ProjectionFailure(code, detail));
}

public enum ProjectionArtifactKind
{
    NarrativeMarkdown,
    NarrativeStateJson,
    DependencyJson,
    RegistryJson
}

public sealed record ProjectionNarrativeObject(
    string ObjectId,
    string ObjectType,
    int SchemaVersion,
    int Revision,
    string Status,
    string? StateRevisionId,
    string? ArtifactDigest,
    string Body,
    string CanonicalRelativePath);

public sealed record ProjectionDependencyEdge(
    string EdgeId,
    string FromObjectId,
    string ToObjectId,
    string EdgeType,
    string ValidationStatus,
    double? Confidence,
    string? ProvenanceRef,
    string? SourceRevisionId,
    long? LastValidatedAtMs);

public sealed record ProjectionRegistryEntry(
    string RegistryEntryId,
    string ObjectId,
    string ObjectType,
    int SchemaVersion,
    string PathId,
    string RelativePath,
    string PathKind,
    bool IsCanonical,
    string RegistrationState,
    string RetrievalAvailability,
    string ReconcileState,
    string TrustedPhysicalDigest,
    string TrustedSemanticDigest);

public sealed record ProjectionSnapshot(
    IReadOnlyList<ProjectionNarrativeObject> NarrativeObjects,
    IReadOnlyList<ProjectionDependencyEdge> DependencyEdges,
    IReadOnlyList<ProjectionRegistryEntry> RegistryEntries);

public sealed record ProjectionArtifact(
    ProjectionArtifactKind Kind,
    string TargetRelativePath,
    byte[] Bytes,
    string PhysicalDigest,
    string SemanticDigest,
    string? ObjectId = null,
    string? ObjectType = null,
    int? SchemaVersion = null,
    string? ArtifactDigest = null,
    string? Status = null);

public sealed record ProjectionBuild(IReadOnlyList<ProjectionArtifact> Artifacts);

public sealed record ParsedProjectionFrontmatter(
    IReadOnlyDictionary<string, string?> KnownFields,
    IReadOnlyDictionary<string, string?> CompatibleUnknownFields,
    IReadOnlyList<string> Warnings,
    string Body);
