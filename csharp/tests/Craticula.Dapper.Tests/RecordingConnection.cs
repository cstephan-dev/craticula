using System.Data;
using System.Data.Common;

namespace Craticula.Dapper.Tests;

/// <summary>
/// A <see cref="DbConnection"/> that forwards everything to a real one and remembers the text of every
/// command it ran, so a test can assert on the SQL that actually reached the engine. SQLite is lenient
/// about statements that SQL Server rejects (a repeated column in the select list next to an
/// <c>ORDER BY</c> on it is error 209 there), so only the command text can catch that class of defect
/// without a live SQL Server (bug #2510). It derives from <see cref="DbConnection"/> because the adapter
/// reads through Dapper's async API, which needs a <see cref="DbCommand"/>.
/// </summary>
internal sealed class RecordingConnection(DbConnection inner) : DbConnection
{
    public List<string> CommandTexts { get; } = [];

    public override string ConnectionString
    {
        get => inner.ConnectionString;
        set => inner.ConnectionString = value;
    }

    public override string Database => inner.Database;

    public override string DataSource => inner.DataSource;

    public override string ServerVersion => inner.ServerVersion;

    public override ConnectionState State => inner.State;

    public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);

    public override void Close() => inner.Close();

    public override void Open() => inner.Open();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        inner.BeginTransaction(isolationLevel);

    protected override DbCommand CreateDbCommand() => new RecordingCommand(inner.CreateCommand(), this);

    protected override void Dispose(bool disposing)
    {
        // The wrapped connection belongs to the fixture database; it disposes it.
    }

    private sealed class RecordingCommand(DbCommand command, RecordingConnection owner) : DbCommand
    {
        public override string CommandText
        {
            get => command.CommandText;
            set => command.CommandText = value;
        }

        public override int CommandTimeout
        {
            get => command.CommandTimeout;
            set => command.CommandTimeout = value;
        }

        public override CommandType CommandType
        {
            get => command.CommandType;
            set => command.CommandType = value;
        }

        public override bool DesignTimeVisible
        {
            get => command.DesignTimeVisible;
            set => command.DesignTimeVisible = value;
        }

        public override UpdateRowSource UpdatedRowSource
        {
            get => command.UpdatedRowSource;
            set => command.UpdatedRowSource = value;
        }

        protected override DbConnection? DbConnection
        {
            get => owner;
            set { }
        }

        protected override DbParameterCollection DbParameterCollection => command.Parameters;

        protected override DbTransaction? DbTransaction
        {
            get => command.Transaction;
            set => command.Transaction = value;
        }

        public override void Cancel() => command.Cancel();

        public override int ExecuteNonQuery()
        {
            owner.CommandTexts.Add(command.CommandText);
            return command.ExecuteNonQuery();
        }

        public override object? ExecuteScalar()
        {
            owner.CommandTexts.Add(command.CommandText);
            return command.ExecuteScalar();
        }

        public override void Prepare() => command.Prepare();

        protected override DbParameter CreateDbParameter() => command.CreateParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            owner.CommandTexts.Add(command.CommandText);
            return command.ExecuteReader(behavior);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                command.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
