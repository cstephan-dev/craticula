using Craticula.Core.Export;
using Craticula.Core.Grid;
using Craticula.Dapper;
using Craticula.Sql;
using Microsoft.Data.Sqlite;

namespace Demo.Api.Grid;

/// <summary>
/// SQLite through <c>Craticula.Dapper</c> and <c>SqliteDialect</c>. The database is a file; each call opens its own connection
/// on it with <c>Mode=ReadWrite</c>, so a missing file is an error (503) and not a new empty database. Pooling is off: a pooled
/// connection would keep a deleted or re-seeded file open.
/// </summary>
public sealed class SqliteGridService(string path, GridRequestPreparer preparer) : IGridService
{
    // A lock held by `seed sqlite` should end the request quickly (503) instead of the driver's 30 s default.
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false,
        DefaultTimeout = 5
    }.ConnectionString;

    public string Name => "sqlite";

    public async Task<GridPageResult> FetchRowsAsync(PreparedRows request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var page = await DapperGridQuery.FetchPageAsync(
            connection, ToSqlRequest(request), SqliteDialect.Instance, includeRowCount: true, cancellationToken);
        return page.ToGridPageResult();
    }

    /// <summary>The Dapper adapter has no entry point for BM-3's distinct values; see <see cref="SetValuesRunner"/>.</summary>
    public Task<IReadOnlyList<object?>> FetchSetValuesAsync(PreparedSetValues request, CancellationToken cancellationToken) =>
        SetValuesRunner.FetchAsync(
            SqliteDialect.Instance,
            preparer.Schema,
            request,
            async token => await OpenAsync(token),
            SqliteBinding.CreateCommand,
            cancellationToken);

    public async IAsyncEnumerable<ExportRow> StreamExportAsync(
        PreparedRows request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await foreach (var row in DapperExportStream.StreamAsync(
            connection, ToSqlRequest(request), SqliteDialect.Instance, options: null, cancellationToken))
        {
            yield return row;
        }
    }

    public CompiledQueryView Compile(PreparedRows request)
    {
        var sqlRequest = ToSqlRequest(request);
        // The adapter spells out the schema's columns for an ungrouped grid request; show the same statement.
        if (sqlRequest.Group is null && sqlRequest.Projection is null)
        {
            sqlRequest = sqlRequest with { Projection = preparer.Schema.FieldOrder };
        }

        var query = SqlQueryBuilder.Build(sqlRequest, SqliteDialect.Instance);
        return new CompiledQueryView("sql", query.Dialect, query.Sql, query.Parameters);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private SqlQueryRequest ToSqlRequest(PreparedRows request) => new()
    {
        Schema = preparer.Schema,
        Filter = request.Filter,
        Sort = request.Parsed.Sort,
        Page = request.Parsed.Page,
        Group = request.Parsed.Group,
        Aggregations = request.Parsed.Aggregations,
        Projection = request.Parsed.Export?.ProjectionColumns
    };
}
