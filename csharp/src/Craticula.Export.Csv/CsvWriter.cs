using System.Numerics;
using System.Text;
using Craticula.Core.Export;

namespace Craticula.Export.Csv;

/// <summary>
/// EXP-4: the backend-agnostic CSV/TSV writer. Consumes any execution adapter's row stream, in the
/// order and the columns EXP-1(b) resolved, and emits RFC 4180 bytes — CRLF records, a quoted field
/// wherever the delimiter, a double quote or a line ending appears inside it, a configurable
/// delimiter and an optional UTF-8 BOM.
///
/// <para><b>It streams, and that is a contract, not an implementation detail (EXP-3/EXP-7).</b> The
/// row stream is enumerated one row at a time and each record is handed to the destination as it is
/// built; the only thing this module holds is one record's worth of text plus the
/// <see cref="StreamWriter"/>'s fixed buffer. A host can therefore wrap
/// <see cref="WriteAsync(IAsyncEnumerable{ExportRow}, IReadOnlyList{ExportColumn}, CsvWriterOptions, Stream, CancellationToken)"/>
/// in an HTTP response body, a file, or a background job producing a download link, and a
/// million-row export costs the same memory as a ten-row one. Awaiting each write is also what gives
/// backpressure for free: a slow destination stops the enumerator rather than filling memory ahead
/// of it. The <see cref="CancellationToken"/> is EXP-7's cancellation hook as seen from this side —
/// the row cap itself is the adapter's, and this module deliberately does not enforce one (it would
/// have to guess whether a truncated file or an error is what the host wants).</para>
///
/// <para><b>The writer takes a column list, not a request.</b> Its input is EXP-1(b)'s already
/// resolved <see cref="ExportColumn"/> list — whichever grid adapter parsed the request produced it
/// (<c>Craticula.AgGrid</c>'s <c>ExportSpec.Columns</c> for AG Grid) — and never the request object
/// itself. That is what keeps this module free of any grid vocabulary, and it also removes a trap: a
/// writer holding the whole spec could read the <see cref="ExportFormat"/> off it and silently
/// disagree with the delimiter the caller configured. The format reaches the file through exactly one
/// door, <see cref="CsvWriterOptions.ForFormat"/>, on the caller's side.</para>
///
/// <para><b>It also hardens against EXP-6's formula injection, and the rule is narrower than EXP-6's
/// wording.</b> A field whose first character is <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or CR is
/// prefixed with <c>'</c> (see <see cref="FormulaInjectionHardening"/> for what that protects against),
/// but only where the text could be client-supplied: <b>every header</b>, which is arbitrary wire text
/// by EXP-1(b), and <b>every data cell except those the writer formatted from a number, a date/time or
/// a <see cref="bool"/></b> — see <see cref="IsInHardeningScope"/>, which states the rule as an
/// exclusion rather than as "is it a <see cref="string"/>" on purpose, so that a value type this writer
/// does not recognise is hardened instead of being waved through.</para>
///
/// <para><b>Why, in one sentence: <c>-</c> is also the leading character of every negative number.</b>
/// EXP-6 reads "values starting with ... <c>-</c>", unqualified, and applied literally it turns
/// <c>-500.25</c> into <c>'-500.25</c> — which a spreadsheet displays as text, will not sum, and cannot
/// be coerced back without the reader knowing this writer's convention. That corrupts the data the
/// export exists to deliver, in the ordinary case, to protect against an attack that cannot reach a
/// numeric column: OWASP's formula injection is about content a user authored, and a value the writer
/// received as a <see cref="decimal"/> was produced by the database and re-rendered here (EXP-5), so
/// there is no user text in it to neutralise. <c>page-export/018</c> is the oracle for both halves —
/// one record where the string <c>-2+3</c> is prefixed and the decimal <c>-500.25</c> beside it is not.
/// The narrowing is recorded at EXP-6 itself, in <c>docs/TRD-grid-query-compiler.md</c> §6.1, because
/// that prose is its normative home. The discriminator is the CLR type of the value and NOT the
/// schema's declared <c>FieldType</c>, for the reason <see cref="CsvValueFormatter"/> gives: the
/// adapter already decided how to materialise the column, and letting the schema win over the actual
/// value is how the two disagree silently.</para>
///
/// <para><b>What that still puts on an execution adapter (#1653).</b> Because EXP-5's formatting reads
/// the CLR type, an adapter must yield <em>native</em> CLR values and never its driver's wrapper types:
/// a <see cref="decimal"/> handed over as a <c>BsonDecimal128</c> loses
/// <see cref="CsvValueFormatter"/>'s scale-preserving arm and is rendered by whatever that wrapper's
/// <c>ToString()</c> does. What it no longer costs is a <em>security</em> failure —
/// <see cref="IsInHardeningScope"/> denies by default, so an unrecognised wrapper around text is
/// hardened rather than skipped. The residual risk is a formatting divergence, which is visible in the
/// bytes and gradeable by a fixture; that is the right shape for an obligation on another story to
/// carry. The opposite case, a numeric column boxed as a <see cref="string"/>, is the only one that
/// would still justify carrying a <c>FieldType</c> on <see cref="ExportColumn"/>; it costs a spurious
/// <c>'</c> rather than a hole, no adapter is known to do it, and adding a <c>Craticula.Core</c>
/// contract member for a hypothetical one buys nothing while the value is in scope one frame above the
/// seam.</para>
/// </summary>
public static class CsvWriter
{
    /// <summary>EXP-4's BOM, written as literal bytes rather than through the encoder so that
    /// "the first three bytes are EF BB BF" is a decision this file makes once and visibly.</summary>
    private static readonly byte[] Utf8ByteOrderMark = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// UTF-8 with no encoder-emitted preamble: the BOM is <see cref="CsvWriterOptions.WriteByteOrderMark"/>'s
    /// business, and an encoder that also emitted one would put the toggle in two places.
    ///
    /// <para>Invalid text (a lone surrogate, which SQL Server's <c>NVARCHAR</c> and MongoDB's
    /// <c>string</c> can both store) is replaced with U+FFFD rather than throwing. An export is a
    /// bulk read of data the writer did not author; failing the whole file over one unpaired
    /// code unit would deny the user the other million rows, and the replacement character is
    /// visible in the output rather than silent.</para>
    /// </summary>
    private static readonly UTF8Encoding Utf8WithoutPreamble = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>RFC 4180 §2: "Each record is located on a separate line, delimited by a line break
    /// (CRLF)". Every record is terminated, including the last one — the RFC makes the final CRLF
    /// optional, and emitting it unconditionally is the form that concatenates cleanly (a chunked
    /// export whose parts are appended must not glue two records together).</summary>
    private const string RecordSeparator = "\r\n";

