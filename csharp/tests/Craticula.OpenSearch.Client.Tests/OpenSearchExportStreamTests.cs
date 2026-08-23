using System.Text.Json.Nodes;
using Craticula.Core.Export;
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.Search;
using OpenSearch.Client;
using OpenSearch.Net;

namespace Craticula.OpenSearch.Client.Tests;

/// <summary>
/// EXP-3's obligations on this adapter, proven against a REAL running OpenSearch instance (see
/// <see cref="OpenSearchFixtureDatabase"/>'s own remarks).
/// </summary>
public sealed class OpenSearchExportStreamTests
{
    private static FixtureRequest ExportableOrdersFixture() =>
        FixtureRequest.Load("page-export", FixtureRequest.ResolveDirectory("page-export", "008"));

    private static IOpenSearchLowLevelClient LowLevelClient() =>
        new OpenSearchClient(new ConnectionSettings(new Uri(
            Environment.GetEnvironmentVariable("CRATICULA_OPENSEARCH_TEST_URI") ?? "http://localhost:9200"))).LowLevel;

    // ---- EXP-3: no row OFFSET, structurally -----------------------------------------------------

    /// <summary>
    /// The structural half — and, unlike <c>Craticula.MongoDB.Driver.Tests.MongoExportStreamTests</c>'
    /// own "never constructs a RangePage" guard, this backend's export DOES construct one: every PIT
    /// batch needs an explicit bounded <c>size</c>, which only a <see cref="RangePage"/> can express
    /// through <c>Craticula.Search.SearchQueryBuilder.Build</c> (<see cref="NonePage"/> compiles NO
    /// <c>size</c> member at all). What EXP-3 actually forbids is an OFFSET, not the page type itself —
    /// mirroring <c>Craticula.Dapper.DapperExportStream</c>'s own precedent (which also constructs a
    /// <see cref="RangePage"/> per chunk, always pinned to <see cref="RangePage.StartRow"/> literal
    /// <c>0</c>): only the <c>search_after</c>/<c>pit</c> boundary advances between batches, never a
    /// paging clause's own skip count. This scan is what makes that a property of the code rather than
    /// a note in a comment.
    /// </summary>
    [Fact]
    public void The_Export_Path_Never_Constructs_A_Nonzero_Row_Offset()
    {
        var path = Path.Combine(RepoPaths.Root, "csharp", "src", "Craticula.OpenSearch.Client", "OpenSearchExportStream.cs");
        Assert.True(File.Exists(path), $"Expected the export path's source at '{path}'.");

        var source = File.ReadAllText(path);
        Assert.Contains("Page = new RangePage { StartRow = 0, EndRow = chunking.ChunkSize }", source, StringComparison.Ordinal);

        // `from` is stripped from the compiled body entirely before a PIT/search_after batch is sent —
        // stronger than merely pinning StartRow to 0, since no offset member reaches the server at all.
        Assert.Contains("body.Remove(\"from\")", source, StringComparison.Ordinal);
    }

    // ---- streaming end to end, including the chunk loop's own termination -----------------------------

    [Fact]
    public async Task Streams_Every_Row_In_Schema_Declaration_Order()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = fixture.Request with { Schema = database.Schema };
        Assert.IsType<NonePage>(request.Page);

        var rows = new List<ExportRow>();
        await foreach (var row in OpenSearchExportStream.StreamAsync(LowLevelClient(), request))
        {
            Assert.Equal(database.Schema.FieldOrder.Count, row.Count);
            rows.Add(row);
        }

        Assert.Equal(5, rows.Count);

        var idIndex = database.Schema.FieldOrder.ToList().IndexOf("id");
        Assert.Equal([1L, 2L, 3L, 4L, 5L], rows.Select(row => row[idIndex]));

