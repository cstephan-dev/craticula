namespace Fixture.Dapper;

public sealed record CompiledQuery(string Sql);

public static class CountWrap
{
    // Wrapping the builder's own output: its text came out of the linted compiler.
    public static string Count(object request, object dialect)
    {
        var inner = SqlQueryBuilder.Build(request, dialect);
        return $"SELECT COUNT(*) FROM ({inner.Sql}) craticula_count";
    }

    // Wrapping whatever a caller hands over: nothing says this text was compiled.
    public static string Wrap(CompiledQuery query) => $"SELECT COUNT(*) FROM ({query.Sql}) craticula_count";
}
