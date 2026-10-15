namespace Fixture.Sql;

public static class Kept
{
    private const string Table = "orders";

    public static string Count() => $"SELECT COUNT(*) FROM {Table}";
}
