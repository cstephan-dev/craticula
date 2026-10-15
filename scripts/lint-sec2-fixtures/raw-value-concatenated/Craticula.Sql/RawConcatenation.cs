namespace Fixture.Sql;

public interface IDialect
{
    string QuoteIdentifier(string identifier);
}

public sealed record FilterRequest(string Name);

public static class RawConcatenation
{
    // The same defect spelled with `+`: the quoted table is fine, the client's name is not.
    public static string Where(FilterRequest request, IDialect dialect) =>
        "SELECT * FROM " + dialect.QuoteIdentifier("customers") + " WHERE \"name\" = '" + request.Name + "'";
}
