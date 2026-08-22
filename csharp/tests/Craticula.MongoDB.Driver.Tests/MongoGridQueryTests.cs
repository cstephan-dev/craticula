using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.Mongo;

namespace Craticula.MongoDB.Driver.Tests;

/// <summary>
/// The grid entry point's own contract, proven against a REAL running MongoDB instance
/// (<c>CRATICULA_MONGO_TEST_URI</c>, default <c>mongodb://localhost:27017</c> — see
/// <see cref="MongoFixtureDatabase"/>'s own remarks for how to start it): a real document set seeded,
/// the real <see cref="MongoPipelineBuilder.Build"/> compiling a real fixture's request, executed via
/// <see cref="MongoGridQuery"/>, compared against an INDEPENDENTLY HAND-COMPUTED expected sequence — the
/// same two-sided-oracle discipline <c>Craticula.Dapper.Tests</c> applies, minus the second compiler
/// neither backend has here (see <see cref="MongoFixtureDatabase"/>'s own remarks).
/// </summary>
public sealed class MongoGridQueryTests
{
    private static FixtureRequest ExportableOrdersFixture() =>
        FixtureRequest.Load("page-export", FixtureRequest.ResolveDirectory("page-export", "008"));

    private static FixtureRequest SalesFactFixture() =>
        FixtureRequest.Load("group-aggregation", FixtureRequest.ResolveDirectory("group-aggregation", "001"));

    // ---- ordinary (no group, no projection): WithExplicitProjection + SC-4 physical-path remapping ----

    /// <summary>
    /// EXP-1(b): a grid page with no explicit projection returns every declared field in schema
    /// declaration order, under its LOGICAL colId — not whatever raw document shape a bare, unprojected
    /// pipeline would hand back. This fixture's schema maps <c>id</c> onto the document's own <c>_id</c>
    /// key AND <c>customerName</c> onto a NESTED <c>customer.name</c> path, so both remaps are proven at
    /// once against the real server.
    /// </summary>
    [Fact]
    public async Task An_Ordinary_Fetch_Forces_An_Explicit_Projection_And_Remaps_Physical_Paths()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await MongoFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = new MongoQueryRequest
        {
            Schema = database.Schema,
            Filter = null,
            Sort = null,
            Page = new RangePage { StartRow = 0, EndRow = 100 },
            Group = null,
            Aggregations = null,
            Projection = null
        };

        var page = await MongoGridQuery.FetchPageAsync(database.Database, request);

        Assert.Equal(database.Schema.FieldOrder, page.Columns);
        Assert.Equal(["id", "orderDate", "customerName", "totalAmount", "status", "region", "notes"], page.Columns);
        Assert.Equal(5, page.Rows.Count);

        // SEM-6 mints "id" as the sole sort key (sortModel is empty upstream of this hand-built request,
        // and this schema's stableKey is "id") -- ascending, so the five ids come back in order.
        var idIndex = page.Columns.ToList().IndexOf("id");
        Assert.Equal([1L, 2L, 3L, 4L, 5L], page.Rows.Select(row => row[idIndex]));