    private const int BufferSize = 16 * 1024;

    /// <summary>
    /// Writes the export to <paramref name="destination"/> and returns the number of <em>data</em>
    /// rows written (the header row is not counted). The count is returned because the writer is the
    /// one place that already knows it — a host enforcing EXP-7's 1,000,000-row cap, or logging what
    /// a user downloaded, would otherwise have to count the stream a second time.
    /// </summary>
    /// <param name="rows">The adapter's row stream, positionally aligned with
    /// <paramref name="columns"/>. Enumerated exactly once, lazily.</param>
    /// <param name="columns">EXP-1(b)'s resolved projection, in emission order — for an AG Grid host,
    /// <c>AgGridParsedRequest.Export.Columns</c> straight off the parser.</param>
    /// <param name="options">EXP-4/EXP-5's writer configuration. Validated before the first byte.
    /// Build it with <see cref="CsvWriterOptions.ForFormat"/> so the request's own
    /// <see cref="ExportFormat"/> reaches the delimiter.</param>
    /// <param name="destination">The byte sink. Left open — this method writes and flushes, it does
    /// not own the stream.</param>
    /// <param name="cancellationToken">EXP-7's cancellation hook.</param>
    /// <exception cref="CsvWriterException">The options collide (EXP-5), or a row's cell count does
    /// not match the column count.</exception>
    public static async Task<long> WriteAsync(
        IAsyncEnumerable<ExportRow> rows,
        IReadOnlyList<ExportColumn> columns,
        CsvWriterOptions options,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Prepare(columns, options, destination);

        if (options.WriteByteOrderMark)
        {
            await destination.WriteAsync(Utf8ByteOrderMark, cancellationToken).ConfigureAwait(false);
        }

        await using var writer = new StreamWriter(destination, Utf8WithoutPreamble, BufferSize, leaveOpen: true);
        var record = new StringBuilder();
        var rowCount = 0L;

        if (options.IncludeHeaderRow)
        {
            BuildHeaderRecord(record, columns, options);
            await writer.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        }

        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // Checked per row rather than left to the destination: EXP-7's cancellation hook has to
            // stop an export that is streaming into a buffer (or into a sink that never blocks), and
            // a token observed only by whatever happens to await is a hook that works on some
            // destinations and not others. It also stops PULLING rows, which is the expensive half —
            // the adapter's cursor is what a cancelled export needs to release.
            cancellationToken.ThrowIfCancellationRequested();

            BuildDataRecord(record, row, columns.Count, rowCount, options);
            await writer.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            rowCount++;
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return rowCount;
    }

