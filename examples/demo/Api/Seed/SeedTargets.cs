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

/// <summary>The seedable providers, by the same names as the API routes.</summary>
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
            "Is `docker compose up -d` running? Override DEMO_MONGO if you changed the port."),
        new SeedTarget(
            "sqlite",
            rows => SqliteSeeder.SeedAsync(DemoSettings.SqlitePath, rows),
            count => $"SQLite: {count} rows in table orders ({DemoSettings.SqlitePath}).",
            "SQLite needs no server. Is the file writable and not locked by another process? Set DEMO_SQLITE to use another path."),
        // Optional and heavy: not a default target. `seed sqlserver` fills it; without DEMO_SQLSERVER the seeder says what to set.
        new SeedTarget(
            "sqlserver",
            rows => SqlServerSeeder.SeedAsync(
                DemoSettings.OptionalSetting(DemoSettings.SqlServerEnvVar)
                    ?? throw new SeedSettingException($"{DemoSettings.SqlServerEnvVar} is not set."),
                rows,
                DemoSettings.OptionalSetting(DemoSettings.SqlServerAdminEnvVar)),
            count => $"SQL Server: {count} rows in table orders.",
            "Is `docker compose --profile sqlserver up -d` running and healthy? Set DEMO_SQLSERVER (the read-only login) and DEMO_SQLSERVER_ADMIN (the seed's login) to its connection strings (README.md has the ones for the compose file).",
            Default: false),
        new SeedTarget(
            "mysql",
            rows => MySqlSeeder.SeedAsync(
                DemoSettings.OptionalSetting(DemoSettings.MySqlEnvVar)
                    ?? throw new SeedSettingException($"{DemoSettings.MySqlEnvVar} is not set."),
                rows),
            count => $"MySQL: {count} rows in table orders.",
            "Is `docker compose --profile mysql up -d` running and healthy? Set DEMO_MYSQL to its connection string (README.md has the one for the compose file).",
            Default: false),
        new SeedTarget(
            "opensearch",
            rows => OpenSearchSeeder.SeedAsync(
                DemoSettings.OptionalSetting(DemoSettings.OpenSearchEnvVar)
                    ?? throw new SeedSettingException($"{DemoSettings.OpenSearchEnvVar} is not set."),
                rows),
            count => $"OpenSearch: {count} documents in index orders.",
            "Is `docker compose --profile opensearch up -d --wait` running and healthy? Set DEMO_OPENSEARCH to its URL (README.md has the one for the compose file).",
            Default: false),
        new SeedTarget(
            "elasticsearch",
            rows => OpenSearchSeeder.SeedAsync(
                DemoSettings.OptionalSetting(DemoSettings.ElasticsearchEnvVar)
                    ?? throw new SeedSettingException($"{DemoSettings.ElasticsearchEnvVar} is not set."),
                rows,
                DemoSettings.ElasticsearchEnvVar,
                "Elasticsearch"),
            count => $"Elasticsearch: {count} documents in index orders.",
            "Is `docker compose --profile elasticsearch up -d --wait` running and healthy? Set DEMO_ELASTICSEARCH to its URL (README.md has the one for the compose file).",
            Default: false)
    ];
}
