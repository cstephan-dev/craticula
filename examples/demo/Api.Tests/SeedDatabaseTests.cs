using Dapper;
using Demo.Api.Seed;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;

namespace Demo.Api.Tests;

// Same collection as the API tests: it drops and reloads both databases, so it must never run beside them.
[Collection("api")]
public class SeedDatabaseTests
{
    [RequiresDatabasesFact]
    public async Task Seeding_twice_gives_equal_counts_and_equal_rows_in_both_databases()
    {
        var rows = OrderGenerator.Generate();

        for (var run = 0; run < 2; run++)
        {
            Assert.Equal(rows.Count, await PostgresSeeder.SeedAsync(DemoSettings.Postgres, rows));
            Assert.Equal(rows.Count, await MongoSeeder.SeedAsync(DemoSettings.Mongo, rows));
        }

        var collection = new MongoClient(DemoSettings.Mongo).GetDatabase(DemoSettings.MongoDatabase)
            .GetCollection<BsonDocument>("orders");
        await using var pg = new NpgsqlConnection(DemoSettings.Postgres);

        foreach (var expected in new[] { rows[0], rows[1], rows[12_345] })
        {
            var doc = await collection.Find(new BsonDocument("_id", expected.Id)).SingleAsync();
            var sql = await pg.QuerySingleAsync<(string order_no, string? customer, decimal? amount, DateTime ordered_at)>(
                "SELECT order_no, customer, amount, ordered_at FROM orders WHERE id = @id", new { id = expected.Id });

            Assert.Equal(sql.order_no, doc["orderNo"].AsString);
            Assert.Equal(sql.customer, doc["customer"].IsBsonNull ? null : doc["customer"].AsString);
            Assert.Equal(sql.amount, doc["amount"].IsBsonNull ? null : doc["amount"].AsDecimal);
            Assert.Equal(sql.ordered_at.ToUniversalTime(), doc["orderedAt"].ToUniversalTime());
        }
    }
}
