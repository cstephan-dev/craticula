namespace Craticula.Core.Grid;

/// <summary>
/// One page of grid rows, in the shape every execution adapter's own <c>XxxGridPage</c> already
/// converges on — the neutral core type OQ-8 asked for and that #1645/#1646/#1647/#1648 each
/// deliberately declined to invent inside their own package, naming this story (#1649) as the one that
/// would. See <c>Craticula.AgGrid.LoadSuccessParamsMapper</c> for what turns this into AG Grid's own
/// wire shape — that type lives in <c>Craticula.AgGrid</c>, not here (AR-2: this type is the neutral
/// hand-off, the wire-shaping is vendor-specific), so it is named in prose rather than linked.
///
/// <para><b>Why this lives in <c>Craticula.Core</c> rather than beside any one adapter or beside
/// <c>Craticula.AgGrid</c>.</b> <see cref="Craticula.Core.Export.ExportRow"/>'s own remarks make the
/// argument that applies here verbatim: the type both sides of a hand-off share belongs where both
/// sides can reach it without depending on each other. Every one of the four execution adapters
/// (<c>Craticula.EntityFrameworkCore</c>, <c>Craticula.Dapper</c>, <c>Craticula.MongoDB.Driver</c>,
/// <c>Craticula.OpenSearch.Client</c>) already references <c>Craticula.Core</c>, so putting the shared
/// envelope here costs no new project reference and no new dependency edge — the rejected alternative,
/// leaving each adapter to hand-write its own conversion into AG Grid's wire shape, is exactly "every
/// host of every adapter would hand-write the same pump, once per adapter" restated for a response
/// instead of a request.</para>
///
/// <para><b>Why the rows are positional and come with their own column list</b> rather than being a
/// list of <c>Dictionary&lt;string, object?&gt;</c> — <see cref="Craticula.Core.Export.ExportRow"/>'s
/// own argument, restated: every adapter's own <c>XxxGridPage</c> already materializes its rows this
/// way (EXP-1(b): the projection is an ORDERED list, and the adapter already has the values in that
/// order off its own reader/cursor), so this type mirrors that shape exactly rather than forcing a
/// second representation into existence between an adapter and this one. The transposition into AG
/// Grid's real, name-keyed wire shape happens exactly once, in
/// <c>Craticula.AgGrid.LoadSuccessParamsMapper</c> — not in each of the four adapters' own
/// conversion.</para>
/// </summary>
public sealed record GridPageResult
{
    /// <summary>The logical colIds each row's cells are positionally aligned with, in emission order —
    /// read straight off the source adapter's own <c>Columns</c> (an ordinary/leaf request's declared
    /// field order, or a grouped level's <c>[groupColumn, ...aggregations.Select(a =&gt;
    /// a.OutputName)]</c>, GRP-3).</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>The page's rows, each aligned with <see cref="Columns"/>, cells as native CLR values —
    /// read straight off the source adapter's own <c>Rows</c>, unchanged.</summary>
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }

    /// <summary>
    /// AG-3's <b>Exact</b> strategy: the number of rows/buckets matching the filter with the row window
    /// removed, when the adapter computed it (an opt-in second round trip — <c>EfCoreGridPage</c>'s,
    /// <c>DapperGridPage</c>'s and <c>MongoGridPage</c>'s own <c>RowCount</c>, and
    /// <c>OpenSearchGridPage</c>'s own inverted-default opt-in path). <see langword="null"/> means "not
    /// known", never "zero" — see <c>Craticula.AgGrid.LoadSuccessParamsMapper</c> for how this and
    /// <see cref="IsLastPage"/> resolve to AG Grid's single wire <c>lastRow</c> field when both could
    /// apply. This value wins whenever it is present AND exact; when it is present but
    /// <see cref="RowCountIsApproximate"/> and <see cref="IsLastPage"/> is also <see langword="true"/>,
    /// the exact value derivable from <see cref="IsLastPage"/> wins instead — see
    /// <c>Craticula.AgGrid.LoadSuccessParamsMapper</c> for the full precedence table.
    /// </summary>
    public long? RowCount { get; init; }

    /// <summary>
    /// AG-3's <b>Lazy</b> strategy: whether the page the adapter returned is the dataset's own last one
    /// — <c>OpenSearchGridPage.LastRow</c>'s raw boolean, carried through unchanged.
    /// <see langword="null"/> on every adapter that only implements the Exact strategy (EF Core, Dapper,
    /// MongoDB — none of them fetch a probe row, so none of them can answer this question at all,
    /// which is why it is a nullable <see cref="bool"/> here rather than defaulting to
    /// <see langword="false"/>).
    /// </summary>
    public bool? IsLastPage { get; init; }

    /// <summary>
    /// Whether <see cref="RowCount"/>, when present, is an approximation rather than an exact total —
    /// <see langword="false"/> (the default) on every path except OpenSearch's grouped <c>cardinality</c>
    /// aggregation (HyperLogLog++, inexact above roughly 3,000 distinct values —
    /// <c>OpenSearchGridQuery.CountBucketsAsync</c>'s own doc comment). See
    /// <c>Craticula.AgGrid.LoadSuccessParamsMapper</c> for the one fact this flag exists to surface: AG
    /// Grid's <c>groupLevelInfo</c>, populated only when this is <see langword="true"/>.
    /// </summary>
    public bool RowCountIsApproximate { get; init; }
}
