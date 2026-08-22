using Craticula.Core.Export;
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.Sql;

namespace Craticula.Dapper.Tests;

/// <summary>
/// EXP-3's two obligations on an execution adapter.
///
/// <list type="number">
/// <item><description><b>No offset walking.</b> Asserted structurally: the export path's own source
/// never assigns <see cref="RangePage.StartRow"/> anything but the literal <c>0</c> — the SQL-text
/// equivalent of <c>Craticula.EntityFrameworkCore.Tests</c>' "no <c>Skip</c> token" guard, adapted for a
/// compiler that has no <c>Skip</c> to name.</description></item>
/// <item><description><b>Keyset chunking that returns the same rows as a single pass.</b> Run at chunk
/// sizes from 1 upward over the hardest ordering in the fixture suite: three sort keys, mixed-case
/// collation, three different null placements, and pairs of rows that tie on every key but the
/// tiebreaker.</description></item>
/// <item><description><b>Native CLR values reach the row stream</b>, proven the same way
/// <see cref="DapperGridQueryTests"/> proves it for the grid path.</description></item>
/// </list>
/// </summary>
public class DapperExportStreamTests
{
    private static readonly ISqlDialect Sqlite = SqliteDialect.Instance;

    /// <summary>sort-paging/007's request with EXP-2's <see cref="NonePage"/> substituted for its row
    /// range — that fixture is a paged grid request, and what is wanted here is its ORDERING under an
    /// export's unpaginated scope.</summary>
    private static FixtureRequest HardestOrdering()
    {
        var fixture = FixtureRequest.Load("sort-paging", FixtureRequest.ResolveDirectory("sort-paging", "007"));
        return fixture with { Sql = fixture.Sql with { Page = new NonePage() } };
    }

    // ---- EXP-3: no offset ----------------------------------------------------------------------------

    /// <summary>
    /// The structural half: the export path's own source never assigns <see cref="RangePage.StartRow"/>
    /// anything but the literal <c>0</c>. That is what makes "an export never offset-walks" a property
    /// of the code rather than of a test that can only witness the datasets it was given.
    /// </summary>
    [Fact]
    public void The_Export_Paths_Source_Never_Assigns_StartRow_Anything_But_The_Literal_Zero()
    {
        var path = RepoPaths.AdapterSource("DapperExportStream.cs");
        Assert.True(File.Exists(path), $"Expected the export path's source at '{path}'.");

        var code = string.Join(
            '\n',
            File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        // Every RangePage this file constructs is `new RangePage { StartRow = 0, EndRow = ... }` — a
        // literal, not a variable that could grow. The only occurrence of "StartRow =" in the executable
        // source must be that literal.
        foreach (var line in code.Split('\n'))
        {
            if (line.Contains("StartRow =", StringComparison.Ordinal))
            {
                Assert.Contains("StartRow = 0", line, StringComparison.Ordinal);
            }
        }

        Assert.Contains("StartRow = 0", code, StringComparison.Ordinal);
    }

    // ---- EXP-3: the keyset walk ------------------------------------------------------------------

    /// <summary>
    /// Every chunk size returns the identical row sequence, and it is the hand-computed sequence for
    /// this ordering. Sizes 1..9 around an 8-row dataset cover the arrangements that break a keyset
    /// walk: a boundary at every row (1), boundaries landing inside a tying run (2, 3, 4), and a chunk
    /// that exactly consumes the data (8) versus one larger than it (9).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task Chunked_Streaming_Returns_The_Hand_Computed_Row_Sequence_At_Every_Chunk_Size(int chunkSize)
    {
        var fixture = HardestOrdering();
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.Employees());

        var rows = await Drain(database, fixture, chunkSize);

        // fixture.Sql carries no export projection (sort-paging/007 is a grid fixture, not an export
        // one), so StreamAsync falls back to Schema.FieldOrder — ["id", "department", "age", "hireDate"]
        // — and "id" is column 0.
        Assert.Equal(Datasets.EmployeesOrderedIds.Length, rows.Count);
        Assert.Equal(Datasets.EmployeesOrderedIds, rows.Select(row => Convert.ToInt64(row[0])));
    }

