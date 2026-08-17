using System.Text;
using Craticula.Core.Export;
using Craticula.Core.Ir;
using Craticula.Export.Csv;

namespace Craticula.EntityFrameworkCore.Tests;

/// <summary>
/// EXP-3's two obligations on an execution adapter, and the one thing #1651 said it could not check for
/// itself.
///
/// <list type="number">
/// <item><description><b>No OFFSET walking.</b> Asserted on the statements EF actually executed, captured
/// from its own command log — not on a query rebuilt in a test — plus a structural guard that reads the
/// export path's own source for the token <c>Skip</c>.</description></item>
/// <item><description><b>Keyset chunking that returns the same rows as a single pass.</b> Run at chunk
/// sizes from 1 upward over the hardest ordering in the fixture suite: four sort keys, three different
/// null placements, a case-insensitive collation, and pairs of rows that tie on every key but the
/// tiebreaker. A chunk size of 1 means every row is a chunk boundary, so the boundary predicate is
/// exercised as many times as there are rows.</description></item>
/// <item><description><b>The row stream feeds <c>CsvWriter</c> with no adaptation</b>, which is what
/// makes #1653's native-CLR-values obligation real rather than nominal: the bytes are only right if the
/// cells arrived as <see cref="decimal"/>, <see cref="DateOnly"/> and <see cref="string"/> rather than as
/// a driver's own wrappers.</description></item>
/// </list>
/// </summary>
public class EfCoreExportStreamTests
{
    private static readonly EfCoreProviderSemantics Sqlite = EfCoreProviderSemantics.Sqlite;

    /// <summary>sort-paging/007's request with EXP-2's <see cref="NonePage"/> substituted for its row
    /// range — that fixture is a paged grid request, and what is wanted here is its ORDERING (the hardest
    /// in the suite) under an export's unpaginated scope. Nothing else about it is touched.</summary>
    private static FixtureRequest HardestOrdering()
    {
        var fixture = FixtureRequest.Load("sort-paging", FixtureRequest.ResolveDirectory("sort-paging", "007"));
        return fixture with { Ef = fixture.Ef with { Page = new NonePage() } };
    }

    private static readonly long[] HardestOrderingIds = [4, 7, 6, 3, 2, 1, 8, 5];

    // ---- EXP-3: no OFFSET ---------------------------------------------------------------------------

    /// <summary>
    /// The story's headline assertion, made on the statements themselves. Every chunk after the first
    /// carries a keyset predicate and a <c>LIMIT</c>; not one carries an <c>OFFSET</c>, at any chunk size,
    /// including the size that forces a boundary predicate per row.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public async Task No_Statement_The_Export_Path_Executes_Contains_An_Offset(int chunkSize)
    {
        var fixture = HardestOrdering();
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.Employees());

        var rows = await Drain(database, fixture, chunkSize);

        Assert.Equal(HardestOrderingIds.Length, rows.Count);
        Assert.NotEmpty(database.ExecutedCommands);

        foreach (var command in database.ExecutedCommands)
        {
            Assert.DoesNotContain("OFFSET", command, StringComparison.OrdinalIgnoreCase);
        }

