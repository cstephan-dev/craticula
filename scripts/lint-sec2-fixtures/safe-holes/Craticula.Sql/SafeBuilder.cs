using System.Text;

namespace Fixture.Sql;

public interface IDialect
{
    string QuoteIdentifier(string identifier);
}

public interface IParameterSink
{
    string Bind(object? value);
}

public enum SortDirection
{
    Asc,
    Desc
}

public sealed record Filter(string Column, object? Value, IReadOnlyList<object?> Values, SortDirection Direction);

public static class SafeBuilder
{
    private const string Table = "orders";

    // Everything spliced here is a quoted identifier, a placeholder from Bind, a constant, an enum or a literal.
    public static string Build(Filter filter, IDialect dialect, IParameterSink parameters)
    {
        var column = dialect.QuoteIdentifier(filter.Column);
        var placeholder = parameters.Bind(filter.Value);
        var order = filter.Direction == SortDirection.Asc ? "ASC" : "DESC";

        var sql = new StringBuilder("SELECT * FROM ")
            .Append(dialect.QuoteIdentifier(Table))
            .Append(" WHERE ")
            .Append(Comparison(column, placeholder))
            .Append(" AND ")
            .Append(column)
            .Append($" IN ({string.Join(", ", filter.Values.Select(parameters.Bind))})")
            .Append(" ORDER BY ")
            .Append(column)
            .Append(' ')
            .Append(order)
            .Append($" -- {nameof(Build)} {Table} {SortDirection.Desc} {Keyword(filter.Direction)}");

        return sql.ToString();
    }

    // A parameter is safe when every call site passes something safe: here, a quoted column and a placeholder.
    private static string Comparison(string quotedColumn, string placeholder) => $"{quotedColumn} = {placeholder}";

    // An enum renders one of its compiled-in member names.
    private static string Keyword(SortDirection direction) => $"{direction}";
}
