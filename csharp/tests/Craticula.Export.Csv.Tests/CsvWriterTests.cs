using System.Numerics;
using System.Text;
using Craticula.Core.Export;
using Craticula.Core.Schema;

namespace Craticula.Export.Csv.Tests;

/// <summary>
/// The rules of EXP-4/EXP-5/EXP-6 that no fixture can carry, exercised directly: every RFC 4180
/// quoting trigger in isolation (the fixtures show them composed), the configurations the writer
/// REFUSES — which produce no output file and therefore cannot be a byte fixture — the two halves of
/// EXP-6's scope that the fixture tree has no room for, and the streaming contract, which is a
/// property of how the writer consumes its input rather than of what it emits.
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
    /// prefixing reach it without a second call site (see EXP-1(b)'s note on
    /// <see cref="ExportColumn.Header"/>, and
    /// <see cref="Prefixes_A_Header_That_Begins_With_A_Trigger_Character"/> for that half). Pinned here
    /// on the quoting rule: a header containing the delimiter is quoted exactly as a value is.
    /// <c>page-export/008</c> is the fixture that supplies wire headers; it carries no row stream, so
    /// this is the only place header text is exercised at all.
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

    // ---- formula-injection hardening (EXP-6) --------------------------------------------------------

    /// <summary>
    /// EXP-6's "configurable off", through the writer rather than through
    /// <see cref="FormulaInjectionHardening"/>: with the option off, a hostile value reaches the file
    /// exactly as the adapter yielded it, and so does a hostile header. No conformance fixture turns the
    /// option off — the six hostile-value cases carry no <c>writerConfig.json</c> at all, which is what
    /// makes them oracles for the default — so this is the only place the disabled path is graded, like
    /// the legal <c>;</c>+<c>,</c> pairing above.
    /// </summary>
    [Fact]
    public void Emits_The_Raw_Value_When_Hardening_Is_Configured_Off()
    {
        IReadOnlyList<ExportColumn> columns = [new ExportColumn("notes", "=Notes")];
        var options = CsvWriterOptions.Default with
        {
            HardenFormulaInjection = false,
            WriteByteOrderMark = false
        };

        Assert.Equal("=Notes\r\n=1+2\r\n", Write(options, columns, [new ExportRow(["=1+2"])]));
    }

    /// <summary>
    /// EXP-1(b), verbatim: a <c>header</c> is arbitrary client text carried to the writer, so "EXP-6's
    /// formula-injection hardening applies to the header row exactly as it applies to values".
    ///
    /// <para><b>No fixture covers a hostile header, deliberately, and this is the only guard.</b> Adding
    /// a case for it would move the artifact census and the compiler runners' exact counts for a property
    /// that has nothing to do with compilation, and overriding a
    /// header on one of the six hostile-value cases would make its bytes test two things at once. The
    /// header path is structural — one call site, <c>harden: true</c> unconditionally — and this states
    /// it. Note the second column: a header is hardened whatever its text, but the type test that
    /// spares a numeric CELL does not exist for a header, because a header is always a string.</para>
    /// </summary>
    [Fact]
    public void Prefixes_A_Header_That_Begins_With_A_Trigger_Character()
    {
        IReadOnlyList<ExportColumn> columns =
            [new ExportColumn("link", "=Import this"), new ExportColumn("total", "-Net total")];
        var options = CsvWriterOptions.Default with { WriteByteOrderMark = false };

        var output = Write(options, columns, [new ExportRow([1L, 2L])]);

        Assert.Equal("'=Import this,'-Net total\r\n1,2\r\n", output);
    }

    /// <summary>
    /// The narrowing #1652 applied to EXP-6's unqualified wording, stated as the rule
    /// <c>page-export/018</c> pins in bytes: a negative number formats to text beginning with <c>-</c>,
    /// EXP-6's third trigger, and is NOT prefixed, while a client-supplied <see cref="string"/> in the
    /// next column is. Prefixing the number would produce <c>'-500.25</c>, which a spreadsheet renders
    /// as text and will not sum — corrupting the data the export exists to deliver, to defend a column
    /// no user authored.
    ///
    /// <para>Every numeric width is listed rather than just the <see cref="decimal"/> the fixture
    /// carries: <c>-</c> is the leading character of every negative number of every width, and a date
    /// renders with two of them. Note what this test does NOT claim — "not a <see cref="string"/>"
    /// is not the rule; the rule is "a number, a date/time or a <see cref="bool"/>", and
    /// <see cref="Prefixes_A_Cell_Whose_Value_Is_An_Unrecognised_Wrapper_Around_Text"/> is the case
    /// that separates the two.</para>
    /// </summary>
    [Fact]
    public void Does_Not_Prefix_A_Cell_The_Writer_Formatted_From_A_Number_Or_A_Date()
    {
        var options = CsvWriterOptions.Default with { IncludeHeaderRow = false, WriteByteOrderMark = false };

        var output = Write(
            options,
            Columns(6),
            [new ExportRow([-500.25m, -42L, -1.5d, -7.25f, new DateOnly(2026, 3, 16), "-2+3"])]);

        Assert.Equal("-500.25,-42,-1.5,-7.25,2026-03-16,'-2+3\r\n", output);
    }

    /// <summary>
    /// The numeric types <see cref="CsvValueFormatter"/> renders through its <see cref="IFormattable"/>
    /// tail rather than through an arm of its own — so the "is it a number" question cannot be answered
    /// by reading that switch alone, which is how they came to be missing from the scope test at first.
    /// <b><see cref="BigInteger"/> is the one that matters</b>: Npgsql reads a PostgreSQL <c>numeric</c>
    /// into it once the value exceeds <see cref="decimal"/>'s range, so a large negative amount reaches
    /// the writer as one on a perfectly ordinary column. Prefixing it would be exactly the corruption
    /// the narrowing exists to prevent, arriving through the door the narrowing did not look at.
    /// </summary>
    [Fact]
    public void Does_Not_Prefix_A_Negative_Bcl_Numeric_The_Formatter_Renders_Through_Its_Formattable_Tail()
    {
        var options = CsvWriterOptions.Default with { IncludeHeaderRow = false, WriteByteOrderMark = false };

        var output = Write(
            options,
            Columns(5),
            [new ExportRow([
                BigInteger.Parse("-79228162514264337593543950336"),
                Int128.MinValue,
                (nint)(-42),
                (Half)(-1.5f),
                "-2+3"])]);

        Assert.Equal(
            $"-79228162514264337593543950336,{Int128.MinValue},-42,-1.5,'-2+3\r\n",
            output);
    }

    /// <summary>
    /// A nullable column — the ordinary shape of almost every database column — must behave as its
    /// underlying type on both sides of the scope test. Boxing a <see cref="Nullable{T}"/> that has a
    /// value yields a box of <c>T</c>, so no arm of its own is needed; this states that rather than
    /// leaving it to be re-derived, because the failure it would otherwise hide is the corrupting one
    /// (a nullable <see cref="decimal"/> column getting <c>'-500.25</c>).
    /// </summary>
    [Fact]
    public void Treats_A_Nullable_Cell_As_Its_Underlying_Type_On_Both_Sides_Of_The_Scope_Test()
    {
        var options = CsvWriterOptions.Default with { IncludeHeaderRow = false, WriteByteOrderMark = false };

        decimal? negativeAmount = -500.25m;
        long? negativeCount = -42L;
        DateOnly? date = new DateOnly(2026, 3, 16);
        decimal? absent = null;

        var output = Write(options, Columns(4), [new ExportRow([negativeAmount, negativeCount, date, absent])]);

        Assert.Equal("-500.25,-42,2026-03-16,\r\n", output);
    }

    /// <summary>A driver's own wrapper around a text column, as MongoDB's <c>BsonString</c> and
    /// <c>JsonElement</c> both are: not a <see cref="string"/>, but it renders its text through
    /// <see cref="CsvValueFormatter"/>'s <c>ToString()</c> tail and carries whatever the client
    /// wrote.</summary>
    private sealed class TextWrapper(string text)
    {
        public override string ToString() => text;
    }

    /// <summary>
    /// <b>The polarity of EXP-6's type test, and the one case where the two plausible spellings
    /// disagree.</b> <see cref="CsvWriter"/> asks "is this a number, a date or a bool?" and hardens
    /// everything else; the obvious alternative asks "is this a <see cref="string"/>?" and hardens
    /// nothing else. They agree on every type either test enumerates — and differ here, on the type
    /// neither anticipated, which is exactly where an adapter bug lands.
    ///
    /// <para>Without this test the suite cannot tell the two apart, and the failure it guards is the
    /// worst shape a security bug takes: <see cref="CsvValueFormatter"/> renders the wrapper's text
    /// correctly, so the file is well formed, every character is where it belongs, the row count is
    /// right — and the hardening is simply not there. Nothing about the output looks wrong. A
    /// default-deny test cannot fail that way; the worst it can do is put a spurious <c>'</c> on a cell,
    /// which someone reads and reports.</para>
    /// </summary>
    [Fact]
    public void Prefixes_A_Cell_Whose_Value_Is_An_Unrecognised_Wrapper_Around_Text()
    {
        var options = CsvWriterOptions.Default with { IncludeHeaderRow = false, WriteByteOrderMark = false };

        var output = Write(options, Columns(2), [new ExportRow([new TextWrapper("=1+2"), new TextWrapper("safe")])]);

        Assert.Equal("'=1+2,safe\r\n", output);
    }

    /// <summary>
    /// The composition of EXP-6 and EXP-4 on one cell, as a string rather than as a byte diff:
    /// <b>the apostrophe lands INSIDE the opening quote.</b> <c>page-export/020</c> is the byte oracle;
    /// this is the readable statement of it, because a byte-array mismatch is exactly where an operator
    /// precedence like this one is hardest to see.
    ///
    /// <para>The wrong form, <c>'"…"</c> (prefix applied after quoting), is not a badly formatted field
    /// but an unparseable one — a bare character in an unquoted field followed by a stray quote — and
    /// applying the prefix first is also what stops it from being able to change whether quoting was
    /// needed.</para>
    /// </summary>
    [Fact]
    public void Puts_The_Apostrophe_Inside_The_Quotes_When_Both_Rules_Apply()
    {
        var options = CsvWriterOptions.Default with { IncludeHeaderRow = false, WriteByteOrderMark = false };

        var output = WriteOneRow(options, "\rHe said \"hello\", then\nleft");

        Assert.Equal("\"'\rHe said \"\"hello\"\", then\nleft\"\r\n", output);
        Assert.StartsWith("\"'", output, StringComparison.Ordinal);
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
    /// bare because <c>,</c> is no longer a delimiter. This is the <b>writer-wide</b> form of that
    /// pairing and it has no fixture — 013 overrides the delimiter only, and
    /// <c>page-export/026</c> pins the <em>per-field</em> form, where the separator comes from schema
    /// config as EXP-5 specifies. A writer-wide separator is a host configuration no schema declares,
    /// so it stays here.
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

    // ---- EXP-5's per-field overrides (#1653) ---------------------------------------------------------

    /// <summary>
    /// EXP-5's two overrides, on two different fields of the same row, with five columns beside them
    /// declaring nothing. Both halves matter: the two overridden columns change, and — the property one
    /// override alone cannot show — the untouched ones do not, so a rendering resolved once and applied
    /// to everything that matched would fail here. <c>page-export/026</c> is the byte oracle for the
    /// same statement; this is the readable form, and the only one that also covers a
    /// <see cref="DateTime"/> and a <see cref="double"/> (that fixture's schema declares neither).
    /// </summary>
    [Fact]
    public void Applies_Each_Columns_Own_Format_And_Leaves_The_Others_Alone()
    {
        IReadOnlyList<ExportColumn> columns =
        [
            new ExportColumn("id", "id"),
            new ExportColumn("orderDate", "orderDate", new FormatOptions { DatePattern = "dd/MM/yyyy" }),
            new ExportColumn("shippedAt", "shippedAt"),
            new ExportColumn("totalAmount", "totalAmount", new FormatOptions { DecimalSeparator = ',' }),
            new ExportColumn("taxRate", "taxRate"),
            new ExportColumn("status", "status")
        ];
        var options = CsvWriterOptions.SemicolonPreset with
        {
            IncludeHeaderRow = false,
            WriteByteOrderMark = false
        };

        var output = Write(options, columns, [new ExportRow([
            1001L,
            new DateOnly(2026, 1, 5),
            new DateTime(2026, 1, 5, 14, 30, 0),
            1234.50m,
            0.25d,
            "shipped"])]);

        Assert.Equal("1001;05/01/2026;2026-01-05T14:30:00;1234,50;0.25;shipped\r\n", output);
    }

    /// <summary>
    /// The widened collision guard. A field-declared separator equal to the delimiter is the identical
    /// defect to the writer-wide one this suite already pins — a well-formed WRONG file — and before
    /// #1653 it would have been written out without a word. It fails before the first byte, like its
    /// sibling, and <b>names the field</b>: a schema with twenty columns gives a host no other way to
    /// find the declaration to fix, and the message is all the host gets.
    /// </summary>
    [Fact]
    public void Refuses_A_Per_Field_Decimal_Separator_Equal_To_The_Delimiter_Before_Writing_Any_Byte()
    {
        IReadOnlyList<ExportColumn> columns =
        [
            new ExportColumn("status", "status"),
            new ExportColumn("totalAmount", "totalAmount", new FormatOptions { DecimalSeparator = ',' })
        ];
        using var destination = new MemoryStream();

        var exception = Assert.Throws<CsvWriterException>(() => CsvWriter.Write(
            [new ExportRow(["shipped", 1234.50m])], columns, CsvWriterOptions.Default, destination));

        Assert.Contains("EXP-5", exception.Message, StringComparison.Ordinal);
        Assert.Contains("totalAmount", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, destination.Length);
    }

    /// <summary>The same refusal on the async path and on <see cref="CsvWriterOptions.Validate"/>'s
    /// column-aware overload — which is where the check lives, so that a host holding its schema and
    /// its options can reject the pair at startup rather than on the first user's download, exactly as
    /// it can for the writer-wide half.</summary>
    [Fact]
    public async Task Refuses_The_Per_Field_Collision_On_The_Async_Path_And_On_Validate()
    {
        IReadOnlyList<ExportColumn> columns =
            [new ExportColumn("totalAmount", "totalAmount", new FormatOptions { DecimalSeparator = '\t' })];
        var options = CsvWriterOptions.ForFormat(ExportFormat.Tsv);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<CsvWriterException>(
            () => CsvWriter.WriteAsync(Empty(), columns, options, destination));
        Assert.Throws<CsvWriterException>(() => options.Validate(columns));

        // ...and the options alone are fine: the collision is a property of the PAIR, which is why the
        // parameterless overload cannot see it and why a GridSchema cannot either (the same schema is
        // legal under ';' and illegal under a tab).
        options.Validate();
        Assert.Equal(0, destination.Length);
    }

    /// <summary>A per-field separator that is one of RFC 4180's own characters is refused for the same
    /// reason the writer-wide one is: no reader could tell it from an escape. Same rule, same place,
    /// because a field is not a lesser source of a separator.</summary>
    [Theory]
    [InlineData('"')]
    [InlineData('\r')]
    [InlineData('\n')]
    public void Refuses_A_Per_Field_Decimal_Separator_That_Is_A_Quoting_Character(char separator)
    {
        IReadOnlyList<ExportColumn> columns =
            [new ExportColumn("totalAmount", "totalAmount", new FormatOptions { DecimalSeparator = separator })];

        var exception = Assert.Throws<CsvWriterException>(
            () => CsvWriterOptions.Default.Validate(columns));

        Assert.Contains("totalAmount", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A column that declares no override at all goes through the per-field path untouched —
    /// the guard has nothing to check and the rendering is the writer-wide default. Stated because
    /// every fixture but 026 depends on it, and they would fail as byte diffs.</summary>
    [Fact]
    public void A_Projection_Declaring_No_Format_Is_Neither_Refused_Nor_Re_Rendered()
    {
        var options = CsvWriterOptions.Default with { IncludeHeaderRow = false, WriteByteOrderMark = false };

        Assert.Equal(
            "1234.50,2026-01-05\r\n",
            Write(options, Columns(2), [new ExportRow([1234.50m, new DateOnly(2026, 1, 5)])]));
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

        // EXP-6's "default ON", which is the direction that matters: a hardening that had to be opted
        // into would leave every host that never read this file exporting unprotected files.
        Assert.True(CsvWriterOptions.Default.HardenFormulaInjection);
        Assert.True(CsvWriterOptions.SemicolonPreset.HardenFormulaInjection);
        Assert.True(CsvWriterOptions.ForFormat(ExportFormat.Tsv).HardenFormulaInjection);
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