        // ...and the walk really did chunk: a size of 1 over 8 rows cannot be one statement. (The walk
        // stops as soon as a chunk comes back without its probe row, so an exactly-full last chunk
        // costs no extra empty round trip.)
        Assert.True(
            database.ExecutedCommands.Count > 1 || chunkSize >= rows.Count,
            $"Chunk size {chunkSize} over {rows.Count} rows executed only one statement, so the keyset " +
            "boundary was never exercised.");
    }

    /// <summary>
    /// The structural half, mirroring <c>Craticula.Sql.Tests.DialectIndependenceTests</c>' forbidden-token
    /// guard: the export path's executable source never names <c>Skip</c>. That is what makes "an export
    /// never OFFSET-walks" a property of the code rather than of the test above, which can only ever
    /// witness the datasets it was given.
    /// </summary>
    [Fact]
    public void The_Export_Paths_Executable_Source_Never_Names_Skip()
    {
        var path = RepoPaths.AdapterSource("EfCoreExportStream.cs");
        Assert.True(File.Exists(path), $"Expected the export path's source at '{path}'.");

        var code = string.Join(
            '\n',
            File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.DoesNotContain("Skip", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyRowWindow", code, StringComparison.Ordinal);
    }

    // ---- EXP-3: the keyset walk --------------------------------------------------------------------

    /// <summary>
    /// Every chunk size returns the identical row sequence, and it is the sequence the GRID path returns
    /// for the same ordering — so chunking is invisible in the output, which is the only property that
    /// makes it a safe substitute for one statement.
    ///
    /// <para>Sizes 1..9 around an 8-row dataset cover the three arrangements that break a keyset walk:
    /// a boundary at every row (1), boundaries that fall inside a run of rows tying on the leading keys
    /// (2, 3, 4), and a chunk that exactly consumes the data (8) versus one larger than it (9) — the pair
    /// that catches a walk terminating on "the chunk was short" versus one that needs an extra empty
    /// round trip.</para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task Chunked_Streaming_Returns_The_Grid_Paths_Row_Sequence_At_Every_Chunk_Size(int chunkSize)
    {
        var fixture = HardestOrdering();
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.Employees());

        var streamed = await Drain(database, fixture, chunkSize);
        var stableKey = fixture.Schema.FieldOrder.ToList().IndexOf(fixture.Schema.StableKey);

        Assert.Equal(HardestOrderingIds, streamed.Select(row => Convert.ToInt64(row[stableKey])));
    }

    /// <summary>
    /// SEM-6's tiebreaker is what makes the walk sound, so a schema whose stable key is NOT unique must
    /// fail loudly.
    ///
    /// <para><b>The failure mode is not the obvious one, and this test has now guarded against the wrong
    /// thing twice.</b> A boundary predicate says "strictly after the last row emitted", so the walk is
    /// monotone — it cannot repeat a row and cannot fail to terminate, which makes a "did the boundary
    /// advance?" guard unreachable code. What a duplicate stable key actually causes is a row
    /// <em>silently skipped</em>: a short export that is perfectly well formed.
    ///
    /// <para><b>Then the fix for that was itself half-blind, and this test hid it by choosing one chunk
    /// size.</b> An adjacent-row check only sees duplicates that land inside the SAME chunk. When the pair
    /// straddles a chunk edge the next chunk's predicate excludes the second row, so it is never fetched
    /// and there is nothing to compare — the review reproduced exactly that at <c>ChunkSize = 1</c>: two
    /// seeded rows, one emitted, no exception. The original test asserted at <c>chunkSize: 2</c> against a
    /// two-row dataset, the one size at which both rows are guaranteed to share a chunk, so it passed
    /// while the behaviour it names was wrong. Hence the theory below: <b>1 is the case that matters</b>,
    /// because with two rows it forces the pair apart.</para>
    ///
    /// <para>The check cannot false-positive, since SC-5 declares that key unique.</para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5000)]
    public async Task A_Non_Unique_Stable_Key_Is_Reported_At_Every_Chunk_Size(int chunkSize)
    {
        var fixture = HardestOrdering();

        // Two rows with the same id, identical on every sort key: no predicate over the ORDER BY tuple
        // can separate them, because the tuple is not a total order on this data.
        IReadOnlyList<IReadOnlyDictionary<string, object?>> duplicated =
        [
            new Dictionary<string, object?> { ["id"] = 1L, ["department"] = "sales", ["age"] = 30L, ["hireDate"] = null },
            new Dictionary<string, object?> { ["id"] = 1L, ["department"] = "sales", ["age"] = 30L, ["hireDate"] = null }
        ];

        using var database = SqliteFixtureDatabase.Create(fixture.Schema, duplicated);

        var exception = await Assert.ThrowsAsync<EfCoreCompilationException>(
            async () => await Drain(database, fixture, chunkSize));

        Assert.Contains("share the stable key", exception.Message, StringComparison.Ordinal);
        Assert.Contains("silently omits", exception.Message, StringComparison.Ordinal);
    }


    /// <summary>EXP-1/EXP-2: a page-scoped export is refused rather than given a <c>Skip</c>, which is
    /// what keeps the no-OFFSET guarantee total instead of true-except-once. The message names the entry
    /// point that serves it, because the query is the same one a grid block runs.</summary>
    [Fact]
    public async Task A_Range_Page_Is_Refused_And_Points_At_The_Grid_Entry_Point()
    {
        var fixture = FixtureRequest.Load("sort-paging", FixtureRequest.ResolveDirectory("sort-paging", "007"));
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.Employees());

        var exception = await Assert.ThrowsAsync<EfCoreCompilationException>(
            async () => await Drain(database, fixture, chunkSize: 5));

        Assert.Contains("NonePage", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FetchPageAsync", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Non_Positive_Chunk_Size_Is_Refused_Before_The_First_Statement()
    {
        var fixture = HardestOrdering();
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.Employees());

        var exception = await Assert.ThrowsAsync<EfCoreCompilationException>(
            async () => await Drain(database, fixture, chunkSize: 0));

        Assert.Contains("ChunkSize", exception.Message, StringComparison.Ordinal);
        Assert.Empty(database.ExecutedCommands);
    }

    [Fact]
    public void The_Default_Chunk_Size_Is_Five_Thousand()
    {
        // EXP-3's documented default, stated here so that changing it is a decision rather than a typo.
        Assert.Equal(5_000, new EfCoreExportOptions().ChunkSize);
    }

    // ---- EXP-1(b): what a row carries -------------------------------------------------------------

    /// <summary>
    /// A row carries EXACTLY the export's own columns, even though the keyset walk needs sort-key columns
    /// the export does not project. <c>page-export/008</c> is the fixture for it: three of seven columns,
    /// and <c>id</c> — the SEM-6 tiebreaker the walk chunks on — is not among them.
    /// </summary>
    [Fact]
    public async Task A_Row_Carries_Only_The_Projected_Columns_Even_Though_The_Keyset_Needs_Others()
    {
        var fixture = FixtureRequest.Load("page-export", FixtureRequest.ResolveDirectory("page-export", "008"));
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.ExportableOrders());

        Assert.Equal(["customerName", "totalAmount", "orderDate"], fixture.Ef.Projection);
        Assert.DoesNotContain(fixture.Schema.StableKey, fixture.Ef.Projection!);

        await foreach (var row in EfCoreExportStream.StreamAsync(
            database.Rows(), fixture.Ef, Sqlite, new EfCoreExportOptions { ChunkSize = 2 }))
        {
            Assert.Equal(3, row.Count);
        }
    }

    /// <summary>
    /// EXP-3 + EXP-4 end to end: the stream this adapter returns is handed to
    /// <c>CsvWriter.WriteAsync</c> with no adaptation, and the bytes are compared in full.
    ///
    /// <para><b>What only this test can catch.</b> Every cell's rendering depends on the CLR type the
    /// adapter produced — <c>19.99</c> keeps its scale because it is a <see cref="decimal"/>,
    /// <c>2024-01-01</c> is ISO because it is a <see cref="DateOnly"/>, and <c>=cmd</c> is prefixed with
    /// <c>'</c> while <c>-5.00</c> is NOT, because EXP-6's scope is decided by the value's type and not by
    /// the schema's. Hand a wrapper type over instead and the file stays well-formed while three of those
    /// four go wrong. That is exactly the obligation <c>CsvWriter</c>'s remarks place on an execution
    /// adapter and explicitly cannot verify from its own side.</para>
    ///
    /// <para>The chunk size is 2 against 5 rows, so the file is assembled from three chunks — a chunk
    /// boundary inside the byte stream must be invisible.</para>
    /// </summary>
    [Fact]
    public async Task The_Row_Stream_Feeds_CsvWriter_Directly_And_The_Bytes_Are_Right()
    {
        var fixture = FixtureRequest.Load("page-export", FixtureRequest.ResolveDirectory("page-export", "008"));
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.ExportableOrders());

        var columns = fixture.ExportColumns!;
        var options = CsvWriterOptions.ForFormat(ExportFormat.Csv) with { WriteByteOrderMark = false };

        using var destination = new MemoryStream();
        var written = await CsvWriter.WriteAsync(
            EfCoreExportStream.StreamAsync(
                database.Rows(), fixture.Ef, Sqlite, new EfCoreExportOptions { ChunkSize = 2 }),
            columns,
            options,
            destination);

        Assert.Equal(5, written);
        Assert.Equal(
            "Customer,Total,Order Date\r\n" +
            "Acme,19.99,2024-01-01\r\n" +           // a decimal, and a DateOnly rendered ISO
            ",100.55,\r\n" +                        // two nulls -> two empty fields, not "null"
            "\"Beta, Inc\",,2024-03-01\r\n" +       // RFC 4180 quoting around the delimiter
            "'=cmd,-5.25,2024-04-01\r\n" +          // EXP-6 hardens the STRING and leaves the negative decimal alone
            "Zed,0.75,2024-05-02\r\n",
            Encoding.UTF8.GetString(destination.ToArray()));
    }

    // ---- helper ------------------------------------------------------------------------------------

    private static async Task<List<ExportRow>> Drain(
        SqliteFixtureDatabase database, FixtureRequest fixture, int chunkSize)
    {
        var rows = new List<ExportRow>();

        await foreach (var row in EfCoreExportStream.StreamAsync(
            database.Rows(), fixture.Ef, Sqlite, new EfCoreExportOptions { ChunkSize = chunkSize }))
        {
            // Materialised eagerly: the row is a segment over the buffer EF filled for it, valid while
            // the consumer holds it, and this test wants all eight at once.
            rows.Add(new ExportRow([.. Enumerable.Range(0, row.Count).Select(index => row[index])]));
        }

        return rows;
    }
}
