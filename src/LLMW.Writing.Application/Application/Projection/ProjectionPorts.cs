namespace LLMW.Writing.Application.Projection;

public interface IDeterministicProjectionSerializer
{
    ProjectionArtifact SerializeNarrativeMarkdown(ProjectionNarrativeObject source);

    ProjectionArtifact SerializeNarrativeState(ProjectionSnapshot source);

    ProjectionArtifact SerializeDependencies(ProjectionSnapshot source);

    ProjectionArtifact SerializeRegistry(ProjectionSnapshot source);
}

public interface IProjectionFrontmatterParser
{
    ProjectionResult<ParsedProjectionFrontmatter> Parse(ReadOnlySpan<byte> bytes);
}

public interface IProjectionRebuilder
{
    ProjectionResult<ProjectionBuild> Rebuild(CancellationToken cancellationToken = default);
}
