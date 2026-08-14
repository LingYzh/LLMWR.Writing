using LLMW.Writing.Application.Projection;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Persistence;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.Infrastructure.Projection;

public sealed class ProjectionRebuilder : IProjectionRebuilder
{
    private readonly NarrativeProjectionPlanner planner;
    private readonly ProjectionAuthorityMaterializer materializer;

    public ProjectionRebuilder(
        string databasePath,
        ImmutableBlobStore blobStore,
        ProjectionAuthorityMaterializer materializer,
        SqliteDatabaseConnectionFactory? connectionFactory = null)
    {
        planner = new NarrativeProjectionPlanner(databasePath, blobStore, connectionFactory);
        this.materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
    }

    public ProjectionResult<ProjectionBuild> Rebuild(CancellationToken cancellationToken = default)
    {
        try
        {
            var current = planner.BuildCurrent(cancellationToken);
            var plans = planner.Stage(current.Build, cancellationToken);
            var transactionId = DurableUuidV7.Create().ToString();
            materializer.Materialize(transactionId, plans, cancellationToken);
            materializer.VerifyRebuild(
                transactionId,
                current.Build,
                current.Metadata,
                plans,
                cancellationToken);
            return ProjectionResults.Success(current.Build);
        }
        catch (Exception exception)
        {
            return ProjectionResults.Fail<ProjectionBuild>(ProjectionError.RebuildFailed, exception.Message);
        }
    }
}
