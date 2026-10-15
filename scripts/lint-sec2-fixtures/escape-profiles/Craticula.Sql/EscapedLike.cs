namespace Fixture.Sql;

public interface IDialect
{
    string QuoteIdentifier(string identifier);
}

public static class EscapedLike
{
    // Escaping makes a value literal for LIKE, not safe for SQL text: a quote still ends the literal. Bind it.
    public static string Contains(IDialect dialect, string value) =>
        $"{dialect.QuoteIdentifier("name")} LIKE '%{SqlPatternEscaping.Escape(value, '\\')}%' ESCAPE '\\'";
}
