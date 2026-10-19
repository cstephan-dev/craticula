using System.Data.Common;
using Craticula.Core.Schema;
using Craticula.Sql;

namespace Demo.Api.Grid;

/// <summary>
/// BM-3's distinct values for a Dapper-style SQL provider, because no adapter has an entry point for them. It compiles with
/// <c>SqlQueryBuilder.BuildSetValues</c> for the given dialect and executes ONLY that artifact: its text and its ordered
/// parameters. What differs per engine is the command (the placeholder style and how a value is bound), so the caller passes
/// it in: <see cref="PostgresSetValues"/> binds <c>$n</c> as unnamed Npgsql parameters, <see cref="SqliteBinding"/> binds <c>?n</c>.
/// </summary>
internal static class SetValuesRunner
{
    public static async Task<IReadOnlyList<object?>> FetchAsync(
        ISqlDialect dialect,
        GridSchema schema,
        PreparedSetValues request,
        Func<CancellationToken, Task<DbConnection>> openConnection,
        Func<DbConnection, SqlQuery, DbCommand> createCommand,
        CancellationToken cancellationToken)
    {
        var query = SqlQueryBuilder.BuildSetValues(
            new SqlSetValuesRequest
            {
                Schema = schema,
                ColId = request.ColId,
                Filter = request.Filter,
                RowLimit = request.RowLimit
            },
            dialect);

        await using var connection = await openConnection(cancellationToken);
        await using var command = createCommand(connection, query);

        var values = new List<object?>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(await reader.IsDBNullAsync(0, cancellationToken) ? null : reader.GetValue(0));
        }

        return values;
    }
}