    /// <summary>
    /// SEM-6's tiebreaker is what makes the walk sound, so a schema whose stable key is NOT unique must
    /// fail loudly rather than silently return a short export. <c>chunkSize: 1</c> is the sharpest case:
    /// with two seeded rows it forces the pair into two DIFFERENT chunks, which is exactly the shape a
    /// weaker "adjacent rows in one chunk" check alone would miss.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5000)]
    public async Task A_Non_Unique_Stable_Key_Is_Reported_At_Every_Chunk_Size(int chunkSize)
    {
        var fixture = HardestOrdering();

        IReadOnlyList<IReadOnlyDictionary<string, object?>> duplicated =
        [
            new Dictionary<string, object?> { ["id"] = 1L, ["department"] = "sales", ["age"] = 30L, ["hireDate"] = null },
            new Dictionary<string, object?> { ["id"] = 1L, ["department"] = "sales", ["age"] = 30L, ["hireDate"] = null }
        ];

        using var database = SqliteFixtureDatabase.Create(fixture.Schema, duplicated);

        var exception = await Assert.ThrowsAsync<DapperCompilationException>(
            async () => await Drain(database, fixture, chunkSize));

        Assert.Contains("share the stable key", exception.Message, StringComparison.Ordinal);
        Assert.Contains("silently omits", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Range_Page_Is_Refused_And_Points_At_The_Grid_Entry_Point()
    {
        var fixture = FixtureRequest.Load("sort-paging", FixtureRequest.ResolveDirectory("sort-paging", "007"));
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.Employees());

        var exception = await Assert.ThrowsAsync<DapperCompilationException>(
            async () => await Drain(database, fixture, chunkSize: 5));

        Assert.Contains("NonePage", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FetchPageAsync", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>EXP-1 v1 scoping: a group-structured export is refused rather than silently flattened to
    /// leaf rows.</summary>
    [Fact]
    public async Task A_Grouped_Request_Is_Refused()
    {
        var fixture = HardestOrdering();
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.Employees());

        var grouped = fixture with
        {
            Sql = fixture.Sql with { Group = new GroupSpec { GroupCols = ["department"], GroupKeys = [] } }
        };

        var exception = await Assert.ThrowsAsync<DapperCompilationException>(
            async () => await Drain(database, grouped, chunkSize: 5));

        Assert.Contains("ungrouped", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- #1646's own code review: two hazards on a STRING sort key, both refused upfront ------------

    /// <summary>
    /// SqlQueryBuilder.RenderBlank renders SEM-2's <c>blank</c>/<c>notBlank</c> as
    /// <c>(col IS NULL OR col = '')</c> for a text field on every dialect except Oracle — correct for an
    /// ordinary "is this blank" filter condition, but <see cref="DapperKeysetPredicate"/> reuses those
    /// same operators for an EXACT null test the compiled <c>ORDER BY</c> does not share: a genuinely
    /// empty string in a string sort key would make the boundary re-match a row it already emitted
    /// (hand-traced by this story's own review to an infinite loop under <c>NullsPosition.Last</c>).
    /// <see cref="DapperExportStream"/> probes for the hazard before the walk starts and refuses it
    /// rather than risk that.
    /// </summary>
    [Fact]
    public async Task An_Empty_String_In_A_Text_Sort_Key_Is_Refused_Rather_Than_Risk_A_Hang_Or_Duplicate()
    {
        var schema = GridSchema.Parse("""
            {
              "mode": "strict", "source": "items", "stableKey": "id",
              "fields": {
                "id": { "type": "int64", "filterable": true, "sortable": true },
                "name": { "type": "string", "filterable": true, "sortable": true }
              }
            }
            """);

        var request = new SqlQueryRequest
        {
            Schema = schema,
            Sort = [new SortItem { Field = "name", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }],
            Page = new NonePage()
        };

        IReadOnlyList<IReadOnlyDictionary<string, object?>> dataset =
        [
            new Dictionary<string, object?> { ["id"] = 1L, ["name"] = "" },
            new Dictionary<string, object?> { ["id"] = 2L, ["name"] = "west" }
        ];

        using var database = SqliteFixtureDatabase.Create(schema, dataset);

        var exception = await Assert.ThrowsAsync<DapperCompilationException>(async () =>
        {
            await foreach (var _ in DapperExportStream.StreamAsync(database.Connection, request, Sqlite))
            {
            }
        });

        Assert.Contains("empty string", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A request whose own sort does not already end in the stable key gets one MINTED
    /// (<c>SemanticsResolver.AppendStableKeyTiebreaker</c>). For a STRING stable key with no explicit
    /// <c>"sensitive"</c> declaration, the minted tiebreaker's <c>ORDER BY</c> is forced case-SENSITIVE
    /// (SEM-6) while <see cref="DapperKeysetPredicate"/>'s boundary comparison on that same field
    /// resolves case-INSENSITIVE by SEM-1's default — a collation mismatch the review that found it
    /// measured against 23 of this suite's own fixture schemas, not a hypothetical.
    /// <see cref="DapperExportStream"/> refuses the shape before the walk starts.
    /// </summary>
    [Fact]
    public async Task A_Minted_Tiebreaker_On_An_Insensitive_String_StableKey_Is_Refused()
    {
        var schema = GridSchema.Parse("""
            {
              "mode": "strict", "source": "items", "stableKey": "id",
              "fields": {
                "id": { "type": "string", "filterable": true, "sortable": true },
                "amount": { "type": "int64", "filterable": true, "sortable": true }
              }
            }
            """);

        var request = new SqlQueryRequest
        {
            Schema = schema,
            // Sorts on "amount" only — does NOT already end in "id", so AppendStableKeyTiebreaker mints
            // one, and the schema declares no explicit caseSensitivity on the string "id" field.
            Sort = [new SortItem { Field = "amount", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }],
            Page = new NonePage()
        };

        IReadOnlyList<IReadOnlyDictionary<string, object?>> dataset =
        [
            new Dictionary<string, object?> { ["id"] = "a", ["amount"] = 1L },
            new Dictionary<string, object?> { ["id"] = "b", ["amount"] = 2L }
        ];

        using var database = SqliteFixtureDatabase.Create(schema, dataset);

        var exception = await Assert.ThrowsAsync<DapperCompilationException>(async () =>
        {
            await foreach (var _ in DapperExportStream.StreamAsync(database.Connection, request, Sqlite))
            {
            }
        });

        Assert.Contains("stableKey", exception.Message, StringComparison.Ordinal);
        Assert.Contains("sensitive", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_Non_Positive_Chunk_Size_Is_Refused_Before_The_First_Statement()
    {
        var fixture = HardestOrdering();
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.Employees());

        var exception = await Assert.ThrowsAsync<DapperCompilationException>(
            async () => await Drain(database, fixture, chunkSize: 0));

        Assert.Contains("ChunkSize", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Default_Chunk_Size_Is_Five_Thousand()
    {
        Assert.Equal(5_000, new DapperExportOptions().ChunkSize);
    }

    // ---- EXP-1(b): what a row carries ---------------------------------------------------------------

    /// <summary>
    /// A row carries EXACTLY the export's own columns, even though the keyset walk needs sort-key
    /// columns the export does not project. page-export/008 is the fixture for it: three of seven
    /// columns, and <c>id</c> — the SEM-6 tiebreaker the walk chunks on — is not among them.
    /// </summary>
    [Fact]
    public async Task A_Row_Carries_Only_The_Projected_Columns_Even_Though_The_Keyset_Needs_Others()
    {
        var fixture = FixtureRequest.Load("page-export", FixtureRequest.ResolveDirectory("page-export", "008"));
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.ExportableOrders());

        Assert.Equal(["customerName", "totalAmount", "orderDate"], fixture.Sql.Projection);
        Assert.DoesNotContain(fixture.Schema.StableKey, fixture.Sql.Projection!);

        var rows = new List<ExportRow>();
        await foreach (var row in DapperExportStream.StreamAsync(
            database.Connection, fixture.Sql, Sqlite, new DapperExportOptions { ChunkSize = 2 }))
        {
            Assert.Equal(3, row.Count);
            rows.Add(row);
        }

        Assert.Equal(5, rows.Count);
    }

    /// <summary>
    /// #1622/#1653's native-CLR-values obligation, from the export path: a <c>decimal</c> column keeps
    /// its scale and a <c>date</c> column materializes as a <see cref="DateOnly"/>, chunked at a size
    /// smaller than the dataset so the boundary walk is exercised on the way there.
    /// </summary>
    [Fact]
    public async Task Cells_Materialize_As_The_Schemas_Declared_Clr_Type()
    {
        var fixture = FixtureRequest.Load("page-export", FixtureRequest.ResolveDirectory("page-export", "008"));
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.ExportableOrders());

        var rows = new List<ExportRow>();
        await foreach (var row in DapperExportStream.StreamAsync(
            database.Connection, fixture.Sql, Sqlite, new DapperExportOptions { ChunkSize = 2 }))
        {
            rows.Add(row);
        }

        // customerName, totalAmount, orderDate — page-export/008's own projection order.
        Assert.IsType<decimal>(rows[0][1]);
        Assert.Equal(19.99m, rows[0][1]);
        Assert.IsType<DateOnly>(rows[0][2]);
        Assert.Equal(new DateOnly(2024, 1, 1), rows[0][2]);
    }

    // ---- helper --------------------------------------------------------------------------------------

    private static async Task<List<IReadOnlyList<object?>>> Drain(
        SqliteFixtureDatabase database, FixtureRequest fixture, int chunkSize)
    {
        var rows = new List<IReadOnlyList<object?>>();

        await foreach (var row in DapperExportStream.StreamAsync(
            database.Connection, fixture.Sql, Sqlite, new DapperExportOptions { ChunkSize = chunkSize }))
        {
            rows.Add([.. Enumerable.Range(0, row.Count).Select(index => row[index])]);
        }

        return rows;
    }
}
