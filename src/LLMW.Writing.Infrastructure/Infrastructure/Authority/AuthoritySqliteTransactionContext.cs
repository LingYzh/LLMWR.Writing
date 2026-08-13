using System.Data.Common;

namespace LLMW.Writing.Infrastructure.Authority;

public sealed class AuthoritySqliteTransactionContext
{
    internal AuthoritySqliteTransactionContext(DbConnection connection, DbTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    public DbConnection Connection { get; }

    public DbTransaction Transaction { get; }

    public DbCommand CreateCommand(string commandText)
    {
        var command = Connection.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = commandText;
        return command;
    }
}
