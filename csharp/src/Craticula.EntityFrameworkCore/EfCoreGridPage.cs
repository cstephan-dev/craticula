namespace Craticula.EntityFrameworkCore;

/// <summary>
/// One page of grid rows, as <see cref="EfCoreGridQuery.FetchPageAsync{TRow}"/> returns it.
///
/// <para><b>This is deliberately NOT OQ-8's shared response envelope.</b> #1622's definition of done
/// wants an AG Grid <c>LoadSuccessParams</c>-shaped result backed by one neutral core type, shared by
/// all four execution adapters. That shared type now exists — <see cref="Craticula.Core.Grid.GridPageResult"/>,
/// added by #1649 — and this adapter's own <c>EfCoreGridPageExtensions.ToGridPageResult()</c> converts
/// this type into it; a host wanting AG Grid's actual wire shape calls that, then
/// <c>Craticula.AgGrid.LoadSuccessParamsMapper.Map</c>. This type itself stays exactly what #1645 built:
/// the narrowest thing this adapter's own real consumer needs.</para>
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
    /// <c>EfCoreQueryBuilder.ResolveProjection</c>'s answer for an ordinary or drill-down-leaf request.
    /// For a grouped level (#1887) this is instead <c>[groupColumn, ...aggregations.Select(a =&gt;
    /// a.OutputName)]</c> (GRP-3) — the level has no export-style projection to resolve, since it
    /// projects a bucket key and its aggregates rather than declared fields.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>The page's rows, each aligned with <see cref="Columns"/>, cells as native CLR values
    /// (never a driver wrapper — see <c>EfCoreQueryBuilder.ApplyProjection</c>).</summary>
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }

    /// <summary>
    /// The number of rows matching the filter with the row window removed — AG Grid SSRM's
    /// <c>rowCount</c>, which lets the grid size its scrollbar and stop requesting blocks. For a
    /// grouped level (#1887) this is instead the number of BUCKETS at that level
    /// (<c>EfCoreQueryBuilder.CountGroupsAsync</c>), the grouped analogue of the same question — how
    /// many results this query answers, with its row window removed.
    /// <see langword="null"/> when the caller did not ask for it, which is distinct from <c>0</c>: the
    /// grid's own contract treats an unknown total as "keep asking", and a wrong zero would empty it.
    ///
    /// <para>Opt-in rather than always computed, because it is a second round trip and a second
    /// aggregate scan on every block a scrolling user pulls. A grid needs it once per filter change, not
    /// once per page.</para>
    /// </summary>
    public long? RowCount { get; init; }
}
