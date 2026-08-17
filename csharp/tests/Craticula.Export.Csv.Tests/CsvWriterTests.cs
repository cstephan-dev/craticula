using System.Text;
using Craticula.Core.Export;

namespace Craticula.Export.Csv.Tests;

/// <summary>
/// The rules of EXP-4/EXP-5 that no fixture can carry, exercised directly: every RFC 4180 quoting
/// trigger in isolation (the fixtures show them composed), the configurations the writer REFUSES —
/// which produce no output file and therefore cannot be a byte fixture — and the streaming contract,
/// which is a property of how the writer consumes its input rather than of what it emits.
///
/// <para>What is deliberately NOT here: a second oracle for the delimiter presets and the BOM. Those
/// are graded by <see cref="CsvWriterConformanceFixtureTests"/> against hand-authored files, and
/// re-asserting them from the implementation's own author would be exactly the weak oracle #1651 was
/// asked to remove.</para>
/// </summary>
public class CsvWriterTests
{
    // ---- RFC 4180 quoting (EXP-4) ------------------------------------------------------------------

    [Theory]
    // No trigger character: written bare, whatever else is in it.
    [InlineData("plain", "plain")]
    [InlineData("a;b", "a;b")]
    [InlineData("  padded  ", "  padded  ")]
    [InlineData("", "")]
    // §2 rule 6: the delimiter, a quote or a line break force the whole field into quotes.
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("a\nb", "\"a\nb\"")]
    [InlineData("a\rb", "\"a\rb\"")]
    [InlineData("a\r\nb", "\"a\r\nb\"")]
    // §2 rule 7: an inner quote is doubled, inside the quotes rule 6 already required.
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("\"", "\"\"\"\"")]
    [InlineData("\",\"", "\"\"\",\"\"\"")]
    public void Quotes_A_Field_Exactly_When_RFC_4180_Requires_It(string value, string expected)
    {
        var output = WriteOneRow(CsvWriterOptions.Default with { IncludeHeaderRow = false, WriteByteOrderMark = false }, value);

        Assert.Equal($"{expected}\r\n", output);
    }

    /// <summary>
    /// The trigger set follows the ACTIVE delimiter, which is the half of EXP-4 that a writer with a
    /// hardcoded comma would still pass every comma-delimited test on. <c>page-export/013</c> and
    /// <c>/025</c> pin the same rule in bytes; this states it as the rule.
    /// </summary>
    [Theory]
    [InlineData(',', "a,b", "\"a,b\"")]
    [InlineData(',', "a;b", "a;b")]
    [InlineData(',', "a\tb", "a\tb")]
    [InlineData(';', "a,b", "a,b")]
    [InlineData(';', "a;b", "\"a;b\"")]
    [InlineData('\t', "a,b", "a,b")]
    [InlineData('\t', "a\tb", "\"a\tb\"")]
    public void The_Quoting_Trigger_Is_Whichever_Character_Is_The_Delimiter(char delimiter, string value, string expected)
    {
        var options = CsvWriterOptions.Default with
        {
            Delimiter = delimiter,
            IncludeHeaderRow = false,
            WriteByteOrderMark = false
        };

        Assert.Equal($"{expected}\r\n", WriteOneRow(options, value));
    }

    /// <summary>A null cell is an empty, UNQUOTED field — and so is an empty string. A CSV file has no
    /// null, so the writer must not invent a distinction (quoting one of them) that no reader could
    /// interpret.</summary>
    [Fact]
    public void A_Null_Cell_And_An_Empty_String_Are_The_Same_Empty_Field()
    {
        var options = CsvWriterOptions.Default with { IncludeHeaderRow = false, WriteByteOrderMark = false };

        Assert.Equal(",\r\n", Write(options, Columns(2), [new ExportRow([null, string.Empty])]));
    }

    [Fact]
    public void Terminates_Every_Record_With_CRLF_Including_The_Last()
    {
        var options = CsvWriterOptions.Default with { WriteByteOrderMark = false };

        var output = Write(options, Columns(1), [new ExportRow(["a"]), new ExportRow(["b"])]);

        Assert.Equal("col0\r\na\r\nb\r\n", output);
    }

    [Fact]
    public void Emits_No_Header_Record_When_The_Header_Row_Is_Configured_Off()
    {
        var options = CsvWriterOptions.Default with { IncludeHeaderRow = false, WriteByteOrderMark = false };

        Assert.Equal("a\r\n", Write(options, Columns(1), [new ExportRow(["a"])]));
    }

    /// <summary>
    /// The header row goes through the same field path as a data cell, which is what makes EXP-6's
    /// prefixing reach it in #1652 without a second call site (see EXP-1(b)'s note on
    /// <see cref="ExportColumn.Header"/>). Pinned here on the rule that already exists: a header
    /// containing the delimiter is quoted exactly as a value is. <c>page-export/008</c> is the
    /// fixture that supplies wire headers; it carries no row stream yet, so this is the only place
    /// header text is exercised at all.
    /// </summary>
    [Fact]
    public void Quotes_A_Header_The_Same_Way_It_Quotes_A_Cell()
    {
        IReadOnlyList<ExportColumn> columns =
            [new ExportColumn("total", "Total, net"), new ExportColumn("note", "He said \"go\"")];
        var options = CsvWriterOptions.Default with { WriteByteOrderMark = false };

        var output = Write(options, columns, [new ExportRow(["1", "2"])]);

        Assert.Equal("\"Total, net\",\"He said \"\"go\"\"\"\r\n1,2\r\n", output);
    }

    // ---- refusals (EXP-5 and the structural impossibilities) ----------------------------------------

    /// <summary>
    /// EXP-5, verbatim: "The writer MUST warn/fail if the decimal separator equals the delimiter".
    /// This module fails — see <see cref="CsvWriterException"/> for warn-vs-fail — and it fails
    /// BEFORE writing anything, so a host that catches it and retries with a corrected configuration
    /// is not appending to a half-written file.
    /// </summary>
    [Fact]
    public void Refuses_A_Decimal_Separator_Equal_To_The_Delimiter_Before_Writing_Any_Byte()
    {
        var options = CsvWriterOptions.Default with { DecimalSeparator = ',' };
        using var destination = new MemoryStream();

        var exception = Assert.Throws<CsvWriterException>(
            () => CsvWriter.Write([new ExportRow(["a"])], Columns(1), options, destination));

        Assert.Contains("EXP-5", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, destination.Length);
    }

    /// <summary>The same collision on the async path, and on <see cref="CsvWriterOptions.Validate"/>
    /// itself — a host should be able to reject its configuration at startup rather than on the first
    /// user's download.</summary>
    [Fact]
    public async Task Refuses_The_Collision_On_The_Async_Path_And_On_Validate()
    {
        var options = CsvWriterOptions.SemicolonPreset with { DecimalSeparator = ';' };
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<CsvWriterException>(
            () => CsvWriter.WriteAsync(Empty(), Columns(1), options, destination));
        Assert.Throws<CsvWriterException>(options.Validate);
        Assert.Equal(0, destination.Length);
    }

    /// <summary>
    /// The pairing the collision guard exists to ALLOW: <c>;</c> delimiter with a <c>,</c> decimal
    /// separator is the French/European locale EXP-4's preset is named for, and the number is written
    /// bare because <c>,</c> is no longer a delimiter. No fixture pins this (013 overrides the
    /// delimiter only — see its description), which is why it is stated here.
    /// </summary>
    [Fact]
    public void Accepts_A_Comma_Decimal_Separator_When_The_Delimiter_Is_A_Semicolon()
    {
        var options = CsvWriterOptions.SemicolonPreset with
        {
            DecimalSeparator = ',',
            IncludeHeaderRow = false,
            WriteByteOrderMark = false
        };

        var output = Write(options, Columns(2), [new ExportRow([1234.50m, "x"])]);

        Assert.Equal("1234,50;x\r\n", output);
    }

    [Theory]
    [InlineData('"')]
    [InlineData('\r')]
    [InlineData('\n')]
    public void Refuses_A_Delimiter_That_Is_Itself_A_Quoting_Character(char delimiter)
    {
        var options = CsvWriterOptions.Default with { Delimiter = delimiter };

        Assert.Throws<CsvWriterException>(options.Validate);
    }

    [Fact]
    public void Refuses_A_Row_Whose_Cell_Count_Does_Not_Match_The_Projection()
    {
        using var destination = new MemoryStream();

        var exception = Assert.Throws<CsvWriterException>(() => CsvWriter.Write(
            [new ExportRow(["a", "b"]), new ExportRow(["a"])],
            Columns(2),
            CsvWriterOptions.Default,
            destination));

        // The ordinal is in the message because "row 1 of your export" is the only way to find the
        // offending row again in a stream nobody kept.
        Assert.Contains("Row 1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_An_Export_That_Resolves_To_No_Columns()
    {
        using var destination = new MemoryStream();

        Assert.Throws<CsvWriterException>(
            () => CsvWriter.Write([], [], CsvWriterOptions.Default, destination));
    }

    // ---- the streaming contract (EXP-3/EXP-7's side of EXP-4) --------------------------------------

    /// <summary>
    /// The writer never materializes the row stream: bytes reach the destination while rows are still
    /// being pulled. Asserted by a destination that fails on its first write — a writer that buffered
    /// the whole result would pull all million rows before finding out, and the row counter proves it
    /// pulled a few hundred instead.
    ///
    /// <para>The source is bounded at a million (EXP-7's default cap) rather than infinite so that a
    /// regression here fails the suite instead of hanging it.</para>
    /// </summary>
    [Fact]
    public async Task Streams_To_The_Destination_Without_Materializing_The_Row_Stream()
    {
        var pulled = 0;
        var destination = new FailOnWriteStream();

        // BOM off: it is written straight to the destination and would fail before a single row was
        // pulled, which is a different (and uninteresting) property.
        await Assert.ThrowsAsync<IOException>(() => CsvWriter.WriteAsync(
            Counted(1_000_000, () => pulled++),
            Columns(3),
            CsvWriterOptions.Default with { WriteByteOrderMark = false },
            destination));

        Assert.True(pulled is > 0 and < 5_000, $"pulled {pulled} rows before the first byte was pushed");
    }

    /// <summary>EXP-7's cancellation hook, from the writer's side: a cancelled token stops the
    /// enumeration rather than being noticed after the last row.</summary>
    [Fact]
    public async Task Stops_Pulling_Rows_When_The_Token_Is_Cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var pulled = 0;
        using var destination = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CsvWriter.WriteAsync(
            Counted(1_000_000, () =>
            {
                if (++pulled == 10)
                {
                    cancellation.Cancel();
                }
            }),
            Columns(3),
            CsvWriterOptions.Default,
            destination,
            cancellation.Token));

        Assert.True(pulled < 5_000, $"pulled {pulled} rows after cancellation");
    }

    /// <summary>The writer writes and flushes; it does not own the destination. A host streaming into
    /// an HTTP response body would otherwise have its response closed by the export.</summary>
    [Fact]
    public async Task Leaves_The_Destination_Stream_Open()
    {
        using var destination = new MemoryStream();

        await CsvWriter.WriteAsync(Empty(), Columns(1), CsvWriterOptions.Default, destination);

        Assert.True(destination.CanWrite);
    }

    /// <summary>An empty result is a header row and nothing else — not an empty file. The grid asked
    /// for these columns; a file with no header at all would be indistinguishable from a failed
    /// export.</summary>
    [Fact]
    public async Task An_Empty_Row_Stream_Still_Emits_The_Header_Row()
    {
        using var destination = new MemoryStream();

        var rows = await CsvWriter.WriteAsync(
            Empty(), Columns(2), CsvWriterOptions.Default with { WriteByteOrderMark = false }, destination);

        Assert.Equal(0, rows);
        Assert.Equal("col0,col1\r\n", Encoding.UTF8.GetString(destination.ToArray()));
    }

    // ---- BOM and format defaults --------------------------------------------------------------------

    [Fact]
    public void The_Byte_Order_Mark_Is_The_First_Three_Bytes_And_Precedes_The_Header()
    {
        using var destination = new MemoryStream();

        CsvWriter.Write([], Columns(1), CsvWriterOptions.Default, destination);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'c' }, destination.ToArray().Take(4).ToArray());
    }

    [Fact]
    public void ForFormat_Maps_Tsv_To_A_Tab_And_Csv_To_A_Comma()
    {
        Assert.Equal(',', CsvWriterOptions.ForFormat(ExportFormat.Csv).Delimiter);
        Assert.Equal('\t', CsvWriterOptions.ForFormat(ExportFormat.Tsv).Delimiter);
    }

    [Fact]
    public void The_Defaults_Are_EXP_4s_Defaults()
    {
        // Stated once, here, because three fixtures are compared against bytes these defaults
        // produced and would fail obscurely (as a byte diff) if one of them changed.
        Assert.Equal(',', CsvWriterOptions.Default.Delimiter);
        Assert.True(CsvWriterOptions.Default.IncludeHeaderRow);
        Assert.True(CsvWriterOptions.Default.WriteByteOrderMark);
        Assert.Equal('.', CsvWriterOptions.Default.DecimalSeparator);
        Assert.Equal(';', CsvWriterOptions.SemicolonPreset.Delimiter);
        Assert.Equal('.', CsvWriterOptions.SemicolonPreset.DecimalSeparator);
    }

    // ---- ExportRow ----------------------------------------------------------------------------------

    /// <summary>A name-keyed document is projected onto the column order, and a key the document does
    /// not carry is an empty cell rather than a shifted row — MongoDB omits a missing field entirely,
    /// so this is the ordinary case for that adapter, not an error path.</summary>
    [Fact]
    public void FromCells_Projects_Onto_The_Column_Order_And_Tolerates_A_Missing_Key()
    {
        IReadOnlyList<ExportColumn> columns =
            [new ExportColumn("b", "b"), new ExportColumn("a", "a"), new ExportColumn("gone", "gone")];

        var row = ExportRow.FromCells(
            new Dictionary<string, object?> { ["a"] = 1L, ["b"] = 2L }, columns);

        var options = CsvWriterOptions.Default with { IncludeHeaderRow = false, WriteByteOrderMark = false };
        Assert.Equal("2,1,\r\n", Write(options, columns, [row]));
    }

    // ---- harness ------------------------------------------------------------------------------------

    private static IReadOnlyList<ExportColumn> Columns(int columnCount) =>
        [.. Enumerable.Range(0, columnCount).Select(index => new ExportColumn($"col{index}", $"col{index}"))];

    private static string WriteOneRow(CsvWriterOptions options, object? value) =>
        Write(options, Columns(1), [new ExportRow([value])]);

    private static string Write(
        CsvWriterOptions options, IReadOnlyList<ExportColumn> columns, IEnumerable<ExportRow> rows)
    {
        using var destination = new MemoryStream();
        CsvWriter.Write(rows, columns, options, destination);
        return Encoding.UTF8.GetString(destination.ToArray());
    }

#pragma warning disable CS1998 // an empty async iterator needs no await; it exists to be an IAsyncEnumerable
    private static async IAsyncEnumerable<ExportRow> Empty()
    {
        yield break;
    }

    private static async IAsyncEnumerable<ExportRow> Counted(int count, Action onPull)
    {
        for (var index = 0; index < count; index++)
        {
            onPull();
            yield return new ExportRow(["a", "b", "c"]);
        }
    }
#pragma warning restore CS1998

    /// <summary>A destination that refuses the first byte it is offered — the cheapest way to observe
    /// WHEN the writer pushes, without depending on its buffer size.</summary>
    private sealed class FailOnWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("destination refused the write");

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("destination refused the write");
    }
}
