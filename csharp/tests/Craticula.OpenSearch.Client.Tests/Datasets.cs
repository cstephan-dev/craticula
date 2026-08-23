using System.Text.Json.Nodes;

namespace Craticula.OpenSearch.Client.Tests;

/// <summary>
/// The inline datasets this suite's cases are seeded with — one method per case, with a comment on
/// every row saying why that row is there. Own INTERNAL copy of <c>Craticula.MongoDB.Driver.Tests.Datasets</c>'s
/// own discipline: a value in each row that only reads correctly if materialization actually ran, and a
/// null in every nullable column a case's own projection touches.
///
/// <para><b>Unlike Mongo's dataset, no <c>_id</c>/nested-path re-keying is exercised.</b> Every field in
/// both fixture schemas this suite reuses (<c>page-export/008</c>, <c>group-aggregation/001</c>)
/// declares a <c>physical.search.field</c> equal to its own colId — SC-4's re-mapping concern is real on
/// this backend too (a schema COULD declare otherwise), but neither chosen fixture happens to exercise
/// it, so these documents are keyed directly by colId.</para>
/// </summary>
internal static class Datasets
{
    /// <summary>
    /// <c>page-export/008</c>'s seven-column <c>orders</c> schema. Every cell here is a claim about the
    /// CLR type it must arrive as after materialization: <c>19.99</c> keeps its scale only if it is read
    /// back as a <see cref="decimal"/> from the raw JSON number token
    /// <c>Craticula.OpenSearch.Client.OpenSearchValueReader.ToDecimal</c>'s <see cref="System.Text.Json.JsonElement.GetDecimal"/>
    /// arm parses directly (never bounced through a binary <see cref="double"/> — <c>_source</c> is the
    /// index's own verbatim stored document, unaffected by how the field is MAPPED for search/aggregation),
    /// and <c>2024-01-01</c> is ISO only if it is read back as a <see cref="DateOnly"/> from a plain JSON
    /// string.
    /// </summary>
    public static IReadOnlyList<JsonObject> ExportableOrders() =>
    [
        Order(1L, "2024-01-01", "Acme", 19.99m, "shipped", "EMEA", null),
        Order(2L, null, null, 100.55m, "pending", null, "second"),
        Order(3L, "2024-03-01", "Beta, Inc", null, "shipped", "APAC", null),
        Order(4L, "2024-04-01", "Zed", -5.25m, null, "EMEA", null),
        Order(5L, "2024-05-02", "Yves", 0.75m, "shipped", "EMEA", "ok")
    ];

    private static JsonObject Order(
        long id, string? orderDate, string? customerName, decimal? totalAmount, string? status, string? region, string? notes) =>
        new()
        {
            ["id"] = id,
            ["orderDate"] = orderDate,
            ["customerName"] = customerName,
            ["totalAmount"] = totalAmount is null ? null : JsonValue.Create(totalAmount.Value),
            ["status"] = status,
            ["region"] = region,
            ["notes"] = notes
        };

    /// <summary>
    /// <c>group-aggregation/001</c>'s <c>sales_fact</c> schema: six rows across three regions — the
    /// EXACT dataset (and expected sums) <c>Craticula.MongoDB.Driver.Tests.Datasets.SalesFact</c> uses,
    /// deliberately reused rather than invented fresh: every amount here (<c>30.00</c>, <c>75.25</c>,
    /// <c>350.50</c>) is exactly representable in IEEE-754 double precision (each is an integer or a
    /// multiple of a power-of-two fraction), which is what makes it a fair cross-backend comparison
    /// despite <see cref="OpenSearchValueReader.ReadMetric"/>'s own double-precision caveat — these
    /// particular values have no rounding to lose in the first place. AMER carries a null <c>amount</c>
    /// on one of its three rows (id <c>s6</c>) — this backend's own <c>sum</c> metric ignores a
    /// missing/null value rather than treating it as zero, so AMER's expected sum (<c>30.00</c>, from
    /// <c>10.00 + 20.00</c>) is only correct if that skip actually happens.
    /// </summary>
    public static IReadOnlyList<JsonObject> SalesFact() =>
    [
        Sale("s1", "EMEA", 100.00m, 3L),
        Sale("s2", "EMEA", 250.50m, 4L),
        Sale("s3", "APAC", 75.25m, 5L),
        Sale("s4", "AMER", 10.00m, 1L),
        Sale("s5", "AMER", 20.00m, 2L),
        Sale("s6", "AMER", null, 7L) // null amount: proves the sum metric skips it rather than failing/zeroing.
    ];

    private static JsonObject Sale(string id, string region, decimal? amount, long quantity) => new()
    {
        ["id"] = id,
        ["region"] = region,
        ["amount"] = amount is null ? null : JsonValue.Create(amount.Value),
        ["quantity"] = quantity
    };

    /// <summary>
    /// A synthetic dataset sized to exercise PERF-2's deep-paging walk (and OSE-11(d)'s grouped
    /// after-key splice) within a fast test, against a deliberately LOWERED
    /// <see cref="OpenSearchDeepPagingOptions.MaxResultWindow"/> test override — a few hundred real
    /// documents rather than the engine's own 10,000-row default, per this story's own test-strategy
    /// note. <paramref name="count"/> rows, id <c>1..count</c>, alternating between two <c>region</c>
    /// values so a grouped-level test over this same dataset has exactly two buckets to page through.
    /// </summary>
    public static IReadOnlyList<JsonObject> DeepPagingRows(int count)
    {
        var rows = new List<JsonObject>(count);
        for (var id = 1L; id <= count; id++)
        {
            rows.Add(new JsonObject
            {
                ["id"] = id,
                ["region"] = id % 2 == 0 ? "EMEA" : "APAC",
                ["amount"] = JsonValue.Create(id * 1.00m)
            });
        }

        return rows;
    }
}
