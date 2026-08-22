namespace Craticula.Dapper;

/// <summary>
/// One page of grid rows, as <see cref="DapperGridQuery.FetchPageAsync"/> returns it.
///
/// <para><b>This is deliberately NOT OQ-8's shared response envelope, and #1646 deliberately does not
/// design one.</b> #1622's definition of done wants an AG Grid <c>LoadSuccessParams</c>-shaped result
/// backed by one neutral core type, shared by all four execution adapters. No such type exists, no
/// story has built it, and inventing it inside this adapter is how a contract gets set in the wrong
/// package and then has to be undone — the same reasoning
/// <c>Craticula.EntityFrameworkCore.EfCoreGridPage</c> carries verbatim for the identical reason. So
/// this type is the narrowest thing this package's own two real consumers (a grid page, <c>CsvWriter</c>
/// via <see cref="DapperExportStream"/>) need, it lives in this package rather than in
/// <c>Craticula.Core</c>, and the shape it turns out to need is reported to #1622 rather than promoted.</para>
///
/// <para><b>Why the rows are positional and come with their own column list</b> rather than being a
/// list of <c>Dictionary&lt;string, object?&gt;</c> — <see cref="Craticula.Core.Export.ExportRow"/>'s
/// own argument, restated: the projection is an ORDERED list (EXP-1(b)), this adapter already has the
/// values in that order off the executed reader, and a per-row dictionary would build one lookup table
/// per row for a mapping that cannot differ between rows.</para>
/// </summary>
public sealed record DapperGridPage
{
    /// <summary>
    /// The logical colIds each row's cells are positionally aligned with, in the order the compiled
    /// statement actually returned its columns — read directly off the executed reader's own column
    /// names (<c>reader.GetName(i)</c>), never re-derived from the request.
    ///
    /// <para><c>Craticula.Sql.SqlQueryBuilder.Build</c> is what decided the aliasing (GRP-3: an ordinary
    /// row query's explicit projection, a drill-down leaf's full field list, or a grouped level's
    /// <c>[groupColumn, ...aggregations]</c>), and this package's whole point is executing whatever it
    /// emitted rather than re-deciding it — see <see cref="DapperGridQuery"/>'s own remarks on why that
    /// makes this entry point grouping-agnostic by construction.</para>
    /// </summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>The page's rows, each aligned with <see cref="Columns"/>, cells as native CLR values
    /// (never a driver wrapper type — see <see cref="DapperValueReader"/>).</summary>
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }

    /// <summary>
    /// The number of rows matching the filter with the row window removed — AG Grid SSRM's
    /// <c>rowCount</c>, which lets the grid size its scrollbar and stop requesting blocks. For a
    /// grouped level this is instead the number of BUCKETS at that level — the same wrapped
    /// <c>COUNT(*)</c> query (<see cref="DapperGridQuery.CountAsync"/>) answers both automatically,
    /// since a grouped level's own compiled rows ARE its buckets, with no special-casing required.
    /// <see langword="null"/> when the caller did not ask for it, which is distinct from <c>0</c>: the
    /// grid's own contract treats an unknown total as "keep asking", and a wrong zero would empty it.
    ///
    /// <para>Opt-in rather than always computed, because it is a second round trip on every block a
    /// scrolling user pulls. A grid needs it once per filter change, not once per page.</para>
    /// </summary>
    public long? RowCount { get; init; }
}
