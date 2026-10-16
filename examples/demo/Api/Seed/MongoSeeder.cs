using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Demo.Api.Seed;

/// <summary>
/// Drops collection <c>orders</c> and reloads it. Representations follow spec/semantics.md SEM-4 and the Mongo
/// fixtures: decimal = Decimal128, datetime = BSON Date (UTC), date = a plain ISO string "yyyy-MM-dd"
/// (never a BSON Date: a date has no instant), null = explicit BSON null, stableKey stored as _id.
/// </summary>
public static class MongoSeeder
{
    private const int BatchSize = 2_000;

    public static async Task<long> SeedAsync(string connectionString, IReadOnlyList<OrderRow> rows)
    {
        var database = new MongoClient(connectionString).GetDatabase(DemoSettings.MongoDatabase);
        await database.DropCollectionAsync("orders");
        var collection = database.GetCollection<BsonDocument>("orders");

        for (var offset = 0; offset < rows.Count; offset += BatchSize)
        {
            var batch = rows.Skip(offset).Take(BatchSize).Select(ToDocument).ToList();
            await collection.InsertManyAsync(batch);
        }

        await collection.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("country")),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("category")),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("orderedAt"))
        ]);

        return await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
    }

    public static BsonDocument ToDocument(OrderRow row) => new()
    {
        ["_id"] = row.Id,
        ["orderNo"] = row.OrderNo,
        ["customer"] = Text(row.Customer),
        ["country"] = Text(row.Country),
        ["category"] = Text(row.Category),
        ["status"] = row.Status,
        ["amount"] = row.Amount is { } a ? new BsonDecimal128(a) : BsonNull.Value,
        ["quantity"] = row.Quantity is { } q ? new BsonInt64(q) : BsonNull.Value,
        ["orderedAt"] = new BsonDateTime(DateTime.SpecifyKind(row.OrderedAt, DateTimeKind.Utc)),
        ["shippedOn"] = row.ShippedOn is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : BsonNull.Value,
        ["isPriority"] = row.IsPriority
    };

    private static BsonValue Text(string? value) => value is null ? BsonNull.Value : value;
}