        var nameIndex = page.Columns.ToList().IndexOf("customerName");
        Assert.Equal("Acme", page.Rows[0][nameIndex]);
        Assert.Null(page.Rows[1][nameIndex]);
    }

    /// <summary>
    /// #1622/#1652/#1653's native-CLR-values obligation, checked from the grid path: a
    /// <see cref="MongoDB.Bson.BsonDecimal128"/> cell materializes as a <see cref="decimal"/> keeping its
    /// scale, and a plain ISO date string materializes as a <see cref="DateOnly"/> — against the real
    /// driver, not a hand-written oracle.
    /// </summary>
    [Fact]
    public async Task Cells_Materialize_As_The_Schemas_Declared_Clr_Type()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await MongoFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = new MongoQueryRequest
        {
            Schema = database.Schema,
            Page = new RangePage { StartRow = 0, EndRow = 100 }
        };

        var page = await MongoGridQuery.FetchPageAsync(database.Database, request);

        var totalAmountIndex = page.Columns.ToList().IndexOf("totalAmount");
        var orderDateIndex = page.Columns.ToList().IndexOf("orderDate");

        var firstRow = page.Rows[0];
        Assert.IsType<decimal>(firstRow[totalAmountIndex]);
        Assert.Equal(19.99m, firstRow[totalAmountIndex]);
        Assert.IsType<DateOnly>(firstRow[orderDateIndex]);
        Assert.Equal(new DateOnly(2024, 1, 1), firstRow[orderDateIndex]);
    }

    /// <summary>EXP-1(b) from the other side: an explicit (export-resolved) projection is honoured in the
    /// order it was asked for, over the grid entry point rather than the export one — a page-scoped
    /// export (<c>exportScope: 'page'</c>) is the same aggregation as a grid block.</summary>
    [Fact]
    public async Task An_Explicit_Projection_Is_Honoured_In_Request_Order()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await MongoFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = fixture.Request with { Schema = database.Schema, Page = new RangePage { StartRow = 0, EndRow = 3 } };

        Assert.Equal(["customerName", "totalAmount", "orderDate"], request.Projection);

        var page = await MongoGridQuery.FetchPageAsync(database.Database, request);

        Assert.Equal(["customerName", "totalAmount", "orderDate"], page.Columns);
        Assert.Equal(3, page.Rows.Count);
        Assert.All(page.Rows, row => Assert.Equal(3, row.Count));
    }

    // ---- row count: filtered total, not page size, and null when not requested ----------------------

    /// <summary>
    /// AG Grid SSRM's <c>rowCount</c> is "how many documents match the filter", NOT "how many are on this
    /// page" — the distinction is only visible on a request whose page is smaller than its result.
    /// </summary>
    [Fact]
    public async Task The_Row_Count_Is_The_Filtered_Total_And_Not_The_Page_Size()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await MongoFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = new MongoQueryRequest
        {
            Schema = database.Schema,
            Page = new RangePage { StartRow = 0, EndRow = 3 }
        };

        var page = await MongoGridQuery.FetchPageAsync(database.Database, request, includeRowCount: true);

        Assert.Equal(3, page.Rows.Count);
        Assert.Equal(5, page.RowCount);
    }

    /// <summary><see langword="null"/> and <c>0</c> are different answers: AG Grid treats an unknown
    /// total as "keep asking for blocks" and a zero as "there is nothing". A caller that did not ask for
    /// the count gets <see langword="null"/>.</summary>
    [Fact]
    public async Task An_Unrequested_Row_Count_Is_Null()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await MongoFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = new MongoQueryRequest
        {
            Schema = database.Schema,
            Page = new RangePage { StartRow = 0, EndRow = 3 }
        };

        var page = await MongoGridQuery.FetchPageAsync(database.Database, request);

        Assert.Null(page.RowCount);
    }

    /// <summary>The separate count entry point answers the same question as the flag, so a host fetching
    /// the total on a different cadence from its blocks cannot be asking something subtly
    /// different — and it must survive MongoDB's own <c>$count</c> quirk (no output document at all when
    /// nothing matches, exercised separately below).</summary>
    [Fact]
    public async Task The_Standalone_Count_Agrees_With_The_Pages_Own()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await MongoFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = new MongoQueryRequest
        {
            Schema = database.Schema,
            Page = new RangePage { StartRow = 0, EndRow = 3 }
        };

        var page = await MongoGridQuery.FetchPageAsync(database.Database, request, includeRowCount: true);
        var standalone = await MongoGridQuery.CountAsync(database.Database, request);

        Assert.Equal(page.RowCount, standalone);
    }

    /// <summary>MongoDB's <c>$count</c> stage emits NO output document when zero documents reach it —
    /// unlike SQL's <c>COUNT(*)</c>, which always returns exactly one row. An absent result must read as
    /// <c>0</c>, not throw or hang.</summary>
    [Fact]
    public async Task A_Count_Over_Zero_Matching_Documents_Is_Zero_Not_An_Absent_Result()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await MongoFixtureDatabase.CreateAsync(fixture.Schema, []);

        var request = new MongoQueryRequest
        {
            Schema = database.Schema,
            Page = new RangePage { StartRow = 0, EndRow = 100 }
        };

        var count = await MongoGridQuery.CountAsync(database.Database, request);

        Assert.Equal(0L, count);
    }

    // ---- grouping: _id -> group column rename, real $group/$sum/$avg over Decimal128/Int64 -----------

    /// <summary>
    /// A real grouped level, against the real server: <c>Craticula.Mongo</c>'s own <c>$group</c> stage
    /// always keys its bucket under the literal <c>_id</c>; this package renames it back to the group
    /// column's own colId (<see cref="MongoGridPage"/>'s own remarks) before the grid ever sees it. Sum
    /// over a real <see cref="MongoDB.Bson.BsonDecimal128"/> column, including a bucket with a null
    /// value the server must skip rather than zero the whole bucket.
    /// </summary>
    [Fact]
    public async Task A_Grouped_Level_Renames_The_Bucket_Column_And_Sums_A_Decimal_Field()
    {
        var fixture = SalesFactFixture();
        await using var database = await MongoFixtureDatabase.CreateAsync(fixture.Schema, Datasets.SalesFact());

        var request = fixture.Request with { Schema = database.Schema };

        var page = await MongoGridQuery.FetchPageAsync(database.Database, request);

        Assert.Equal(["region", "amount_sum"], page.Columns);
        Assert.Equal(3, page.Rows.Count);

        var byRegion = page.Rows.ToDictionary(row => (string)row[0]!, row => (decimal)row[1]!);
        Assert.Equal(30.00m, byRegion["AMER"]);
        Assert.Equal(75.25m, byRegion["APAC"]);
        Assert.Equal(350.50m, byRegion["EMEA"]);

        // No explicit sort in this fixture, so SEM-6 mints the group column itself as the tiebreaker
        // (GRP-4) -- ascending, hence AMER, APAC, EMEA in that order.
        Assert.Equal(["AMER", "APAC", "EMEA"], page.Rows.Select(row => row[0]));
    }

    /// <summary>
    /// AGG-1's non-obvious member, against a real integer column: <c>avg</c> does NOT keep the source
    /// field's <see cref="FieldType.Int64"/> the way <c>sum</c>/<c>min</c>/<c>max</c> genuinely do —
    /// MongoDB's own <c>$avg</c> always returns a fractional value. EMEA's two quantities (3, 4) average
    /// to a genuinely fractional 3.5, which only reads back correctly as a real <see cref="double"/>.
    /// </summary>
    [Fact]
    public async Task An_Average_Over_An_Integer_Column_Widens_To_Double()
    {
        var fixture = SalesFactFixture();
        await using var database = await MongoFixtureDatabase.CreateAsync(fixture.Schema, Datasets.SalesFact());

        var request = new MongoQueryRequest
        {
            Schema = database.Schema,
            Page = new RangePage { StartRow = 0, EndRow = 100 },
            Group = new GroupSpec { GroupCols = ["region"], GroupKeys = [] },
            Aggregations = [new Aggregation { Field = "quantity", Func = AggregationFunc.Avg }]
        };

        var page = await MongoGridQuery.FetchPageAsync(database.Database, request);

        Assert.Equal(["region", "quantity_avg"], page.Columns);

        var emeaIndex = page.Rows.ToList().FindIndex(row => (string)row[0]! == "EMEA");
        Assert.True(emeaIndex >= 0, "Expected an EMEA bucket.");

        var value = Assert.IsType<double>(page.Rows[emeaIndex][1]);
        Assert.Equal(3.5, value);
    }

    /// <summary>For a grouped level, <c>RowCount</c> is the BUCKET count, not the leaf-document count —
    /// proven on a page window smaller than the number of buckets, exactly like the ordinary case
    /// above.</summary>
    [Fact]
    public async Task RowCount_For_A_Grouped_Level_Is_The_Bucket_Count()
    {
        var fixture = SalesFactFixture();
        await using var database = await MongoFixtureDatabase.CreateAsync(fixture.Schema, Datasets.SalesFact());

        var request = fixture.Request with { Schema = database.Schema, Page = new RangePage { StartRow = 0, EndRow = 2 } };

        var page = await MongoGridQuery.FetchPageAsync(database.Database, request, includeRowCount: true);

        Assert.Equal(2, page.Rows.Count);
        Assert.Equal(3, page.RowCount); // AMER, APAC, EMEA -- three buckets, not six seeded documents
    }
}
