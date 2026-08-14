using System.Data.Common;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.Infrastructure.ChapterAuthority;

public sealed class ChapterAuthorityMaterializer : IAuthorityMaterializer
{
    private readonly string databasePath;
    private readonly IAuthorityMaterializer inner;
    private readonly SqliteDatabaseConnectionFactory connectionFactory;
    private readonly Func<long> clock;

    public ChapterAuthorityMaterializer(
        string databasePath,
        IAuthorityMaterializer inner,
        SqliteDatabaseConnectionFactory? connectionFactory = null,
        Func<long>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.connectionFactory = connectionFactory ?? new SqliteDatabaseConnectionFactory();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void Materialize(
        string transactionId,
        IReadOnlyList<AuthorityMaterializationPlan> plans,
        CancellationToken cancellationToken = default) =>
        inner.Materialize(transactionId, plans, cancellationToken);

    public void Verify(
        string transactionId,
        IReadOnlyList<AuthorityMaterializationPlan> plans,
        CancellationToken cancellationToken = default)
    {
        inner.Verify(transactionId, plans, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var transaction = connection.BeginTransaction();
        using (var revisions = connection.CreateCommand())
        {
            revisions.Transaction = transaction;
            revisions.CommandText =
                """
                UPDATE manuscript_revisions
                SET materialization_status='materialized'
                WHERE transaction_id=$transaction_id;
                """;
            Add(revisions, "$transaction_id", transactionId);
            revisions.ExecuteNonQuery();
        }

        using (var chapters = connection.CreateCommand())
        {
            chapters.Transaction = transaction;
            chapters.CommandText =
                """
                UPDATE chapters
                SET workflow_state='materialized',updated_at_ms=$now
                WHERE current_manuscript_revision_id IN (
                    SELECT revision_id FROM manuscript_revisions WHERE transaction_id=$transaction_id);
                """;
            Add(chapters, "$now", clock());
            Add(chapters, "$transaction_id", transactionId);
            chapters.ExecuteNonQuery();
        }

        using (var projectState = connection.CreateCommand())
        {
            projectState.Transaction = transaction;
            projectState.CommandText =
                """
                UPDATE authority_transactions
                SET project_submission_state='idle'
                WHERE transaction_id=$transaction_id;
                """;
            Add(projectState, "$transaction_id", transactionId);
            projectState.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