        var nameIndex = database.Schema.FieldOrder.ToList().IndexOf("customerName");
        Assert.Equal("Acme", rows[0][nameIndex]);
        Assert.Null(rows[1][nameIndex]);
    }

    [Fact]
    public async Task Cells_Materialize_As_The_Schemas_Declared_Clr_Type()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = fixture.Request with { Schema = database.Schema };

        var rows = new List<ExportRow>();
        await foreach (var row in OpenSearchExportStream.StreamAsync(LowLevelClient(), request))
        {
            rows.Add(row);
        }

        var totalAmountIndex = database.Schema.FieldOrder.ToList().IndexOf("totalAmount");
        var orderDateIndex = database.Schema.FieldOrder.ToList().IndexOf("orderDate");

        Assert.IsType<decimal>(rows[0][totalAmountIndex]);
        Assert.Equal(19.99m, rows[0][totalAmountIndex]);
        Assert.IsType<DateOnly>(rows[0][orderDateIndex]);
        Assert.Equal(new DateOnly(2024, 1, 1), rows[0][orderDateIndex]);
    }

    /// <summary>The chunk loop's own termination on a SHORT page: a chunk size larger than the whole
    /// dataset means the very first batch is short, and the loop must stop after it rather than issuing
    /// a second, empty round trip.</summary>
    [Fact]
    public async Task The_Chunk_Loop_Terminates_On_The_First_Short_Batch()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = fixture.Request with { Schema = database.Schema };
        var options = new OpenSearchExportOptions { ChunkSize = 1_000 }; // far larger than the 5-row dataset.

        var rows = new List<ExportRow>();
        await foreach (var row in OpenSearchExportStream.StreamAsync(LowLevelClient(), request, options))
        {
            rows.Add(row);
        }

        Assert.Equal(5, rows.Count);
    }

    /// <summary>The chunk loop across MULTIPLE batches: a chunk size smaller than the dataset forces at
    /// least one full batch plus a short one, proving <c>search_after</c> actually advances between
    /// batches rather than re-reading the same page.</summary>
    [Fact]
    public async Task The_Chunk_Loop_Advances_Search_After_Across_Multiple_Batches()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = fixture.Request with { Schema = database.Schema };
        var options = new OpenSearchExportOptions { ChunkSize = 2 }; // 5 rows -> chunks of 2, 2, 1.

        var rows = new List<ExportRow>();
        await foreach (var row in OpenSearchExportStream.StreamAsync(LowLevelClient(), request, options))
        {
            rows.Add(row);
        }

        var idIndex = database.Schema.FieldOrder.ToList().IndexOf("id");
        Assert.Equal([1L, 2L, 3L, 4L, 5L], rows.Select(row => row[idIndex]));
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

    /// <summary>The export path's own equivalent of <c>OpenSearchGridQueryTests</c>' identical case:
    /// <c>FetchChunkAsync</c> used to read <c>_source</c> by colId directly, ignoring a declared
    /// <c>physical.search.field</c> remap.</summary>
    [Fact]
    public async Task A_Remapped_Physical_Search_Field_Is_Read_By_Its_Physical_Name_Not_The_ColId()
    {
        var indexName = $"remapped_field_export_{Guid.NewGuid():N}";
        var schema = RemappedFieldSchema(indexName);
        var documents = new List<JsonObject> { new() { ["id"] = 1L, ["amt_actual"] = JsonValue.Create(42.50m) } };
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(schema, documents);

        var request = new SearchQueryRequest { Schema = database.Schema, Page = new NonePage() };

        var rows = new List<ExportRow>();
        await foreach (var row in OpenSearchExportStream.StreamAsync(LowLevelClient(), request))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        var amountIndex = database.Schema.FieldOrder.ToList().IndexOf("amount");
        Assert.NotNull(rows[0][amountIndex]);
        Assert.Equal(42.50m, rows[0][amountIndex]);
    }

    // ---- structural refusals ------------------------------------------------------------------------

    [Fact]
    public async Task A_Range_Page_Is_Refused_And_Points_At_The_Grid_Entry_Point()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = fixture.Request with { Schema = database.Schema, Page = new RangePage { StartRow = 0, EndRow = 5 } };

        var exception = await Assert.ThrowsAsync<OpenSearchClientCompilationException>(async () =>
        {
            await foreach (var _ in OpenSearchExportStream.StreamAsync(LowLevelClient(), request))
            {
            }
        });

        Assert.Contains("NonePage", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FetchPageAsync", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Grouped_Request_Is_Refused()
    {
        var fixture = FixtureRequest.Load("group-aggregation", FixtureRequest.ResolveDirectory("group-aggregation", "001"));
        await using var database = await OpenSearchFixtureDatabase.CreateAsync(fixture.Schema, Datasets.SalesFact());

        var request = new SearchQueryRequest
        {
            Schema = database.Schema,
            Page = new NonePage(),
            Group = new GroupSpec { GroupCols = ["region"], GroupKeys = [] }
        };

        var exception = await Assert.ThrowsAsync<OpenSearchClientCompilationException>(async () =>
        {
            await foreach (var _ in OpenSearchExportStream.StreamAsync(LowLevelClient(), request))
            {
            }
        });

        Assert.Contains("ungrouped", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
