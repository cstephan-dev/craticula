using Craticula.Core.Export;
using Craticula.Core.Grid;
using Craticula.Core.Ir;
using Craticula.Dapper;
using Craticula.Sql;
using Microsoft.Data.SqlClient;

namespace Demo.Api.Grid;

/// <summary>
/// SQL Server through <c>Craticula.Dapper</c> and <c>MssqlDialect</c>. An optional engine: registered only when
/// <c>DEMO_SQLSERVER</c> is set. Each call opens its own pooled connection; a connect timeout of 5 s (unless the connection string sets one) and no connect retry make a
/// stopped container answer 503 after about that long instead of the driver's 15 s default (measured: a refused connection
/// is retried until the timeout expires).
/// </summary>
public sealed class SqlServerGridService : IGridService
{
    private readonly string connectionString;
    private readonly GridRequestPreparer preparer;

    public SqlServerGridService(string connectionString, GridRequestPreparer preparer)
    {
        this.preparer = preparer;
        var builder = new SqlConnectionStringBuilder(connectionString) { ConnectRetryCount = 0 };
        if (!builder.ShouldSerialize("Connect Timeout"))
        {
            builder.ConnectTimeout = 5;
        }

        this.connectionString = builder.ConnectionString;
    }

    public string Name => "sqlserver";

    public async Task<GridPageResult> FetchRowsAsync(PreparedRows request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var page = await DapperGridQuery.FetchPageAsync(
            connection, ToSqlRequest(request), MssqlDialect.Instance, includeRowCount: true, cancellationToken);
        var result = page.ToGridPageResult();
        return result with { Rows = [.. result.Rows.Select(MarkInstantsUtc)] };
    }

    /// <summary>The Dapper adapter has no entry point for BM-3's distinct values; see <see cref="SetValuesRunner"/>.</summary>
    public Task<IReadOnlyList<object?>> FetchSetValuesAsync(PreparedSetValues request, CancellationToken cancellationToken) =>
        SetValuesRunner.FetchAsync(
            MssqlDialect.Instance,
            preparer.Schema,
            request,
            async token => await OpenAsync(token),
            SqlServerBinding.CreateCommand,
            cancellationToken);

    /// <summary>
    /// The export is walked a page at a time through the grid entry point, not through <c>DapperExportStream</c>: on SQL Server the
    /// stream's keyset query lists the sort key twice when it is also an export column (<c>SELECT [id], ..., [id] ... ORDER BY
    /// [id]</c>), and the engine answers error 209 "Ambiguous column name" (measured on 2022-CU14 for every export that
    /// includes <c>id</c>). The walk pages with OFFSET/FETCH over the compiled statement, whose ORDER BY already ends in the
    /// stable key (SEM-6), so the order is total and the demo table is static during an export. Chunks of
    /// <see cref="ExportPageSize"/> keep memory flat like the stream does.
    /// </summary>
    public async IAsyncEnumerable<ExportRow> StreamExportAsync(
        PreparedRows request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var baseRequest = ToSqlRequest(request);
        for (var start = 0; ; start += ExportPageSize)
        {
            var page = await DapperGridQuery.FetchPageAsync(
                connection,
                baseRequest with { Page = new RangePage { StartRow = start, EndRow = start + ExportPageSize } },
                MssqlDialect.Instance,
                includeRowCount: false,
                cancellationToken);
            foreach (var row in page.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ExportRow(MarkInstantsUtc(row));
            }

            if (page.Rows.Count < ExportPageSize)
            {
                yield break;
            }
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

        var query = SqlQueryBuilder.Build(sqlRequest, MssqlDialect.Instance);
        return new CompiledQueryView("sql", query.Dialect, query.Sql, query.Parameters);
    }

    private const int ExportPageSize = 5000;

    /// <summary>
    /// SQL Server's <c>datetime2</c> carries no zone, so the driver hands back a <see cref="DateTime"/> of kind Unspecified and
    /// the JSON writer would drop the trailing <c>Z</c> every other provider sends. The column holds UTC (the seed writes UTC
    /// instants and the schema's datetime is an absolute instant, SEM-4), so the kind is stated here.
    /// </summary>
    private static IReadOnlyList<object?> MarkInstantsUtc(IReadOnlyList<object?> cells) =>
        [.. cells.Select(cell => cell is DateTime { Kind: DateTimeKind.Unspecified } instant ? DateTime.SpecifyKind(instant, DateTimeKind.Utc) : cell)];

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
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
