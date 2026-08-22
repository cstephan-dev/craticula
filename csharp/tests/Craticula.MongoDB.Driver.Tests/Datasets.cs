using MongoDB.Bson;

namespace Craticula.MongoDB.Driver.Tests;

/// <summary>
/// The inline datasets this suite's cases are seeded with — one method per case, with a comment on every
/// row saying why that row is there. Own INTERNAL copy of <c>Craticula.Dapper.Tests.Datasets</c>'s own
/// discipline: a value in each row that only reads correctly if materialization actually ran (a
/// <see cref="Decimal128"/> keeping its scale, a plain ISO date string reading back as a
/// <see cref="DateOnly"/>), and a null in every nullable column a case's own projection touches.
///
/// <para><b>BSON shapes are chosen to match what <c>Craticula.Mongo.MongoExtendedJson.Encode</c> compiles
/// a filter/comparison against</b> — <c>Craticula.MongoDB.Driver.MongoValueReader</c>'s own remarks name
/// the same two conventions: a <c>date</c> field is a plain ISO string (never a BSON Date), and a
/// <c>datetime</c> field is a real BSON Date. Storing them any other way would still often READ back
/// correctly (both reader arms accept either shape defensively) but would compare WRONG against a
/// compiled filter, which is not exercised by these particular fixtures but is worth getting right
/// anyway.</para>
/// </summary>
internal static class Datasets
{
    /// <summary>
    /// <c>page-export/008</c>'s seven-column <c>orders</c> schema. Every cell here is a claim about the
    /// CLR type it must arrive as after materialization: <c>19.99</c> keeps its scale only if it is read
    /// back as a <see cref="decimal"/> from a <see cref="BsonDecimal128"/>, <c>2024-01-01</c> is ISO only
    /// if it is read back as a <see cref="DateOnly"/> from a plain BSON string.
    ///
    /// <para><b>The stableKey <c>id</c> is stored under the document's own <c>_id</c> key</b> — this
    /// fixture's schema declares <c>physical.mongo.path: "_id"</c> for it, a real SC-4 case this dataset
    /// exercises rather than avoids: an ordinary/no-projection grid fetch must re-key <c>_id</c> back to
    /// the logical colId <c>id</c> via the forced <c>$project</c>
    /// (<see cref="MongoGridQuery"/>'s own <c>WithExplicitProjection</c>), exactly like
    /// <c>customer.name</c>'s nested path below.</para>
    ///
    /// <para><b><c>customerName</c> is stored NESTED</b> (<c>{"customer":{"name": ...}}</c>), matching
    /// this schema's <c>physical.mongo.path: "customer.name"</c> — proving the same re-keying for a
    /// nested document path, not just a top-level rename.</para>
    /// </summary>
    public static IReadOnlyList<BsonDocument> ExportableOrders() =>
    [
        Order(1L, "2024-01-01", "Acme", 19.99m, "shipped", "EMEA", null),
        Order(2L, null, null, 100.55m, "pending", null, "second"),
        Order(3L, "2024-03-01", "Beta, Inc", null, "shipped", "APAC", null),
        Order(4L, "2024-04-01", "Zed", -5.25m, null, "EMEA", null),
        Order(5L, "2024-05-02", "Yves", 0.75m, "shipped", "EMEA", "ok")
    ];

    private static BsonDocument Order(
        long id, string? orderDate, string? customerName, decimal? totalAmount, string? status, string? region, string? notes) =>
        new()
        {
            ["_id"] = id,
            ["orderDate"] = orderDate is null ? BsonNull.Value : orderDate,
            ["customer"] = new BsonDocument { ["name"] = customerName is null ? BsonNull.Value : customerName },
            ["totalAmount"] = totalAmount is null ? BsonNull.Value : new BsonDecimal128(totalAmount.Value),
            ["status"] = status is null ? BsonNull.Value : status,
            ["region"] = region is null ? BsonNull.Value : region,
            ["notes"] = notes is null ? BsonNull.Value : notes
        };

    /// <summary>
    /// <c>group-aggregation/001</c>'s <c>sales_fact</c> schema: six rows across three regions, proving a
    /// real <c>$group</c>+<c>$sum</c>/<c>$avg</c> over <see cref="BsonDecimal128"/>/<see cref="BsonInt64"/>
    /// values against the actual server, not merely against the compiler's own artifact.
    ///
    /// <para>AMER carries a null <c>amount</c> on one of its three rows (id <c>s6</c>) — MongoDB's own
    /// <c>$sum</c> accumulator ignores a non-numeric/missing value rather than treating it as zero or
    /// failing, so AMER's expected sum (<c>30.00</c>, from <c>10.00 + 20.00</c>) is only correct if that
    /// skip actually happens. EMEA's two <c>quantity</c> values (<c>3</c>, <c>4</c>) average to a
    /// genuinely fractional <c>3.5</c> — distinguishing a real <see cref="double"/> materialization from
    /// one that silently rounded or truncated to <c>3</c>/<c>4</c>.</para>
    /// </summary>
    public static IReadOnlyList<BsonDocument> SalesFact() =>
    [
        Sale("s1", "EMEA", 100.00m, 3L),
        Sale("s2", "EMEA", 250.50m, 4L),
        Sale("s3", "APAC", 75.25m, 5L),
        Sale("s4", "AMER", 10.00m, 1L),
        Sale("s5", "AMER", 20.00m, 2L),
        Sale("s6", "AMER", null, 7L) // null amount: proves $sum skips it rather than failing/zeroing
    ];

    private static BsonDocument Sale(string id, string region, decimal? amount, long quantity) => new()
    {
        ["id"] = id,
        ["region"] = region,
        ["amount"] = amount is null ? BsonNull.Value : new BsonDecimal128(amount.Value),
        ["quantity"] = quantity
    };
}
