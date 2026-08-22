namespace Craticula.Core.Export;

/// <summary>
/// One row of an export, as an execution adapter yields it: the cell values <b>positionally aligned
/// with the resolved <see cref="ExportColumn"/> list</b>, still as CLR values (a <see cref="decimal"/>, a
/// <see cref="DateOnly"/>, a <see langword="null"/>) rather than as text. Formatting is EXP-5's and
/// belongs to the writer, not to whoever read the row off a cursor.
///
/// <para><b>Why positional rather than a name-keyed map.</b> EXP-1(b) makes the projection an
/// <em>ordered</em> list, and every compiler renders it in exactly that order — the Postgres
/// <c>SELECT</c> list, MongoDB's <c>$project</c>. An adapter reading a
/// <c>DbDataReader</c> therefore already has the values in this order, and a dictionary would make
/// it build one lookup per row for a mapping that cannot differ. It also removes a whole failure
/// mode: a name-keyed row can silently disagree with the projection (a missing key becomes an empty
/// cell), whereas a length mismatch is caught by <c>Craticula.Export.Csv.CsvWriter</c> on the first
/// row. <see cref="FromCells"/> exists for an adapter whose driver hands back a name-keyed document
/// and has no cheaper way to resolve the column list up front, making that mapping the ONE place the
/// two representations would meet.
///
/// <para><b>#1647 found <see cref="FromCells"/> is not actually needed by every name-keyed
/// driver.</b> `Craticula.MongoDB.Driver` reads raw BSON documents — the shape `FromCells` was
/// written for — but resolves its own positional column list from the REQUEST's already-settled
/// shape (which document key each column reads from is known before any document arrives, including
/// on a zero-row result), then reads each cell by that key directly per
/// <see cref="Craticula.Core.Schema.FieldType"/>. Going through `FromCells`'s
/// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> shape would cost a dictionary allocation per
/// document for a lookup this adapter never actually needs, and would lose the per-`FieldType` typed
/// read it must do regardless. `FromCells` therefore currently has no production consumer — it
/// remains here for whichever future adapter's own column-resolution strategy genuinely needs a
/// name-keyed lookup (a description withdrawn from naming specific future adapters, since MongoDB
/// was the one this file previously named and it turned out not to need it).</para>
///
/// <para>A <see langword="struct"/> wrapping the caller's own list: constructing one allocates
/// nothing beyond what the adapter already had, which is what lets a million-row export (EXP-7)
/// stream through the writer without the writer being the thing that materializes it. An adapter
/// chunking a keyset walk (EXP-3) can therefore hand out an <see cref="ArraySegment{T}"/> over one
/// wider buffer it filled — <see cref="ArraySegment{T}"/> is an <see cref="IReadOnlyList{T}"/> —
/// without copying a cell, which is what <c>Craticula.EntityFrameworkCore</c> does.</para>
/// </summary>
/// <remarks>
/// <b>Why this lives in <c>Craticula.Core</c> rather than beside the CSV writer that consumes it —
/// moved here by #1645.</b> It is the hand-off type between an <em>execution adapter</em> (EXP-3's
/// row stream) and a <em>writer</em> (EXP-4's bytes), which is verbatim the argument
/// <see cref="ExportColumn"/>'s own remarks make for its own move in #1651: AR-3's boundary rule is
/// that dependencies point <em>into</em> the core and never sideways between adapters, so the type
/// both sides share belongs here. What kept that argument abstract until now is that only one side of
/// the seam existed — no execution adapter did. #1645 built the first one
/// (<c>Craticula.EntityFrameworkCore</c>), and an adapter that referenced <c>Craticula.Export.Csv</c>
/// merely to name its own return type would be welded to <em>one file format</em>: the same adapter
/// must be able to feed <c>Export.Xlsx</c> (roadmap §14) without either writer knowing the other
/// exists, and an execution adapter carrying a driver dependency (AR-3's exception) must not drag
/// that driver into a writer's dependency graph either.
///
/// <para>The alternative — an adapter yielding a bare
/// <c>IAsyncEnumerable&lt;IReadOnlyList&lt;object?&gt;&gt;</c> that each host re-wraps — was rejected
/// because the BCL has no <c>Select</c> over <see cref="IAsyncEnumerable{T}"/>: every host of every
/// adapter would hand-write the same <c>await foreach … yield return new ExportRow(cells)</c> pump,
/// once per adapter, to bridge two types that differ by nothing. The move costs no call site: every
/// file that names this type already imported this namespace for <see cref="ExportColumn"/>.</para>
/// </remarks>
public readonly struct ExportRow
{
    private readonly IReadOnlyList<object?>? values;

    /// <summary>Wraps <paramref name="values"/> as one row, in the resolved column list's order. The
    /// list is not copied — it may be reused by the caller once the row has been written.</summary>
    public ExportRow(IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        this.values = values;
    }

    /// <summary>The number of cells, compared against the column count on every row.</summary>
    public int Count => values?.Count ?? 0;

    /// <summary>The cell at <paramref name="index"/>, unformatted.</summary>
    public object? this[int index] => values is null
        ? throw new InvalidOperationException("This ExportRow was never initialized with any values.")
        : values[index];

    /// <summary>
    /// Projects a name-keyed document onto <paramref name="columns"/>, in that order. A colId absent
    /// from <paramref name="cells"/> yields <see langword="null"/> — an adapter whose driver omits
    /// missing fields (MongoDB does) is the reason this is a lookup rather than a length check, and
    /// "absent" and "stored null" are the same empty cell in a CSV file either way.
    /// </summary>
    public static ExportRow FromCells(IReadOnlyDictionary<string, object?> cells, IReadOnlyList<ExportColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(columns);

        var projected = new object?[columns.Count];
        for (var index = 0; index < columns.Count; index++)
        {
            projected[index] = cells.TryGetValue(columns[index].ColId, out var value) ? value : null;
        }

        return new ExportRow(projected);
    }
}
