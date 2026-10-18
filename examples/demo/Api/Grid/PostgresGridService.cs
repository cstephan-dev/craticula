using Craticula.Core.Export;
using Craticula.Core.Grid;
using Craticula.Dapper;
using Craticula.Sql;
using Npgsql;

namespace Demo.Api.Grid;

/// <summary>PostgreSQL through <c>Craticula.Dapper</c> and <c>PostgresDialect</c>.</summary>
public sealed class PostgresGridService(NpgsqlDataSource dataSource, GridRequestPreparer preparer) : IGridService
{
    public string Name => "postgres";

    public async Task<GridPageResult> FetchRowsAsync(PreparedRows request, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var page = await DapperGridQuery.FetchPageAsync(
            connection, ToSqlRequest(request), PostgresDialect.Instance, includeRowCount: true, cancellationToken);
        return page.ToGridPageResult();
    }

    /// <summary>The Dapper adapter has no entry point for BM-3's distinct values; see <see cref="PostgresSetValues"/>.</summary>
    public Task<IReadOnlyList<object?>> FetchSetValuesAsync(PreparedSetValues request, CancellationToken cancellationToken) =>
        PostgresSetValues.FetchAsync(dataSource, preparer.Schema, request, cancellationToken);

    public async IAsyncEnumerable<ExportRow> StreamExportAsync(
        PreparedRows request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await foreach (var row in DapperExportStream.StreamAsync(
            connection, ToSqlRequest(request), PostgresDialect.Instance, options: null, cancellationToken))
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

        var query = SqlQueryBuilder.Build(sqlRequest, PostgresDialect.Instance);
        return new CompiledQueryView("sql", query.Dialect, query.Sql, query.Parameters);
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