    /// <summary>
    /// The synchronous counterpart, for an adapter whose driver exposes a blocking cursor (several
    /// ADO.NET providers do). Identical output by construction: both overloads build their records
    /// through the same <see cref="BuildHeaderRecord"/>/<see cref="BuildDataRecord"/> pair, so the
    /// two can differ only in how the bytes are pushed, never in what the bytes are.
    /// </summary>
    public static long Write(
        IEnumerable<ExportRow> rows,
        IReadOnlyList<ExportColumn> columns,
        CsvWriterOptions options,
        Stream destination)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Prepare(columns, options, destination);

        if (options.WriteByteOrderMark)
        {
            destination.Write(Utf8ByteOrderMark);
        }

        using var writer = new StreamWriter(destination, Utf8WithoutPreamble, BufferSize, leaveOpen: true);
        var record = new StringBuilder();
        var rowCount = 0L;

        if (options.IncludeHeaderRow)
        {
            BuildHeaderRecord(record, columns, options);
            writer.Write(record);
        }

        foreach (var row in rows)
        {
            BuildDataRecord(record, row, columns.Count, rowCount, options);
            writer.Write(record);
            rowCount++;
        }

        writer.Flush();
        return rowCount;
    }

    /// <summary>Everything both overloads must do before touching the destination: reject a
    /// colliding configuration (EXP-5) while nothing has been written yet.</summary>
    private static void Prepare(
        IReadOnlyList<ExportColumn> columns, CsvWriterOptions options, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(destination);

        options.Validate();

        // EXP-1(b) guarantees a non-empty resolved list, and AgGridRequestParser rejects an empty
        // exportColumns outright — but this list can also be hand-built by a host, and a zero-column
        // export would emit a file of empty lines rather than fail. The compilers guard their end of
        // exactly this (`Projection is { Count: 0 }`); this is the writer's end.
        if (columns.Count == 0)
        {
            throw new CsvWriterException(
                "EXP-1(b): the export resolves to no columns at all. A CSV file with no columns is " +
                "a file of blank lines, not an empty export.");
        }
    }

    /// <summary>
    /// EXP-4's header row: the resolved <see cref="ExportColumn.Header"/> of each column, in
    /// projection order. It goes through <see cref="AppendField"/> like any data cell, which is both
    /// why a header containing the delimiter is quoted and how EXP-6's prefixing reaches it (#1650
    /// recorded that obligation on <see cref="ExportColumn"/> itself).
    ///
    /// <para>It passes <c>harden: true</c> <b>unconditionally</b>, with none of the type test a data
    /// cell gets: a header is arbitrary client text by EXP-1(b) — a wire-supplied <c>header</c> override,
    /// else the schema's <c>label</c>, else the colId — and it is always a <see cref="string"/>, so the
    /// narrowing the data path applies has nothing to exclude here. A grid whose column captions are
    /// user-editable is exactly how <c>=cmd|…</c> reaches a header row.</para>
    /// </summary>
    private static void BuildHeaderRecord(
        StringBuilder record, IReadOnlyList<ExportColumn> columns, CsvWriterOptions options)
    {
        record.Clear();
        for (var index = 0; index < columns.Count; index++)
        {
            if (index > 0)
            {
                record.Append(options.Delimiter);
            }

            AppendField(record, columns[index].Header, options, harden: true);
        }

        record.Append(RecordSeparator);
    }

    private static void BuildDataRecord(
        StringBuilder record, ExportRow row, int columnCount, long rowOrdinal, CsvWriterOptions options)
    {
        // A row that does not match the projection is a bug in the adapter that produced it, and the
        // symptom without this check is a file whose columns silently shift from that row on — the
        // same class of damage EXP-5's collision guard exists to prevent, so it gets the same
        // treatment. The ordinal is included because "row 400123 of your export" is the only way to
        // find it again in a stream nobody kept.
        if (row.Count != columnCount)
        {
            throw new CsvWriterException(
                $"Row {rowOrdinal} carries {row.Count} cells but the export projects {columnCount} " +
                "columns. The row stream must be positionally aligned with the resolved column list.");
        }

        record.Clear();
        for (var index = 0; index < columnCount; index++)
        {
            if (index > 0)
            {
                record.Append(options.Delimiter);
            }

            // The value is read into a local because EXP-6's scope is decided by its CLR TYPE, and the
            // type is gone one line later: CsvValueFormatter returns a string for every input, so a
            // decimal that formatted to "-500.25" and a client-supplied "-2+3" are indistinguishable
            // at the seam. `-` is the leading character of every negative number, so hardening a
            // formatted number would corrupt it (class remarks).
            var value = row[index];

            AppendField(
                record,
                CsvValueFormatter.Format(value, options.DecimalSeparator),
                options,
                harden: IsInHardeningScope(value));
        }

        record.Append(RecordSeparator);
    }

    /// <summary>
    /// Whether a data cell's value is in EXP-6's scope, decided as a <b>default-deny</b> test: every
    /// type <see cref="CsvValueFormatter"/> renders from a number, a date/time or a boolean is out of
    /// scope, and <b>anything else is in it</b> — a <see cref="string"/>, but equally an enum, a
    /// <see cref="char"/>, or a type this writer has never heard of.
    ///
    /// <para><b>The polarity is the whole point, and the obvious spelling (<c>value is string</c>) has
    /// it backwards.</b> Both tests agree on every type listed below and on <see cref="string"/>; they
    /// differ only on the unknown one, and that is precisely where an adapter bug lands. A text column
    /// handed over as a driver's own wrapper — a <c>BsonString</c>, a <c>JsonElement</c>, a
    /// <c>DbDataRecord</c> field object — is not a <see cref="string"/>, yet
    /// <see cref="CsvValueFormatter"/> falls through to its <see cref="IFormattable"/>/<c>ToString()</c>
    /// tail and produces exactly the right text. Under <c>value is string</c> that cell would be
    /// written unhardened, and <b>the failure is invisible in the only direction that matters</b>: the
    /// file is well formed, every character is where it should be, and the security control is simply
    /// absent. No test in this module could see it, because a test would have to guess which wrapper
    /// type a future adapter picks. Denying by default means an unrecognised type is hardened, so the
    /// worst an adapter bug can now cost is a spurious <c>'</c> on a cell — visible, reportable, and
    /// not a vulnerability.
    ///
    /// <para><b>The excluded set must cover every type <see cref="CsvValueFormatter.Format"/> renders
    /// as a number, and that is more than its explicit arms.</b> The formatter ends in
    /// <c>IFormattable formattable => formattable.ToString(null, InvariantCulture)</c>, so a BCL numeric
    /// it never names still comes out as digits — and a negative one still leads with <c>-</c>. Hence
    /// <see cref="Half"/>, <see cref="Int128"/>, <see cref="UInt128"/>, <see cref="nint"/>,
    /// <see cref="nuint"/> and <see cref="BigInteger"/> below: none has a <c>FieldType</c> and none
    /// appears in the formatter's switch, but <c>BigInteger</c> in particular is what Npgsql reads a
    /// PostgreSQL <c>numeric</c> into once it exceeds <see cref="decimal"/>'s range, which makes it a
    /// live path rather than a hypothetical one. Prefixing it would produce <c>'-42</c> — the exact
    /// corruption this narrowing exists to prevent, arriving through the one door the narrowing did not
    /// look at. <see cref="TimeSpan"/> is excluded on the same reasoning: a negative duration renders
    /// <c>-01:30:00</c>.</para>
    ///
    /// <para>The direction of the residual risk is what makes that list safe to be incomplete: a
    /// numeric type still missing from it is hardened, costing a visible spurious <c>'</c>, whereas a
    /// text type wrongly added to it would be a silent hole. So <b>add a type here only when it is a
    /// machine-rendered quantity</b> — never to silence a stray apostrophe on something that might
    /// carry client text.</para>
    ///
    /// <para><see langword="null"/> is in scope and costs nothing:
    /// <see cref="CsvValueFormatter.Format"/> maps it to the empty string, which
    /// <see cref="FormulaInjectionHardening"/> never prefixes. A <see cref="Nullable{T}"/> cell needs no
    /// arm of its own — boxing one that has a value yields a box of <c>T</c>, so it matches <c>T</c>'s
    /// pattern here, and an empty one boxes to <see langword="null"/>.</para>
    /// </summary>
    private static bool IsInHardeningScope(object? value) => value switch
    {
        bool or byte or sbyte or short or ushort or int or uint or long or ulong => false,
        decimal or double or float => false,
        Half or Int128 or UInt128 or nint or nuint or BigInteger => false,
        DateOnly or DateTime or DateTimeOffset or TimeOnly or TimeSpan => false,
        _ => true
    };

    /// <summary>
    /// RFC 4180 §2 rules 5-7, applied to one already-formatted field: a field containing the active
    /// delimiter, a double quote, CR or LF is wrapped in double quotes and its own quotes are
    /// doubled; anything else is written bare.
    ///
    /// <para><b>This is the single choke point for every character that reaches the file</b> — the
    /// header row and every data cell both come through here, which is what makes "EXP-6 covers the
    /// header row too" (EXP-1(b), <see cref="ExportColumn.Header"/>) a structural property rather
    /// than something a second call site could forget.</para>
    ///
    /// <para><b>EXP-6's <c>'</c> prefixing therefore happens here as well, BEFORE the quoting
    /// decision.</b> The two rules are independent and compose: the prefix becomes the field's first
    /// character, then RFC 4180 wraps whatever the field now is. <c>page-export/020</c> is the oracle
    /// for the composition — a value beginning with CR and embedding a quote, a comma and an LF comes
    /// out as <c>"'\rHe said ""hello"", then\nleft"</c>, with the apostrophe <b>inside</b> the opening
    /// quote. Prefixing after the decision would emit <c>'"…"</c>, which is not a CSV field at all (a
    /// bare character in an unquoted field followed by a stray quote), and would also let the prefix
    /// change whether quoting was needed — a value of exactly <c>"\r"</c> needs quotes either way, but
    /// the order must not be what decides it.</para>
    ///
    /// <para>Note that a line ending INSIDE a field is preserved exactly as the value carried it (a
    /// bare LF stays a bare LF); only the record separator is normalized to CRLF. Rewriting a value's
    /// own newlines would be the writer editing data it was asked to transport.</para>
    /// </summary>
    /// <param name="harden">Whether this field is in EXP-6's scope — <see langword="true"/> for every
    /// header, and for a data cell whatever <see cref="IsInHardeningScope"/> says, which is every value
    /// <em>except</em> a numeric, temporal or boolean one. Combined here with
    /// <see cref="CsvWriterOptions.HardenFormulaInjection"/>, which is the host's "configurable off": the
    /// scope answers <em>which</em> fields the rule covers, the option whether it runs at all, and both
    /// must hold. See the class remarks for why the scope is narrower than EXP-6's wording, and
    /// <see cref="IsInHardeningScope"/> for why it is spelled as an exclusion.</param>
    private static void AppendField(
        StringBuilder record, string text, CsvWriterOptions options, bool harden)
    {
        text = FormulaInjectionHardening.Harden(text, enabled: harden && options.HardenFormulaInjection);

        var needsQuoting = false;
        foreach (var character in text)
        {
            if (character == options.Delimiter || character is '"' or '\r' or '\n')
            {
                needsQuoting = true;
                break;
            }
        }

        if (!needsQuoting)
        {
            record.Append(text);
            return;
        }

        record.Append('"');
        foreach (var character in text)
        {
            if (character == '"')
            {
                record.Append('"');
            }

            record.Append(character);
        }

        record.Append('"');
    }
}
