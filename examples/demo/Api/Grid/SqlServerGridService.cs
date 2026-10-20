using Craticula.Core.Export;
using Craticula.Core.Grid;
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
        this.connectionString = Normalise(connectionString);
    }

    /// <summary>
    /// The connection string the service uses. <c>ConnectRetryCount</c> is always forced to 0, whatever the setting says, so a
    /// stopped container answers 503 as soon as the connect timeout expires instead of after the driver's retries. Throws
    /// <see cref="ArgumentException"/> for a malformed string; the host checks it at startup (<c>OptionalProviders</c>).
    /// </summary>
    public static string Normalise(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { ConnectRetryCount = 0 };
        if (!builder.ShouldSerialize("Connect Timeout"))
        {
            builder.ConnectTimeout = 5;
        }

        return builder.ConnectionString;
    }

    public string Name => "sqlserver";

    public async Task<GridPageResult> FetchRowsAsync(PreparedRows request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var page = await DapperGridQuery.FetchPageAsync(
            connection, ToSqlRequest(request), MssqlDialect.Instance, includeRowCount: true, cancellationToken);
        return page.ToGridPageResult();
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

    public async IAsyncEnumerable<ExportRow> StreamExportAsync(
        PreparedRows request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await foreach (var row in DapperExportStream.StreamAsync(
            connection, ToSqlRequest(request), MssqlDialect.Instance, options: null, cancellationToken))
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

        var query = SqlQueryBuilder.Build(sqlRequest, MssqlDialect.Instance);
        return new CompiledQueryView("sql", query.Dialect, query.Sql, query.Parameters);
    }

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
