namespace Fixture.Sql;

public sealed class FixtureCompilationException(string message) : Exception(message);

public static class Messages
{
    // Exception messages are not query text, however they are built.
    public static string Require(string? value, string field) =>
        value ?? throw new FixtureCompilationException($"Field '{field}' requires a value, got none.");

    public static void Reject(object request) =>
        throw new InvalidOperationException("The request " + request + " is not supported.");

    private static FixtureCompilationException Unknown(string field, object? value) =>
        new($"Unknown field '{field}' with value '{value}'.");
}
