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
/// <para><b>What is deliberately NOT here: EXP-6's formula-injection hardening.</b> Prefixing a value
/// that starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or CR with <c>'</c> is #1652's, along
/// with the six <c>page-export/016</c>-<c>020</c>, <c>022</c> fixtures that pin it. The seam it needs
/// is <see cref="AppendField"/> — the single choke point every header cell and every data cell
/// passes through, before the quoting decision. See its own remarks.</para>
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
    /// why a header containing the delimiter is quoted and where EXP-6's prefixing will reach it
    /// (#1650 recorded that obligation on <see cref="ExportColumn"/> itself).
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

            AppendField(record, columns[index].Header, options);
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

            AppendField(record, CsvValueFormatter.Format(row[index], options.DecimalSeparator), options);
        }

        record.Append(RecordSeparator);
    }

    /// <summary>
    /// RFC 4180 §2 rules 5-7, applied to one already-formatted field: a field containing the active
    /// delimiter, a double quote, CR or LF is wrapped in double quotes and its own quotes are
    /// doubled; anything else is written bare.
    ///
    /// <para><b>This is the single choke point for every character that reaches the file</b> — the
    /// header row and every data cell both come through here, which is what makes "EXP-6 covers the
    /// header row too" (EXP-1(b), <see cref="ExportColumn.Header"/>) a structural property rather
    /// than something a second call site could forget. <b>#1652 adds EXP-6's <c>'</c> prefixing at
    /// the marked line below</b>, i.e. BEFORE the quoting decision: the two rules are independent and
    /// compose (<c>page-export/020</c> is prefixed AND quoted), and a prefix applied after the
    /// decision would be able to change whether quoting was needed.</para>
    ///
    /// <para>Note that a line ending INSIDE a field is preserved exactly as the value carried it (a
    /// bare LF stays a bare LF); only the record separator is normalized to CRLF. Rewriting a value's
    /// own newlines would be the writer editing data it was asked to transport.</para>
    /// </summary>
    private static void AppendField(StringBuilder record, string text, CsvWriterOptions options)
    {
        // #1652 (EXP-6): text = FormulaInjectionHardening.Harden(text, options) goes here.

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
