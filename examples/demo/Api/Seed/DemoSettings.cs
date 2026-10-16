namespace Demo.Api.Seed;

/// <summary>Connection strings for the two demo databases; the defaults match docker-compose.yml.</summary>
public static class DemoSettings
{
    public static string Postgres =>
        Environment.GetEnvironmentVariable("DEMO_PG")
        ?? "Host=127.0.0.1;Port=55433;Username=demo;Password=demo;Database=demo";

    public static string Mongo =>
        Environment.GetEnvironmentVariable("DEMO_MONGO")
        ?? "mongodb://127.0.0.1:57017";

    public const string MongoDatabase = "demo";
}
