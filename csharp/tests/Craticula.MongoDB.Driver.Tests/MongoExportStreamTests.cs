using Craticula.Core.Export;
using Craticula.Core.Ir;
using Craticula.Mongo;

namespace Craticula.MongoDB.Driver.Tests;

/// <summary>
/// EXP-3's obligations on this adapter, proven against a REAL running MongoDB instance (see
/// <see cref="MongoFixtureDatabase"/>'s own remarks).
///
/// <list type="number">
/// <item><description><b>No row window, ever — structurally, not just on the datasets this suite
/// happens to try.</b> Asserted by scanning the export path's own source for the one construct that
/// would introduce one.</description></item>
/// <item><description><b>Native CLR values reach the row stream</b>, proven the same way
/// <see cref="MongoGridQueryTests"/> proves it for the grid path.</description></item>
/// <item><description><b>The two structural refusals</b> (a <see cref="RangePage"/>, a
/// <see cref="GroupSpec"/>) that keep the grid/export split a property of the code, mirroring
/// <c>Craticula.Dapper.Tests.DapperExportStreamTests</c>' own equivalents.</description></item>
/// </list>
/// </summary>
public sealed class MongoExportStreamTests
{
    private static FixtureRequest ExportableOrdersFixture() =>
        FixtureRequest.Load("page-export", FixtureRequest.ResolveDirectory("page-export", "008"));

    // ---- EXP-3: no row window, structurally -----------------------------------------------------

    /// <summary>
    /// The structural half: the export path's own source never constructs a <see cref="RangePage"/> at
    /// all — unlike <c>Craticula.Dapper.DapperExportStream</c> (which must build one per keyset chunk,
    /// pinned to <c>StartRow = 0</c>), this adapter has NO keyset machinery and so nothing in it ever
    /// needs to build a row window in the first place. That is what makes "an export never applies a row
    /// window" a property of the code rather than of a test that can only witness the datasets it was
    /// given.
    /// </summary>
    [Fact]
    public void The_Export_Path_Never_Constructs_A_RangePage()
    {
        var path = RepoPaths.AdapterSource("MongoExportStream.cs");
        Assert.True(File.Exists(path), $"Expected the export path's source at '{path}'.");

        var source = File.ReadAllText(path);

        Assert.DoesNotContain("new RangePage", source, StringComparison.Ordinal);
    }

    // ---- EXP-1(b)/EXP-3: streaming end to end -----------------------------------------------------

    /// <summary>
    /// The fixture's own naturally-resolved export request (<c>exportScope: "all"</c> -&gt;
    /// <see cref="NonePage"/>, no group, the resolved <c>customerName</c>/<c>totalAmount</c>/<c>orderDate</c>
    /// projection), streamed against the real seeded collection. No explicit sort in the fixture, so
    /// SEM-6 mints the stableKey (<c>id</c>, mapped onto the document's own <c>_id</c>) as the sole
    /// ordering key — the five rows come back in id order, deterministically.
    /// </summary>
    [Fact]
    public async Task Streams_Every_Row_In_The_Exports_Own_Column_Order()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await MongoFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = fixture.Request with { Schema = database.Schema };
        Assert.IsType<NonePage>(request.Page);
        Assert.Equal(["customerName", "totalAmount", "orderDate"], request.Projection);

        var rows = new List<ExportRow>();
        await foreach (var row in MongoExportStream.StreamAsync(database.Database, request))
        {
            Assert.Equal(3, row.Count);
            rows.Add(row);
        }

        Assert.Equal(5, rows.Count);

        Assert.Equal("Acme", rows[0][0]);
        Assert.Equal(19.99m, rows[0][1]);
        Assert.Equal(new DateOnly(2024, 1, 1), rows[0][2]);

        Assert.Null(rows[1][0]); // customerName null for id 2
        Assert.Equal(100.55m, rows[1][1]);
        Assert.Null(rows[1][2]); // orderDate null for id 2

        Assert.Equal("Beta, Inc", rows[2][0]);
        Assert.Null(rows[2][1]); // totalAmount null for id 3
    }

    /// <summary>
    /// #1622/#1652/#1653's native-CLR-values obligation, from the export path: a real
    /// <see cref="MongoDB.Bson.BsonDecimal128"/> keeps its scale and a plain ISO date string
    /// materializes as a <see cref="DateOnly"/>.
    /// </summary>
    [Fact]
    public async Task Cells_Materialize_As_The_Schemas_Declared_Clr_Type()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await MongoFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = fixture.Request with { Schema = database.Schema };

        var rows = new List<ExportRow>();
        await foreach (var row in MongoExportStream.StreamAsync(database.Database, request))
        {
            rows.Add(row);
        }

        // customerName, totalAmount, orderDate -- page-export/008's own projection order.
        Assert.IsType<decimal>(rows[0][1]);
        Assert.Equal(19.99m, rows[0][1]);
        Assert.IsType<DateOnly>(rows[0][2]);
        Assert.Equal(new DateOnly(2024, 1, 1), rows[0][2]);
    }

    // ---- structural refusals ------------------------------------------------------------------------

    [Fact]
    public async Task A_Range_Page_Is_Refused_And_Points_At_The_Grid_Entry_Point()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await MongoFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = fixture.Request with { Schema = database.Schema, Page = new RangePage { StartRow = 0, EndRow = 5 } };

        var exception = await Assert.ThrowsAsync<MongoCompilationException>(async () =>
        {
            await foreach (var _ in MongoExportStream.StreamAsync(database.Database, request))
            {
            }
        });

        Assert.Contains("NonePage", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FetchPageAsync", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>EXP-1 v1 scoping: a group-structured export is refused rather than silently flattened to
    /// leaf documents.</summary>
    [Fact]
    public async Task A_Grouped_Request_Is_Refused()
    {
        var fixture = FixtureRequest.Load("group-aggregation", FixtureRequest.ResolveDirectory("group-aggregation", "001"));
        await using var database = await MongoFixtureDatabase.CreateAsync(fixture.Schema, Datasets.SalesFact());

        var request = new MongoQueryRequest
        {
            Schema = database.Schema,
            Page = new NonePage(),
            Group = new GroupSpec { GroupCols = ["region"], GroupKeys = [] }
        };

        var exception = await Assert.ThrowsAsync<MongoCompilationException>(async () =>
        {
            await foreach (var _ in MongoExportStream.StreamAsync(database.Database, request))
            {
            }
        });

        Assert.Contains("ungrouped", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_Empty_Export_Projection_Is_Refused()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await MongoFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = new MongoQueryRequest
        {
            Schema = database.Schema,
            Page = new NonePage(),
            Projection = []
        };

        var exception = await Assert.ThrowsAsync<MongoCompilationException>(async () =>
        {
            await foreach (var _ in MongoExportStream.StreamAsync(database.Database, request))
            {
            }
        });

        Assert.Contains("empty", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
