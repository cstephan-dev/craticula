namespace Demo.Api.Seed;

/// <summary>
/// One database the <c>seed</c> command can fill. <see cref="SeedAsync"/> drops, creates and loads the rows and returns the
/// stored count; <see cref="Describe"/> is the line printed for that count; <see cref="Hint"/> follows an error.
/// <see cref="Default"/> targets are seeded when no provider is named.
/// </summary>
public sealed record SeedTarget(
    string Name,
    Func<IReadOnlyList<OrderRow>, Task<long>> SeedAsync,
    Func<long, string> Describe,
    string Hint,
    bool Default = true);

/// <summary>The seedable providers, by the same names as the API routes. The later engine tasks add theirs to <see cref="All"/>.</summary>
public static class SeedTargets
{
    public static IReadOnlyList<SeedTarget> All { get; } =
    [
        new SeedTarget(
            "postgres",
            rows => PostgresSeeder.SeedAsync(DemoSettings.Postgres, rows),
            count => $"PostgreSQL: {count} rows in table orders.",
            "Is `docker compose up -d` running? Override DEMO_PG if you changed the port."),
        new SeedTarget(
            "mongo",
            rows => MongoSeeder.SeedAsync(DemoSettings.Mongo, rows),
            count => $"MongoDB: {count} documents in collection orders.",
            "Is `docker compose up -d` running? Override DEMO_MONGO if you changed the port.")
    ];
}
