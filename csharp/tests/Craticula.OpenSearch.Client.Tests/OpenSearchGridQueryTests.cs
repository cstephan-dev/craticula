using System.Text.Json.Nodes;
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.Search;
using OpenSearch.Client;
using OpenSearch.Net;

namespace Craticula.OpenSearch.Client.Tests;

/// <summary>
/// The grid entry point's own contract, proven against a REAL running OpenSearch instance
/// (<c>CRATICULA_OPENSEARCH_TEST_URI</c>, default <c>http://localhost:9200</c> — see
/// <see cref="OpenSearchFixtureDatabase"/>'s own remarks): a real index seeded, the real
/// <see cref="SearchQueryBuilder.Build"/> compiling a real fixture's request, executed via
/// <see cref="OpenSearchGridQuery"/>, compared against an INDEPENDENTLY HAND-COMPUTED expected sequence
/// — the same two-sided-oracle discipline <c>Craticula.MongoDB.Driver.Tests</c> applies, for the
/// identical reason (no second compiler exists for this backend either).
/// </summary>
public sealed class OpenSearchGridQueryTests
{
    private static FixtureRequest ExportableOrdersFixture() =>
        FixtureRequest.Load("page-export", FixtureRequest.ResolveDirectory("page-export", "008"));

    private static FixtureRequest SalesFactFixture() =>
        FixtureRequest.Load("group-aggregation", FixtureRequest.ResolveDirectory("group-aggregation", "001"));

    private static IOpenSearchLowLevelClient LowLevelClient() =>
        new OpenSearchClient(new ConnectionSettings(new Uri(
            Environment.GetEnvironmentVariable("CRATICULA_OPENSEARCH_TEST_URI") ?? "http://localhost:9200"))).LowLevel;

    // ---- ordinary (ungrouped): schema-order columns, native CLR materialization -----------------------

    [Fact]
    public async Task An_Ordinary_Fetch_Returns_Every_Declared_Field_In_Schema_Order()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = new SearchQueryRequest
        {
            Schema = database.Schema,
            Filter = null,
            Sort = null,
            Page = new RangePage { StartRow = 0, EndRow = 100 },
            Group = null,
            Aggregations = null
        };

        var page = await OpenSearchGridQuery.FetchPageAsync(
            LowLevelClient(), request, "session-ordinary", new InMemorySearchCursorCache());

        Assert.Equal(database.Schema.FieldOrder, page.Columns);
        Assert.Equal(["id", "orderDate", "customerName", "totalAmount", "status", "region", "notes"], page.Columns);
        Assert.Equal(5, page.Rows.Count);
        Assert.True(page.LastRow);

        // SEM-6 mints "id" as the sole sort key (sortModel is empty upstream of this hand-built
        // request) -- ascending, so the five ids come back in order.
        var idIndex = page.Columns.ToList().IndexOf("id");
        Assert.Equal([1L, 2L, 3L, 4L, 5L], page.Rows.Select(row => row[idIndex]));

