namespace Craticula.EntityFrameworkCore;

/// <summary>
/// One page of grid rows, as <see cref="EfCoreGridQuery.FetchPageAsync{TRow}"/> returns it.
///
/// <para><b>This is deliberately NOT OQ-8's shared response envelope, and #1645 deliberately does not
/// design one.</b> #1622's definition of done wants an AG Grid <c>LoadSuccessParams</c>-shaped result
/// backed by one neutral core type, shared by all four execution adapters. No such type exists, no
/// story has built it, and inventing it inside the first adapter is how a contract gets set in the
/// wrong package and then has to be undone — the same reasoning that put
/// <see cref="Craticula.Core.Export.ExportColumn"/> in the core rather than beside the grid adapter
/// that resolves it. So this type is the narrowest thing one real consumer needs, it lives in this
/// package rather than in <c>Craticula.Core</c>, and the shape it turned out to need is reported to
/// #1622 rather than promoted.</para>
///
/// <para><b>Why the rows are positional and come with their column list</b> rather than being a list of
/// <c>Dictionary&lt;string, object?&gt;</c>. It is <see cref="Craticula.Core.Export.ExportRow"/>'s own
/// argument, for the same reason: the projection is an ORDERED list (EXP-1(b)), the adapter already has
/// the values in that order off the reader, and a per-row dictionary would build one lookup table per
/// row for a mapping that cannot differ between rows. A host that wants AG Grid's
/// object-per-row shape zips <see cref="Columns"/> with each entry, once, where it also decides its
/// JSON naming; a host streaming to a CSV writer never needs the objects at all. What a name-keyed row
/// would additionally cost is a failure mode: a missing key reads as an empty cell, whereas a length
/// mismatch is arithmetic.</para>
/// </summary>
public sealed record EfCoreGridPage
{
    /// <summary>The logical colIds each row's cells are positionally aligned with, in emission order —
    /// <c>EfCoreQueryBuilder.ResolveProjection</c>'s answer for this request.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>The page's rows, each aligned with <see cref="Columns"/>, cells as native CLR values
    /// (never a driver wrapper — see <c>EfCoreQueryBuilder.ApplyProjection</c>).</summary>
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }

    /// <summary>
    /// The number of rows matching the filter with the row window removed — AG Grid SSRM's
    /// <c>rowCount</c>, which lets the grid size its scrollbar and stop requesting blocks.
    /// <see langword="null"/> when the caller did not ask for it, which is distinct from <c>0</c>: the
    /// grid's own contract treats an unknown total as "keep asking", and a wrong zero would empty it.
    ///
    /// <para>Opt-in rather than always computed, because it is a second round trip and a second
    /// aggregate scan on every block a scrolling user pulls. A grid needs it once per filter change, not
    /// once per page.</para>
    /// </summary>
    public long? RowCount { get; init; }
}
