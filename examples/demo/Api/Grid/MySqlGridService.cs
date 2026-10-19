using Craticula.Core.Export;
using Craticula.Core.Grid;
using Craticula.Core.Ir;
using Craticula.Dapper;
using Craticula.Sql;
using MySqlConnector;

namespace Demo.Api.Grid;

/// <summary>
/// MySQL 8.4 through <c>Craticula.Dapper</c> and <c>MySqlDialect</c>. An optional engine: registered only when
/// <c>DEMO_MYSQL</c> is set. Each call opens its own pooled connection; a connect timeout of 5 s unless the connection string
/// sets one makes an unreachable server answer 503 after about that long instead of the driver's 15 s default.
/// </summary>
public sealed class MySqlGridService : IGridService
{
    private readonly string connectionString;
    private readonly GridRequestPreparer preparer;

    public MySqlGridService(string connectionString, GridRequestPreparer preparer)
    {
        this.preparer = preparer;
        var builder = new MySqlConnectionStringBuilder(connectionString);
        if (!ConnectionStringNamesATimeout(connectionString))
        {
            builder.ConnectionTimeout = 5;
        }

        this.connectionString = builder.ConnectionString;
    }

    public string Name => "mysql";

    public async Task<GridPageResult> FetchRowsAsync(PreparedRows request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var page = await DapperGridQuery.FetchPageAsync(
            connection, ToSqlRequest(request), MySqlDialect.Instance, includeRowCount: true, cancellationToken);
        var result = page.ToGridPageResult();
        return result with { Rows = [.. result.Rows.Select(MarkInstantsUtc)] };
    }

    /// <summary>The Dapper adapter has no entry point for BM-3's distinct values; see <see cref="SetValuesRunner"/>.</summary>
    public Task<IReadOnlyList<object?>> FetchSetValuesAsync(PreparedSetValues request, CancellationToken cancellationToken) =>
        SetValuesRunner.FetchAsync(
            MySqlDialect.Instance,
            preparer.Schema,
            request,
            async token => await OpenAsync(token),
            MySqlBinding.CreateCommand,
            cancellationToken);

    /// <summary>
    /// The export goes through <c>DapperExportStream</c> (keyset pages over the stable key); the datetimes it reads are marked UTC
    /// like the grid rows, or the CSV would write them without the zone.
    /// </summary>
    public async IAsyncEnumerable<ExportRow> StreamExportAsync(
        PreparedRows request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await foreach (var row in DapperExportStream.StreamAsync(
            connection, ToSqlRequest(request), MySqlDialect.Instance, options: null, cancellationToken))
        {
            yield return MarkInstantsUtc(row);
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

        var query = SqlQueryBuilder.Build(sqlRequest, MySqlDialect.Instance);
        return new CompiledQueryView("sql", query.Dialect, query.Sql, query.Parameters);
    }

    /// <summary>
    /// MySQL's <c>DATETIME</c> carries no zone, so the driver hands back a <see cref="DateTime"/> of kind Unspecified and
    /// the JSON writer would drop the trailing <c>Z</c> every other provider sends. The column holds UTC (the seed writes UTC
    /// instants and the schema's datetime is an absolute instant, SEM-4), so the kind is stated here.
    /// </summary>
    private static IReadOnlyList<object?> MarkInstantsUtc(IReadOnlyList<object?> cells) =>
        [.. cells.Select(cell => cell is DateTime { Kind: DateTimeKind.Unspecified } instant ? DateTime.SpecifyKind(instant, DateTimeKind.Utc) : cell)];

    /// <summary>The builder cannot tell a timeout the string sets from its own default, so the keys are looked up by name.</summary>
    private static bool ConnectionStringNamesATimeout(string connectionString)
    {
        var keys = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
        return keys.ContainsKey("Connection Timeout") || keys.ContainsKey("Connect Timeout") || keys.ContainsKey("ConnectionTimeout");
    }

    private static ExportRow MarkInstantsUtc(ExportRow row) =>
        new(MarkInstantsUtc([.. Enumerable.Range(0, row.Count).Select(index => row[index])]));

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
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
