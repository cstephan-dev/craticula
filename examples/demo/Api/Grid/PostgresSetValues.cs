using Craticula.Core.Schema;
using Craticula.Sql;
using Npgsql;

namespace Demo.Api.Grid;

/// <summary>
/// BM-3's distinct values on PostgreSQL, shared by the <c>postgres</c> and <c>ef</c> providers because neither adapter has an entry
/// point for them. It compiles with <c>SqlQueryBuilder.BuildSetValues</c> and executes ONLY that artifact (its text and its ordered
/// parameters). The binding is the one the Dapper adapter does for the postgres dialect: unnamed positional parameters for the
/// <c>$n</c> placeholders, a list value becoming a typed array.
/// </summary>
internal static class PostgresSetValues
{
    public static async Task<IReadOnlyList<object?>> FetchAsync(
        NpgsqlDataSource dataSource, GridSchema schema, PreparedSetValues request, CancellationToken cancellationToken)
    {
        var query = SqlQueryBuilder.BuildSetValues(
            new SqlSetValuesRequest
            {
                Schema = schema,
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
