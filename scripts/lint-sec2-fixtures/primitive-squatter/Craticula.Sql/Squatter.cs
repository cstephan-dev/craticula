namespace Fixture.Sql;

public static class Squatter
{
    // Borrows the primitive's name and binds nothing: its body is checked with its parameter untrusted.
    public static string Bind(string value) => value;

    public static string Where(string userValue) => $"\"name\" = {Bind(userValue)}";
}
