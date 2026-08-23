namespace Craticula.OpenSearch.Client;

/// <summary>
/// One page of grid rows, as <see cref="OpenSearchGridQuery.FetchPageAsync"/> returns it.
///
/// <para><b>This is deliberately NOT OQ-8's shared response envelope.</b> #1622's definition of done
/// wants an AG Grid <c>LoadSuccessParams</c>-shaped result backed by one neutral core type, shared by
/// all four execution adapters. That shared type now exists — <see cref="Craticula.Core.Grid.GridPageResult"/>,
/// added by #1649 — and this adapter's own <c>OpenSearchGridPageExtensions.ToGridPageResult()</c>
/// converts this type into it; a host wanting AG Grid's actual wire shape calls that, then
/// <c>Craticula.AgGrid.LoadSuccessParamsMapper.Map</c>. This type itself stays exactly what #1648 built:
/// the narrowest thing this package's own two real consumers (a grid page, <c>CsvWriter</c> via
/// <see cref="OpenSearchExportStream"/>) need.</para>
///
/// <para><b>Columns are always <c>Schema.FieldOrder</c> for an ordinary row query — never a caller-
/// selected subset.</b> Unlike <c>MongoQueryRequest</c>/<c>SqlQueryRequest</c>,
/// <see cref="Craticula.Search.SearchQueryRequest"/> carries no <c>Projection</c> member at all: OSE-13
/// states "no <c>_source</c> filtering exists on this backend" as a COMPILER-level fact, and that fact
/// runs one layer deeper than compiler output — the IR this adapter is handed carries no projection to
/// even read. This is a genuine, documented capability gap against the three SQL-shaped backends (whose
/// <c>FetchPageAsync</c> doubles as the page-scoped export entry point with an arbitrary narrower column
/// list), not an oversight of this story: <c>Craticula.Search.Tests.SearchFixtureCompiler.Compile</c>
/// (#1644's own conformance harness) independently confirms the same thing by never referencing
/// <c>AgGridParsedRequest.Export</c> when building a <see cref="Craticula.Search.SearchQueryRequest"/>.
/// For a grouped level, <see cref="Columns"/> is instead <c>[groupColumn, ...aggregations.Select(a =&gt;
/// a.OutputName)]</c> (GRP-3), mirroring every sibling's identical rule.</para>
///
/// <para><b>Why the rows are positional and come with their own column list</b> rather than being a
/// list of <c>Dictionary&lt;string, object?&gt;</c> — <see cref="Craticula.Core.Export.ExportRow"/>'s
/// own argument, restated: the projection is an ORDERED list (EXP-1(b)), this adapter already has the
/// values in that order (having resolved which document key each column reads from), and a per-row
/// dictionary would build one lookup table per row for a mapping that cannot differ between rows.</para>
///
/// <para><b><see cref="LastRow"/> — the first sibling type that needs it.</b> EF/Dapper/Mongo all
/// default to AG-3's <c>Exact</c> strategy (an upfront <c>COUNT</c>/<c>$count</c>) and never built a
/// <c>Lazy</c> short-page detector; this backend defaults to <c>Lazy</c> instead (the TRD's own DoD:
/// "<c>lastRow</c> strategy defaults to <c>Lazy</c> per AG-3"), which fetches one row/bucket MORE than
/// requested and reports the page's own END from whether that extra one came back — cheaper than a
/// second round trip on every block a scrolling user pulls, at the cost of one extra row/bucket per
/// fetch instead. This is a <see cref="bool"/>, not a numeric row count: whether THIS page is the
/// dataset's last one, leaving the arithmetic that turns it into AG Grid's own numeric <c>lastRow</c>
/// field (<c>startRow + rows.Count</c> when <see langword="true"/>, otherwise unknown) to whichever
/// host maps this type into the wire response — #1649's job, not this one's.</para>
/// </summary>
public sealed record OpenSearchGridPage
{
    /// <summary>The logical colIds each row's cells are positionally aligned with — see this type's own
    /// remarks for why this is always <c>Schema.FieldOrder</c> on an ordinary row query.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>The page's rows, each aligned with <see cref="Columns"/>, cells as native CLR values
    /// (never a raw <see cref="System.Text.Json.JsonElement"/> — see <see cref="OpenSearchValueReader"/>).
    /// Already trimmed to the requested page size — the AG-3 Lazy "fetch one extra, then trim" probe row
    /// never reaches this list.</summary>
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }

    /// <summary>
    /// The number of rows/buckets matching the filter with the row window removed — AG Grid SSRM's
    /// <c>rowCount</c>. <see langword="null"/> in TWO distinct cases, both meaning "unknown, keep
    /// asking" rather than "zero" or "exactly some specific number" — a wrong zero would empty the grid
    /// outright, and the grid's own contract already treats an unknown total this way:
    ///
    /// <list type="number">
    /// <item><description>The caller did not ask for it (<c>includeRowCount: false</c>) — the ordinary,
    /// zero-cost case.</description></item>
    /// <item><description>The caller DID ask for it, but the true count exceeds the capped
    /// <c>track_total_hits</c> threshold below (<c>hits.total.relation != "eq"</c>) — the count that
    /// came back is the CAP, not the real total, and reporting it as exact would silently stop the grid
    /// short of its real row count right at PERF-2's own deep-paging boundary.</description></item>
    /// </list>
    ///
    /// <para><b>A host wiring this into a response must not assume <c>includeRowCount: true</c> always
    /// yields a non-null result</b> — case 2 above is exactly why <see cref="OpenSearchGridQuery.CountRowsAsync"/>
    /// returns <see langword="null"/> rather than the clamped value once <c>relation</c> stops being
    /// <c>"eq"</c>.</para>
    ///
    /// <para><b>Ungrouped: <c>track_total_hits</c>, CAPPED (PERF-3) — never the uncapped
    /// <see langword="true"/>.</b> This backend defaults <see cref="LastRow"/>'s strategy to
    /// <c>Lazy</c>, so a caller asking for <see cref="RowCount"/> here is deliberately opting INTO the
    /// <c>Exact</c> path every other adapter defaults to — inverted, not skipped. The cap reused is
    /// <see cref="OpenSearchDeepPagingOptions.MaxResultWindow"/>: PERF-3 asks only that the count be
    /// bounded rather than an unconditional full scan, and the row-level deep-paging threshold is
    /// already the natural, host-configurable bound this adapter carries — introducing a SECOND,
    /// independent cap would be one more knob answering the same question.</para>
    ///
    /// <para><b>Grouped: an approximate <c>cardinality</c> aggregation on the group column, a documented
    /// divergence from the other three backends' exact bucket count.</b> A <c>composite</c> aggregation
    /// has no cheap way to learn its own total bucket count without walking every page (defeating the
    /// point of paging it at all) or an unbounded <c>terms</c> aggregation (PERF-3's own concern, the
    /// grouped-level mirror of the ungrouped cap above). <c>cardinality</c> is HyperLogLog++-based —
    /// exact (with very high probability) up to its default <c>precision_threshold</c> of 3,000 distinct
    /// values, approximate beyond it — which is this backend's own honestly-recorded trade-off in the
    /// same register as AGG-5's <c>missing_bucket</c> ordering divergence: bounded cost, near-exact under
    /// the threshold, degrades gracefully beyond it, rather than an unbounded exact answer this
    /// aggregation type structurally cannot give.</para>
    /// </summary>
    public long? RowCount { get; init; }

    /// <summary>AG-3's <c>Lazy</c> strategy: <see langword="true"/> when this page is the dataset's
    /// last one (fewer rows/buckets came back than the one-extra probe asked for) — see this type's own
    /// remarks.</summary>
    public bool LastRow { get; init; }
}