        var nameIndex = page.Columns.ToList().IndexOf("customerName");
        Assert.Equal("Acme", page.Rows[0][nameIndex]);
        Assert.Null(page.Rows[1][nameIndex]);
    }

    /// <summary>#1622's native-CLR-values obligation, checked from the grid path: a JSON number token
    /// materializes as a <see cref="decimal"/> keeping its scale, and a plain ISO date string
    /// materializes as a <see cref="DateOnly"/> — against the real index, not a hand-written oracle.</summary>
    [Fact]
    public async Task Cells_Materialize_As_The_Schemas_Declared_Clr_Type()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = new SearchQueryRequest { Schema = database.Schema, Page = new RangePage { StartRow = 0, EndRow = 100 } };
        var page = await OpenSearchGridQuery.FetchPageAsync(LowLevelClient(), request, "session-types", new InMemorySearchCursorCache());

        var totalAmountIndex = page.Columns.ToList().IndexOf("totalAmount");
        var orderDateIndex = page.Columns.ToList().IndexOf("orderDate");

        var firstRow = page.Rows[0];
        Assert.IsType<decimal>(firstRow[totalAmountIndex]);
        Assert.Equal(19.99m, firstRow[totalAmountIndex]);
        Assert.IsType<DateOnly>(firstRow[orderDateIndex]);
        Assert.Equal(new DateOnly(2024, 1, 1), firstRow[orderDateIndex]);
    }

    // ---- AG-3's Lazy strategy: fetch one extra, trim, report the end from whether it came back --------

    [Fact]
    public async Task LastRow_Is_False_When_More_Rows_Remain()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = new SearchQueryRequest { Schema = database.Schema, Page = new RangePage { StartRow = 0, EndRow = 3 } };
        var page = await OpenSearchGridQuery.FetchPageAsync(LowLevelClient(), request, "session-lastrow-1", new InMemorySearchCursorCache());

        Assert.Equal(3, page.Rows.Count);
        Assert.False(page.LastRow);
    }

    [Fact]
    public async Task LastRow_Is_True_When_The_Page_Exactly_Exhausts_The_Dataset()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = new SearchQueryRequest { Schema = database.Schema, Page = new RangePage { StartRow = 0, EndRow = 5 } };
        var page = await OpenSearchGridQuery.FetchPageAsync(LowLevelClient(), request, "session-lastrow-2", new InMemorySearchCursorCache());

        Assert.Equal(5, page.Rows.Count);
        Assert.True(page.LastRow);
    }

    // ---- row count: capped track_total_hits, null when not requested ----------------------------------

    [Fact]
    public async Task The_Row_Count_Is_The_Filtered_Total_And_Not_The_Page_Size()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = new SearchQueryRequest { Schema = database.Schema, Page = new RangePage { StartRow = 0, EndRow = 3 } };
        var page = await OpenSearchGridQuery.FetchPageAsync(
            LowLevelClient(), request, "session-count", new InMemorySearchCursorCache(), includeRowCount: true);

        Assert.Equal(3, page.Rows.Count);
        Assert.Equal(5, page.RowCount);
    }

    [Fact]
    public async Task An_Unrequested_Row_Count_Is_Null()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = new SearchQueryRequest { Schema = database.Schema, Page = new RangePage { StartRow = 0, EndRow = 3 } };
        var page = await OpenSearchGridQuery.FetchPageAsync(LowLevelClient(), request, "session-nocount", new InMemorySearchCursorCache());

        Assert.Null(page.RowCount);
    }

    /// <summary>
    /// Review finding: <c>track_total_hits</c> is capped at <see cref="OpenSearchDeepPagingOptions.MaxResultWindow"/>
    /// (PERF-3), which makes the engine clamp <c>hits.total.value</c> to that cap AND set
    /// <c>hits.total.relation</c> to <c>"gte"</c> (not <c>"eq"</c>) once the true count exceeds it. A
    /// caller trusting the capped VALUE as exact would stop paginating right at the boundary PERF-2's own
    /// deep-paging feature exists to let the grid keep going past — this asserts the fix instead returns
    /// <see langword="null"/> (this backend's own documented "unknown total means keep asking" contract,
    /// see <see cref="OpenSearchGridPage.RowCount"/>'s own remarks), never the misleadingly-precise
    /// capped number.
    /// </summary>
    [Fact]
    public async Task RowCount_Is_Null_When_The_Total_Exceeds_The_Capped_Track_Total_Hits()
    {
        var indexName = $"row_count_cap_{Guid.NewGuid():N}";
        var schema = DeepPagingSchema(indexName);
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(schema, Datasets.DeepPagingRows(80));

        var options = new OpenSearchDeepPagingOptions { MaxResultWindow = 50 };
        var request = new SearchQueryRequest { Schema = database.Schema, Page = new RangePage { StartRow = 0, EndRow = 10 } };

        var page = await OpenSearchGridQuery.FetchPageAsync(
            LowLevelClient(), request, "session-count-cap", new InMemorySearchCursorCache(), options, includeRowCount: true);

        Assert.Equal(10, page.Rows.Count);
        Assert.Null(page.RowCount);
    }

    // ---- grouped: composite bucketing, real sum/cardinality over the real server -----------------------

    [Fact]
    public async Task A_Grouped_Level_Sums_A_Decimal_Field_Skipping_Nulls()
    {
        var fixture = SalesFactFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.SalesFact());

        var request = fixture.Request with { Schema = database.Schema };
        var page = await OpenSearchGridQuery.FetchPageAsync(LowLevelClient(), request, "session-group", new InMemorySearchCursorCache());

        Assert.Equal(["region", "amount_sum"], page.Columns);
        Assert.Equal(3, page.Rows.Count);
        Assert.True(page.LastRow);

        var byRegion = page.Rows.ToDictionary(row => (string)row[0]!, row => (decimal)row[1]!);
        Assert.Equal(30.00m, byRegion["AMER"]);
        Assert.Equal(75.25m, byRegion["APAC"]);
        Assert.Equal(350.50m, byRegion["EMEA"]);

        // No explicit sort in this fixture, so SEM-6 mints the group column itself as the tiebreaker --
        // ascending, hence AMER, APAC, EMEA in that order (a composite source orders by its key by
        // construction).
        Assert.Equal(["AMER", "APAC", "EMEA"], page.Rows.Select(row => row[0]));
    }

    [Fact]
    public async Task RowCount_For_A_Grouped_Level_Is_The_Bucket_Count_Via_Cardinality()
    {
        var fixture = SalesFactFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.SalesFact());

        var request = fixture.Request with { Schema = database.Schema, Page = new RangePage { StartRow = 0, EndRow = 2 } };
        var page = await OpenSearchGridQuery.FetchPageAsync(
            LowLevelClient(), request, "session-group-count", new InMemorySearchCursorCache(), includeRowCount: true);

        Assert.Equal(2, page.Rows.Count);
        Assert.Equal(3, page.RowCount); // AMER, APAC, EMEA -- three buckets, well under cardinality's own precision threshold.
    }

    /// <summary>
    /// Review finding: <c>RenderGroupedAggregations</c> sets <c>missing_bucket: true</c> so a null/missing
    /// group value gets its OWN bucket (AR-8/BM-2) rather than being dropped, but <c>cardinality</c> only
    /// counts distinct values among documents that HAVE the field — so whenever a document is missing it
    /// (<c>page-export/008</c>'s own <c>region</c> column has exactly one such row, order id 2), an
    /// unfixed <c>CountBucketsAsync</c> reports N-1 while the composite itself still returns N buckets.
    /// This asserts <see cref="OpenSearchGridPage.RowCount"/> matches the REAL bucket count the engine
    /// returns (<c>EMEA</c>, <c>APAC</c>, and the null bucket = 3), not the cardinality-alone undercount
    /// (2).
    /// </summary>
    [Fact]
    public async Task RowCount_For_A_Grouped_Level_Accounts_For_The_Missing_Bucket()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = new SearchQueryRequest
        {
            Schema = database.Schema,
            Page = new RangePage { StartRow = 0, EndRow = 10 },
            Group = new GroupSpec { GroupCols = ["region"], GroupKeys = [] },
            Aggregations = [new Aggregation { Field = "totalAmount", Func = AggregationFunc.Sum }]
        };

        var page = await OpenSearchGridQuery.FetchPageAsync(
            LowLevelClient(), request, "session-group-missing-count", new InMemorySearchCursorCache(), includeRowCount: true);

        // EMEA, APAC, and the null-region bucket -- the composite's own real bucket count.
        Assert.Equal(3, page.Rows.Count);
        Assert.Equal(3, page.RowCount);
    }

    /// <summary>
    /// OSE-11(d): the compiler refuses ANY non-zero <c>startRow</c> for a grouped level outright — this
    /// is what the synthetic-zero-page + <c>after</c> splice this package's own <c>FetchPageAsync</c>
    /// exists to route around. Proven with a block size of 2 over 3 real buckets, so the second block IS
    /// reached and IS short (proving <see cref="OpenSearchGridPage.LastRow"/> on the grouped path too).
    /// </summary>
    [Fact]
    public async Task A_Grouped_Levels_Second_Block_Continues_From_The_Previous_Blocks_After_Key()
    {
        var fixture = SalesFactFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.SalesFact());

        var client = LowLevelClient();
        var cache = new InMemorySearchCursorCache();

        var request = new SearchQueryRequest
        {
            Schema = database.Schema,
            Page = new RangePage { StartRow = 0, EndRow = 2 },
            Group = new GroupSpec { GroupCols = ["region"], GroupKeys = [] },
            Aggregations = [new Aggregation { Field = "amount", Func = AggregationFunc.Sum }]
        };

        var first = await OpenSearchGridQuery.FetchPageAsync(client, request, "session-splice", cache);
        Assert.Equal(2, first.Rows.Count);
        Assert.False(first.LastRow);
        Assert.Equal(["AMER", "APAC"], first.Rows.Select(row => (string)row[0]!));

        var second = await OpenSearchGridQuery.FetchPageAsync(
            client, request with { Page = new RangePage { StartRow = 2, EndRow = 4 } }, "session-splice", cache);
        Assert.Single(second.Rows);
        Assert.True(second.LastRow);
        Assert.Equal("EMEA", second.Rows[0][0]);
        Assert.Equal(350.50m, second.Rows[0][1]);
    }

    /// <summary>
    /// Review finding: <c>avg</c> over an <see cref="FieldType.Int64"/>-declared source (<c>quantity</c>
    /// here) must keep its fractional part, exactly like every sibling adapter's own identical widening.
    /// Unwidened, <c>ResolveAggregationType</c> would resolve the OUTPUT type to <see cref="FieldType.Int64"/>
    /// too, and <c>OpenSearchValueReader.ReadMetric</c>'s own <see cref="FieldType.Int64"/> arm does a
    /// CHECKED CAST off the engine's own double-precision <c>value</c> — truncating <c>1.5</c> back to
    /// <c>1</c> — even though the engine itself already computed and returned the correct fractional
    /// average. AMER's own three quantities (<c>1, 2, 7</c>) deliberately do not divide evenly, so its
    /// average (<c>10/3</c>) has more than one decimal digit of real precision to lose.
    /// </summary>
    [Fact]
    public async Task An_Avg_Aggregation_Over_An_Int64_Field_Keeps_Its_Fractional_Part()
    {
        var fixture = SalesFactFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.SalesFact());

        var request = new SearchQueryRequest
        {
            Schema = database.Schema,
            Page = new RangePage { StartRow = 0, EndRow = 10 },
            Group = new GroupSpec { GroupCols = ["region"], GroupKeys = [] },
            Aggregations = [new Aggregation { Field = "quantity", Func = AggregationFunc.Avg }]
        };

        var page = await OpenSearchGridQuery.FetchPageAsync(LowLevelClient(), request, "session-avg-int64", new InMemorySearchCursorCache());

        Assert.Equal(["region", "quantity_avg"], page.Columns);

        var byRegion = page.Rows.ToDictionary(row => (string)row[0]!, row => row[1]);

        Assert.IsType<double>(byRegion["EMEA"]);
        Assert.Equal(3.5, (double)byRegion["EMEA"]!, precision: 10); // quantities 3, 4.

        Assert.IsType<double>(byRegion["AMER"]);
        Assert.Equal(10.0 / 3.0, (double)byRegion["AMER"]!, precision: 10); // quantities 1, 2, 7.
    }

    // ---- SC-4: a physical.search.field mapping that differs from the colId -----------------------------

    private static GridSchema RemappedFieldSchema(string index) => GridSchema.Parse($$"""
        {
          "mode": "strict", "source": "{{index}}", "stableKey": "id",
          "fields": {
            "id": { "type": "int64", "filterable": true, "sortable": true, "physical": { "search": { "field": "id" } } },
            "amount": { "type": "decimal", "filterable": true, "sortable": true, "physical": { "search": { "field": "amt_actual" } } }
          }
        }
        """);

    /// <summary>
    /// Review finding: <c>ReadHitBatch</c> used to read <c>_source</c> by colId directly, ignoring a
    /// declared <c>physical.search.field</c> remap — <c>amount</c>'s own <c>_source</c> key here is
    /// <c>amt_actual</c>, not <c>amount</c>, so an unfixed reader would silently return
    /// <see langword="null"/> for every row's own <c>amount</c> cell even though the document genuinely
    /// carries a value.
    /// </summary>
    [Fact]
    public async Task A_Remapped_Physical_Search_Field_Is_Read_By_Its_Physical_Name_Not_The_ColId()
    {
        var indexName = $"remapped_field_{Guid.NewGuid():N}";
        var schema = RemappedFieldSchema(indexName);
        var documents = new List<JsonObject> { new() { ["id"] = 1L, ["amt_actual"] = JsonValue.Create(42.50m) } };
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(schema, documents);

        var request = new SearchQueryRequest { Schema = database.Schema, Page = new RangePage { StartRow = 0, EndRow = 10 } };
        var page = await OpenSearchGridQuery.FetchPageAsync(LowLevelClient(), request, "session-remap", new InMemorySearchCursorCache());

        Assert.Single(page.Rows);
        var amountIndex = page.Columns.ToList().IndexOf("amount");
        Assert.NotNull(page.Rows[0][amountIndex]);
        Assert.Equal(42.50m, page.Rows[0][amountIndex]);
    }

    // ---- structural refusal --------------------------------------------------------------------------

    [Fact]
    public async Task A_None_Page_Is_Refused_And_Points_At_The_Export_Entry_Point()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = new SearchQueryRequest { Schema = database.Schema, Page = new NonePage() };

        var exception = await Assert.ThrowsAsync<OpenSearchClientCompilationException>(() =>
            OpenSearchGridQuery.FetchPageAsync(LowLevelClient(), request, "session-refuse", new InMemorySearchCursorCache()));

        Assert.Contains("RangePage", exception.Message, StringComparison.Ordinal);
        Assert.Contains("OpenSearchExportStream", exception.Message, StringComparison.Ordinal);
    }

    // ---- PERF-2: the deep-paging walk, the offset/MaxResultWindow boundary, and PIT hygiene -----------

    private static GridSchema DeepPagingSchema(string index) => GridSchema.Parse($$"""
        {
          "mode": "strict", "source": "{{index}}", "stableKey": "id",
          "fields": {
            "id": { "type": "int64", "filterable": true, "sortable": true, "physical": { "search": { "field": "id" } } },
            "region": {
              "type": "string", "filterable": true, "sortable": true, "groupable": true,
              "physical": { "search": { "field": "region", "keywordSubField": "region.keyword" } }
            },
            "amount": { "type": "decimal", "filterable": true, "sortable": true, "physical": { "search": { "field": "amount" } } }
          }
        }
        """);

    /// <summary>
    /// The offset-vs-<see cref="OpenSearchDeepPagingOptions.MaxResultWindow"/> threshold, observed
    /// indirectly through the cursor cache: the plain <c>from</c>/<c>size</c> path NEVER touches the
    /// cache (there is no cursor to remember), while crossing the threshold by even one row always
    /// writes an entry — so the cache's own state after a fetch reveals which path this adapter took
    /// without needing a spy on the client itself.
    /// </summary>
    [Fact]
    public async Task A_Page_Exactly_At_The_Window_Stays_Plain_But_One_Row_Beyond_Switches_To_Deep_Paging()
    {
        var indexName = $"deep_paging_boundary_{Guid.NewGuid():N}";
        var schema = DeepPagingSchema(indexName);
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(schema, Datasets.DeepPagingRows(80));

        var options = new OpenSearchDeepPagingOptions { MaxResultWindow = 50 };
        var client = LowLevelClient();

        // pageSize 10, fetchSize 11: startRow(0) + fetchSize(11) <= 50 -- stays under the window.
        var atBoundary = new SearchQueryRequest { Schema = database.Schema, Page = new RangePage { StartRow = 39, EndRow = 49 } };
        var cacheAtBoundary = new InMemorySearchCursorCache();
        var boundaryPage = await OpenSearchGridQuery.FetchPageAsync(client, atBoundary, "session-boundary-plain", cacheAtBoundary, options);
        Assert.Equal(10, boundaryPage.Rows.Count);

        var shapeAtBoundary = SearchQueryBuilder.Build(atBoundary with { Page = new RangePage { StartRow = 0, EndRow = 1 } });
        var keyAtBoundary = OpenSearchGridQuery.ComputeCursorKey("session-boundary-plain", shapeAtBoundary);
        Assert.Null(await cacheAtBoundary.TryGetAsync(keyAtBoundary, CancellationToken.None));

        // pageSize 10, fetchSize 11: startRow(40) + fetchSize(11) = 51 > 50 -- one row past the window.
        var pastBoundary = new SearchQueryRequest { Schema = database.Schema, Page = new RangePage { StartRow = 40, EndRow = 50 } };
        var cachePastBoundary = new InMemorySearchCursorCache();
        var pastPage = await OpenSearchGridQuery.FetchPageAsync(client, pastBoundary, "session-boundary-deep", cachePastBoundary, options);
        Assert.Equal(10, pastPage.Rows.Count);

        var shapePastBoundary = SearchQueryBuilder.Build(pastBoundary with { Page = new RangePage { StartRow = 0, EndRow = 1 } });
        var keyPastBoundary = OpenSearchGridQuery.ComputeCursorKey("session-boundary-deep", shapePastBoundary);
        Assert.NotNull(await cachePastBoundary.TryGetAsync(keyPastBoundary, CancellationToken.None));
    }

    /// <summary>
    /// The deep-paging walk end to end, against a deliberately LOWERED <c>MaxResultWindow</c> (50) over
    /// a 200-row synthetic dataset — proving the PIT/<c>search_after</c> path within a fast test rather
    /// than needing 10,000+ real rows. Three consecutive deep pages, the second and third both O(1)
    /// cache reuses (each page's own <c>startRow</c> matches the previous page's own cached
    /// <c>UpToRow</c> exactly) rather than a fresh walk from zero.
    /// </summary>
    [Fact]
    public async Task The_Deep_Paging_Walk_Returns_Every_Row_Exactly_Once_Across_Sequential_Pages()
    {
        var indexName = $"deep_paging_walk_{Guid.NewGuid():N}";
        var schema = DeepPagingSchema(indexName);
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(schema, Datasets.DeepPagingRows(200));

        var options = new OpenSearchDeepPagingOptions { MaxResultWindow = 50, WalkBatchSize = 1_000 };
        var client = LowLevelClient();
        var cache = new InMemorySearchCursorCache();
        var request = new SearchQueryRequest { Schema = database.Schema, Page = new RangePage { StartRow = 0, EndRow = 0 } };

        var shape = SearchQueryBuilder.Build(request with { Page = new RangePage { StartRow = 0, EndRow = 1 } });
        var cacheKey = OpenSearchGridQuery.ComputeCursorKey("session-walk", shape);

        // Page 1: 0..30 stays under the window (plain path) -- no cache entry yet.
        var page1 = await OpenSearchGridQuery.FetchPageAsync(
            client, request with { Page = new RangePage { StartRow = 0, EndRow = 30 } }, "session-walk", cache, options);
        Assert.Equal(30, page1.Rows.Count);
        Assert.False(page1.LastRow);
        Assert.Null(await cache.TryGetAsync(cacheKey, CancellationToken.None));

        // Page 2: 30..60 crosses the window -- the FIRST deep fetch, cold-walking from row 0.
        var page2 = await OpenSearchGridQuery.FetchPageAsync(
            client, request with { Page = new RangePage { StartRow = 30, EndRow = 60 } }, "session-walk", cache, options);
        Assert.Equal(30, page2.Rows.Count);
        Assert.False(page2.LastRow);

        var afterPage2 = await cache.TryGetAsync(cacheKey, CancellationToken.None);
        Assert.NotNull(afterPage2);
        Assert.Equal(60, afterPage2!.UpToRow);

        // Page 3: 60..90 -- an EXACT cache hit (afterPage2.UpToRow == 60 == this page's own startRow),
        // an O(1) reuse rather than a second cold walk.
        var page3 = await OpenSearchGridQuery.FetchPageAsync(
            client, request with { Page = new RangePage { StartRow = 60, EndRow = 90 } }, "session-walk", cache, options);
        Assert.Equal(30, page3.Rows.Count);
        Assert.False(page3.LastRow);

        var afterPage3 = await cache.TryGetAsync(cacheKey, CancellationToken.None);
        Assert.NotNull(afterPage3);
        Assert.Equal(90, afterPage3!.UpToRow);

        // Every row across the three pages is distinct and in order -- no duplication, no gap.
        var ids = page1.Rows.Concat(page2.Rows).Concat(page3.Rows).Select(row => (long)row[0]!).ToList();
        Assert.Equal(Enumerable.Range(1, 90).Select(i => (long)i), ids);

        // Page 4: a GAP -- jump straight to 150..180, ahead of the cache's own UpToRow(90) by more than
        // one WalkBatchSize-sized step's worth (30) would ordinarily require, but WalkBatchSize(1000)
        // covers it in a single internal step, exercising the "ahead with a gap" branch distinctly from
        // both the exact-match and the cold-start ones.
        var page4 = await OpenSearchGridQuery.FetchPageAsync(
            client, request with { Page = new RangePage { StartRow = 150, EndRow = 180 } }, "session-walk", cache, options);
        Assert.Equal(30, page4.Rows.Count);
        Assert.Equal(Enumerable.Range(151, 30).Select(i => (long)i), page4.Rows.Select(row => (long)row[0]!));
    }

    /// <summary>
    /// PERF-2's resource-hygiene half: when a request lands BEHIND the cache's own cursor (never
    /// expected from AG Grid, but treated as a cold start rather than an error per this package's own
    /// design), the outgoing entry's PIT is best-effort closed. Proven directly against the real
    /// cluster: a real PIT is opened, seeded into the cache under the SAME key the adapter itself would
    /// compute, and after the adapter's own cold-start replaces it, a search against the OLD pit id no
    /// longer succeeds.
    /// </summary>
    [Fact]
    public async Task A_Stale_Cached_Pit_Is_Closed_When_The_Request_Lands_Behind_It()
    {
        var indexName = $"deep_paging_evict_{Guid.NewGuid():N}";
        var schema = DeepPagingSchema(indexName);
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(schema, Datasets.DeepPagingRows(80));

        var options = new OpenSearchDeepPagingOptions { MaxResultWindow = 10 };
        var client = LowLevelClient();
        var cache = new InMemorySearchCursorCache();
        var request = new SearchQueryRequest { Schema = database.Schema, Page = new RangePage { StartRow = 0, EndRow = 0 } };

        var openPitResponse = await client.CreatePitAsync<StringResponse>(
            database.Schema.Source, new CreatePitRequestParameters { KeepAlive = TimeSpan.FromMinutes(5) });
        Assert.True(openPitResponse.HttpStatusCode is >= 200 and < 300, openPitResponse.DebugInformation);

        using var pitDocument = System.Text.Json.JsonDocument.Parse(openPitResponse.Body);
        var stalePitId = pitDocument.RootElement.GetProperty("pit_id").GetString()!;

        var shape = SearchQueryBuilder.Build(request with { Page = new RangePage { StartRow = 0, EndRow = 1 } });
        var cacheKey = OpenSearchGridQuery.ComputeCursorKey("session-evict", shape);

        // Seeded well AHEAD of any request this test makes, forcing the "behind the cache" cold-start
        // branch on the very first call.
        await cache.SetAsync(
            cacheKey,
            new SearchCursorEntry(stalePitId, UpToRow: 70, new JsonArray(70L), DateTimeOffset.UtcNow.AddMinutes(5)),
            CancellationToken.None);

        var page = await OpenSearchGridQuery.FetchPageAsync(
            client, request with { Page = new RangePage { StartRow = 20, EndRow = 30 } }, "session-evict", cache, options);
        Assert.Equal(10, page.Rows.Count);

        // The stale PIT must no longer resolve -- proof the close call actually reached the server,
        // not merely that this package's own code attempted one. Checked via the raw HTTP status
        // rather than StringResponse.Success: OpenSearch.Net's low-level client whitelists a 404 on
        // `_search` (the exact status a closed PIT returns) as a "known" response, so Success alone
        // would read this probe as having succeeded even though the PIT is gone.
        var probeBody = new JsonObject { ["pit"] = new JsonObject { ["id"] = stalePitId, ["keep_alive"] = "1m" }, ["size"] = 1 };
        var probeResponse = await client.SearchAsync<StringResponse>(PostData.String(probeBody.ToJsonString()));
        Assert.False(
            probeResponse.HttpStatusCode is >= 200 and < 300,
            "Expected the stale PIT to have been closed by the adapter's own cold-start eviction.");
    }

    /// <summary>
    /// Verification-review finding: eviction is warranted ONLY when the ENGINE itself rejects the cursor
    /// (a 4xx-range response) — not on every failure. The cached <c>search_after</c> payload is
    /// corrupted with one element too many for this schema's own single-field sort (SEM-6 mints "id" as
    /// the sole sort key here), so the real cluster genuinely returns a 400 ("search_after has N
    /// value(s) but sort has 1") — this is not simulated. Proves both halves of the fix: the poisoned
    /// entry is evicted (so the NEXT request for this session/hash cold-restarts instead of repeating
    /// the identical 400 for the rest of <c>PitKeepAlive</c>'s window) AND its now-orphaned PIT is
    /// closed, mirroring <see cref="A_Stale_Cached_Pit_Is_Closed_When_The_Request_Lands_Behind_It"/>'s
    /// own real-cluster verification style.
    /// </summary>
    [Fact]
    public async Task A_Cursor_Rejected_By_The_Engine_Evicts_The_Cache_Entry_And_Closes_Its_Pit()
    {
        var indexName = $"deep_paging_reject_{Guid.NewGuid():N}";
        var schema = DeepPagingSchema(indexName);
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(schema, Datasets.DeepPagingRows(80));

        var options = new OpenSearchDeepPagingOptions { MaxResultWindow = 10 };
        var client = LowLevelClient();
        var cache = new InMemorySearchCursorCache();
        var request = new SearchQueryRequest { Schema = database.Schema, Page = new RangePage { StartRow = 0, EndRow = 0 } };

        // A normal deep fetch first, seeding a REAL PIT and cursor over this schema's own sort.
        await OpenSearchGridQuery.FetchPageAsync(
            client, request with { Page = new RangePage { StartRow = 0, EndRow = 10 } }, "session-reject", cache, options);

        var shape = SearchQueryBuilder.Build(request with { Page = new RangePage { StartRow = 0, EndRow = 1 } });
        var cacheKey = OpenSearchGridQuery.ComputeCursorKey("session-reject", shape);
        var seeded = await cache.TryGetAsync(cacheKey, CancellationToken.None);
        Assert.NotNull(seeded);

        var corrupted = new SearchCursorEntry(seeded!.PitId, seeded.UpToRow, new JsonArray(1L, 2L, 3L), seeded.ExpiresAt);
        await cache.SetAsync(cacheKey, corrupted, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<OpenSearchClientCompilationException>(() =>
            OpenSearchGridQuery.FetchPageAsync(
                client, request with { Page = new RangePage { StartRow = seeded.UpToRow, EndRow = seeded.UpToRow + 10 } },
                "session-reject", cache, options));

        Assert.Equal(400, exception.HttpStatusCode);
        Assert.Null(await cache.TryGetAsync(cacheKey, CancellationToken.None));

        // The now-orphaned PIT must be closed too, not merely the cache slot cleared -- checked via the
        // raw HTTP status per RequireSuccess's own remarks (Success alone reads a closed PIT's 404 as
        // true).
        var probeBody = new JsonObject { ["pit"] = new JsonObject { ["id"] = seeded.PitId, ["keep_alive"] = "1m" }, ["size"] = 1 };
        var probeResponse = await client.SearchAsync<StringResponse>(PostData.String(probeBody.ToJsonString()));
        Assert.False(
            probeResponse.HttpStatusCode is >= 200 and < 300,
            "Expected the orphaned PIT to have been closed alongside the evicted cache entry.");
    }

    /// <summary>
    /// Finding A's other half: a failure that says NOTHING about the cursor's own validity — here, a
    /// caller-side cancellation mid-flight — must never evict an already-healthy cache entry, nor close
    /// its still-live PIT. Proven against the REAL client: a pre-cancelled token propagates as
    /// <c>OpenSearch.Net</c>'s own transport-failure exception (not
    /// <see cref="OpenSearchClientCompilationException"/>), so <c>IsCursorRejection</c> correctly never
    /// fires and the seeded entry survives untouched.
    /// </summary>
    [Fact]
    public async Task A_Cancelled_Request_Does_Not_Evict_A_Healthy_Cache_Entry()
    {
        var indexName = $"deep_paging_cancel_{Guid.NewGuid():N}";
        var schema = DeepPagingSchema(indexName);
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(schema, Datasets.DeepPagingRows(80));

        var options = new OpenSearchDeepPagingOptions { MaxResultWindow = 10 };
        var client = LowLevelClient();
        var cache = new InMemorySearchCursorCache();
        var request = new SearchQueryRequest { Schema = database.Schema, Page = new RangePage { StartRow = 0, EndRow = 0 } };

        await OpenSearchGridQuery.FetchPageAsync(
            client, request with { Page = new RangePage { StartRow = 0, EndRow = 10 } }, "session-cancel", cache, options);

        var shape = SearchQueryBuilder.Build(request with { Page = new RangePage { StartRow = 0, EndRow = 1 } });
        var cacheKey = OpenSearchGridQuery.ComputeCursorKey("session-cancel", shape);
        var seeded = await cache.TryGetAsync(cacheKey, CancellationToken.None);
        Assert.NotNull(seeded);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Exception? caught = null;
        try
        {
            await OpenSearchGridQuery.FetchPageAsync(
                client, request with { Page = new RangePage { StartRow = seeded!.UpToRow, EndRow = seeded.UpToRow + 10 } },
                "session-cancel", cache, options, cancellationToken: cts.Token);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);

        var afterEntry = await cache.TryGetAsync(cacheKey, CancellationToken.None);
        Assert.NotNull(afterEntry);
        Assert.Equal(seeded!.PitId, afterEntry!.PitId);
        Assert.Equal(seeded.UpToRow, afterEntry.UpToRow);

        // The PIT itself must still be usable too -- not merely the cache slot left alone.
        var probeBody = new JsonObject { ["pit"] = new JsonObject { ["id"] = seeded.PitId, ["keep_alive"] = "1m" }, ["size"] = 1 };
        var probeResponse = await client.SearchAsync<StringResponse>(PostData.String(probeBody.ToJsonString()));
        Assert.True(
            probeResponse.HttpStatusCode is >= 200 and < 300,
            "Expected the healthy PIT to remain open after a cancellation that says nothing about its validity.");
    }
}
