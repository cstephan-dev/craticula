namespace Fixture.Dapper;

public sealed record Request(string Value);

public static class Statements
{
    private static string Head() => "SELECT * FROM t WHERE c = ";

    // `var` hides the string type: the lint takes it from Head()'s declared return type.
    public static string Concatenated(Request request)
    {
        var head = Head();
        var sql = head + request.Value;
        return sql;
    }

    public static string Appended(Request request)
    {
        var sql = Head();
        sql += request.Value;
        return sql;
    }
}
