namespace Demo.Api.Seed;

/// <summary>The `seed` command: generate once, write the same rows to both databases, print the counts.</summary>
public static class SeedCommand
{
    public static async Task<int> RunAsync()
    {
        var rows = OrderGenerator.Generate();
        Console.WriteLine($"Generated {rows.Count} orders (seed {OrderGenerator.Seed}).");

        try
        {
            var pg = await PostgresSeeder.SeedAsync(DemoSettings.Postgres, rows);
            Console.WriteLine($"PostgreSQL: {pg} rows in table orders.");

            var mongo = await MongoSeeder.SeedAsync(DemoSettings.Mongo, rows);
            Console.WriteLine($"MongoDB: {mongo} documents in collection orders.");
            return pg == rows.Count && mongo == rows.Count ? 0 : 1;
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or MongoDB.Driver.MongoException or TimeoutException)
        {
            Console.Error.WriteLine($"Seed failed: {ex.Message}");
            Console.Error.WriteLine("Is `docker compose up -d` running? Override DEMO_PG / DEMO_MONGO if you changed the ports.");
            return 2;
        }
    }
}
