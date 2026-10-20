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
        this.connectionString = Normalise(connectionString);
    }

    /// <summary>The connection string the service uses (5 s connect timeout unless the setting names one). Throws
    /// <see cref="ArgumentException"/> for a malformed string; the host checks it at startup (<c>OptionalProviders</c>).</summary>
    public static string Normalise(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        if (!ConnectionStringNamesATimeout(connectionString))
        {
            builder.ConnectionTimeout = 5;
        }

        return builder.ConnectionString;
    }

    public string Name => "mysql";

    public async Task<GridPageResult> FetchRowsAsync(PreparedRows request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var page = await DapperGridQuery.FetchPageAsync(
            connection, ToSqlRequest(request), MySqlDialect.Instance, includeRowCount: true, cancellationToken);
        return page.ToGridPageResult();
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

    public async IAsyncEnumerable<ExportRow> StreamExportAsync(
        PreparedRows request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await foreach (var row in DapperExportStream.StreamAsync(
            connection, ToSqlRequest(request), MySqlDialect.Instance, options: null, cancellationToken))
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

        var query = SqlQueryBuilder.Build(sqlRequest, MySqlDialect.Instance);
        return new CompiledQueryView("sql", query.Dialect, query.Sql, query.Parameters);
    }

    /// <summary>The builder cannot tell a timeout the string sets from its own default, so the keys are looked up by name.</summary>
    private static bool ConnectionStringNamesATimeout(string connectionString)
    {
        var keys = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
        return keys.ContainsKey("Connection Timeout") || keys.ContainsKey("Connect Timeout") || keys.ContainsKey("ConnectionTimeout");
    }

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
