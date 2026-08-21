using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.EntityFrameworkCore;

/// <summary>
/// Everything this adapter needs to compile one row query: the host's <see cref="GridSchema"/> plus
/// the already-validated IR a request resolved to. <b>Deliberately property-for-property the same
/// shape as <c>Craticula.Sql.SqlQueryRequest</c></b>, including the members this adapter refuses, so
/// that a host feeding both paths (AR-4's parity requirement, and #1645's own harness) maps one to
/// the other by copying fields and cannot lose one in the transcription.
///
/// <para>Produced from the same pipeline: <c>Craticula.AgGrid.AgGridRequestParser.Parse</c> yields the
/// IR half, <see cref="GridFilterPipeline.Process"/> yields <see cref="Filter"/>. This adapter runs
/// the IR's own structural validators again anyway (the defense-in-depth precedent every public entry
/// point in this codebase follows), but it does NOT re-run the allowlist: SC-1/SC-2 are the pipeline's,
/// and a field this adapter cannot resolve against the schema is reported as a caller precondition
/// failure rather than as a client error.</para>
/// </summary>
public sealed record EfCoreQueryRequest
{
    /// <summary>SC-1..SC-5: the field allowlist, the physical mappings this adapter resolves EF
    /// property names from, and <see cref="GridSchema.StableKey"/> — which is both SEM-6's sort
    /// tiebreaker and EXP-3's keyset chunking key.
    ///
    /// <para><see cref="GridSchema.Source"/> is <b>not</b> read here, and that is the one structural
    /// difference from the SQL compiler: the <c>FROM</c> target is whatever
    /// <see cref="System.Linq.IQueryable{T}"/> the host hands in. That is what lets EF's global query
    /// filters (tenancy, soft delete) compose — they are already on the <c>DbSet</c> — which is AR-4's
    /// entire reason for permitting this path. <see cref="GridSchemaPropertyBag.Query"/> builds the
    /// queryable from <see cref="GridSchema.Source"/> for a host that has no entity classes.</para>
    /// </summary>
    public required GridSchema Schema { get; init; }

    /// <summary>The final filter tree — merged, allowlisted, limit-checked, coerced and
    /// static-filter-injected by <see cref="GridFilterPipeline.Process"/>. <see langword="null"/>
    /// compiles to no <c>Where</c> call at all.</summary>
    public FilterNode? Filter { get; init; }

    /// <summary>The request's own sort keys, before SEM-6's tiebreaker (which
    /// <see cref="EfCoreQueryBuilder.ApplyOrdering{TRow}"/> appends via
    /// <see cref="SemanticsResolver.AppendStableKeyTiebreaker"/>). <see langword="null"/> means the
    /// grid chose no sort, and the tiebreaker alone orders the result — which on this path is load
    /// bearing twice over, since EXP-3's keyset walk needs a total order to chunk on.</summary>
    public IReadOnlyList<SortItem>? Sort { get; init; }

    /// <summary>AG-3/EXP-2. A <see cref="RangePage"/> becomes <c>Skip</c>/<c>Take</c> on the grid path
    /// and <see cref="NonePage"/> becomes no row window at all. <b>The export path ignores this
    /// property entirely</b> and refuses a <see cref="RangePage"/>, because EF compiles <c>Skip</c> to
    /// <c>OFFSET</c> and EXP-3 forbids OFFSET-walking an export — see
    /// <see cref="EfCoreExportStream"/>.</summary>
    public required PageSpec Page { get; init; }

    /// <summary>
    /// AR-8: AG Grid SSRM's row-group drill-down protocol. Compiled by
    /// <see cref="EfCoreGridQuery.FetchPageAsync{TRow}"/> since #1887 (GRP-1..GRP-6: the grouped
    /// level's projection and aggregates, GRP-2's exact bucket keys, GRP-4's tiebreaker substitution).
    /// <see cref="EfCoreQueryBuilder.ApplyFilter{TRow}"/>/<see cref="EfCoreQueryBuilder.ApplyOrdering{TRow}"/>
    /// — the per-clause entry points, reached directly rather than through the grid orchestration —
    /// still <b>refuse</b> a non-<see langword="null"/> value, naming why: those two apply a single
    /// clause each, and neither has a defined meaning for "restrict/order by group state" on its own.
    /// The member's own existence, independent of which entry point a caller reaches, is still worth
    /// stating: a host that <em>has</em> group state (every <c>AgGridParsedRequest</c> off a grouped
    /// grid does) and copies the rest of the IR across must not silently get the ungrouped leaf rows
    /// back, which is what an ABSENT member would risk.</summary>
    public GroupSpec? Group { get; init; }

    /// <summary>AR-8's <c>valueCols</c>, compiled alongside <see cref="Group"/> since #1887. Checked
    /// independently of it at the per-clause entry points (<see cref="EfCoreQueryBuilder.ApplyFilter{TRow}"/>/
    /// <see cref="EfCoreQueryBuilder.ApplyOrdering{TRow}"/> refuse a non-empty value even with no
    /// <see cref="Group"/> set), because AG Grid can send <c>valueCols</c> with no <c>rowGroupCols</c>
    /// at all (pivot mode, or a grid whose user added an aggregation before grouping), and silently
    /// dropping the aggregates there would return leaf rows where the caller asked for totals.</summary>
    public IReadOnlyList<Aggregation>? Aggregations { get; init; }

    /// <summary>
    /// EXP-1(b): the export's output columns, in the order they must appear —
    /// <c>Craticula.AgGrid.ExportSpec.ProjectionColumns</c>, already resolved. A plain list of logical
    /// field names rather than the adapter's own export type, for the reason
    /// <c>SqlQueryRequest.Projection</c> gives: headers, format and scope are invisible to anything
    /// that builds a query.
    ///
    /// <para>Where the two paths differ: a compiler renders <see langword="null"/> as GRP-3's
    /// <c>SELECT *</c>, which returns the <em>physical</em> column names. This adapter has no such
    /// fallback available, because a projection is how it produces rows at all (see
    /// <see cref="EfCoreQueryBuilder.ApplyProjection{TRow}"/>), so <see langword="null"/> resolves to
    /// <see cref="GridSchema.FieldOrder"/> — every declared field in declaration order, under its
    /// LOGICAL name. That is GRP-3's own rule for a grouping request's leaf, applied here for the same
    /// reason it exists there: <c>SELECT *</c> hands back column names a grid does not know.</para>
    ///
    /// <para>An EMPTY list is refused, as both compilers refuse it: it is not "project nothing"
    /// (nothing is not a row), and <c>AgGridRequestParser</c> already rejects an empty
    /// <c>exportColumns</c> at the wire.</para>
    /// </summary>
    public IReadOnlyList<string>? Projection { get; init; }
}
