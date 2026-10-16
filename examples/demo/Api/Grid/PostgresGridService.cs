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

    /// <summary>
    /// The Dapper adapter has no entry point for BM-3's distinct values, so this compiles with
    /// <c>SqlQueryBuilder.BuildSetValues</c> and executes ONLY that artifact (its text and its ordered parameters).
    /// The binding is the same one the adapter does for the postgres dialect: unnamed positional parameters for the
    /// <c>$n</c> placeholders, a list value becoming a typed array.
    /// </summary>
    public async Task<IReadOnlyList<object?>> FetchSetValuesAsync(PreparedSetValues request, CancellationToken cancellationToken)
    {
        var query = SqlQueryBuilder.BuildSetValues(
            new SqlSetValuesRequest
            {
                Schema = preparer.Schema,
                ColId = request.ColId,
                Filter = request.Filter,
                RowLimit = request.RowLimit
            },
            PostgresDialect.Instance);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(query.Sql, connection);
        foreach (var value in query.Parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = Bind(value) });
        }

        var values = new List<object?>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(await reader.IsDBNullAsync(0, cancellationToken) ? null : reader.GetValue(0));
        }

        return values;
    }

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

    private static object Bind(object? value) => value switch
    {
        null => DBNull.Value,
        IReadOnlyList<object?> set => ToTypedArray(set),
        _ => value
    };

    private static Array ToTypedArray(IReadOnlyList<object?> values)
    {
        var elementType = values.FirstOrDefault(v => v is not null)?.GetType() ?? typeof(string);
        var arrayType = elementType.IsValueType && values.Any(v => v is null)
            ? typeof(Nullable<>).MakeGenericType(elementType)
            : elementType;
        var typed = Array.CreateInstance(arrayType, values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            typed.SetValue(values[i], i);
        }

        return typed;
    }
}
