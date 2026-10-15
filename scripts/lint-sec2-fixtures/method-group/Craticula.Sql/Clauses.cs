namespace Fixture.Sql;

public interface IDialect
{
    string QuoteIdentifier(string identifier);
}

public sealed record Aggregation(string Field, string? Alias);

public static class Clauses
{
    private static string WhereClause(string column) => $"WHERE {column} = 1";

    public static string Safe(IDialect dialect) => WhereClause(dialect.QuoteIdentifier("amount"));

    // A method group: ConvertAll calls WhereClause with each raw column, out of the lint's sight.
    public static List<string> Unsafe(List<string> userColumns) => userColumns.ConvertAll(WhereClause);

    // Not method groups, though each shares its name with a method here: a record member passed as an argument, and
    // a property of a type the lint cannot see, read in a condition.
    private static string Alias(string expression, IDialect dialect) =>
        $"{expression} AS {dialect.QuoteIdentifier("total")}";

    private static string Columns(string list) => $"SELECT {list}";

    public static string Aliased(Aggregation aggregation, IDialect dialect) =>
        Describe(aggregation.Alias) + Alias("COUNT(*)", dialect);

    public static string Projection(Grid grid, IDialect dialect) =>
        grid.Columns > 0 ? Columns(dialect.QuoteIdentifier("amount")) : Columns("*");

    private static string Describe(string? alias) => alias is null ? "none" : "some";
}
