namespace Fixture.Sql;

public sealed class QuotingDialect
{
    // The quoting primitive: the one place a raw identifier meets the delimiter, which it doubles first.
    public string QuoteIdentifier(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
