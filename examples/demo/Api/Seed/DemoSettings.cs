namespace Demo.Api.Seed;

/// <summary>Connection strings for the demo databases. PostgreSQL and MongoDB have defaults that match docker-compose.yml;
/// an optional engine has none: it is on only when its environment variable is set (see <see cref="OptionalSetting"/>).</summary>
public static class DemoSettings
{
    public static string Postgres =>
        Environment.GetEnvironmentVariable("DEMO_PG")
        ?? "Host=127.0.0.1;Port=55433;Username=demo;Password=demo;Database=demo";

    public static string Mongo =>
        Environment.GetEnvironmentVariable("DEMO_MONGO")
        ?? "mongodb://127.0.0.1:57017";

    public const string MongoDatabase = "demo";

    /// <summary>The value of an optional engine's variable, or null when it is unset or blank (the engine is not configured).</summary>
    public static string? OptionalSetting(string envVar) =>
        NullIfBlank(Environment.GetEnvironmentVariable(envVar));

    /// <summary>True when an optional engine's variable is set.</summary>
    public static bool IsConfigured(string envVar) => OptionalSetting(envVar) is not null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
