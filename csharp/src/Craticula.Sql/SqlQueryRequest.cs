using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Sql;

/// <summary>
/// Everything <see cref="SqlQueryBuilder.Build"/> needs to compile one leaf query: the host's
/// <see cref="GridSchema"/> plus the already-validated IR pieces a request resolved to
/// (<c>Craticula.AgGrid.AgGridRequestParser</c> produces the IR half,
/// <see cref="GridFilterPipeline.Process"/> produces <see cref="Filter"/>).
/// </summary>
public sealed record SqlQueryRequest
{
    /// <summary>SC-1..SC-5: the field allowlist, physical mappings, <see cref="GridSchema.Source"/>
    /// (SQL-0's <c>FROM</c> target) and <see cref="GridSchema.StableKey"/> (SEM-6's tiebreaker).</summary>
    public required GridSchema Schema { get; init; }

    /// <summary>The final filter tree — merged, allowlisted, limit-checked, coerced and
    /// static-filter-injected by <see cref="GridFilterPipeline.Process"/>. <see langword="null"/>
    /// compiles to no <c>WHERE</c> clause at all.</summary>
    public FilterNode? Filter { get; init; }

    /// <summary>The request's own sort keys, before SEM-6's tiebreaker (which
    /// <see cref="SqlQueryBuilder"/> appends via <see cref="SemanticsResolver.AppendStableKeyTiebreaker"/>).
    /// <see langword="null"/> means the grid chose no sort, and the tiebreaker alone orders the
    /// result.</summary>
    public IReadOnlyList<SortItem>? Sort { get; init; }

    public required PageSpec Page { get; init; }

    /// <summary>
    /// BM-2's row-group state, and the switch <see cref="SqlQueryBuilder"/> dispatches on (GRP-1):
    /// <see langword="null"/> compiles the plain leaf query, fewer <see cref="GroupSpec.GroupKeys"/>
    /// than <see cref="GroupSpec.GroupCols"/> compiles the grouped level, and equal counts compile
    /// the drill-down leaf. Setting it changes more than the <c>GROUP BY</c>: a grouping request
    /// projects named columns at every level (GRP-3) and folds no case on its own keys (GRP-2), so
    /// it is not a decoration on the ungrouped query.
    ///
    /// <para>A caller that holds group state but wants the plain rows anyway must pass
    /// <see langword="null"/> here. That is a real case — <c>spec/fixtures/page-export/010</c> and
    /// <c>/011</c> carry <c>rowGroupCols</c> and still compile to a flat export, because EXP-1's
    /// <c>exportScope: "all"</c> discards the grouping — and it belongs to the export feature, which
    /// owns that decision, not to this builder.</para>
    ///
    /// <para><b>This is client-controlled input and must reach here already allowlisted</b>, exactly
    /// like <see cref="Filter"/>: pass it (and <see cref="Aggregations"/>) to
    /// <see cref="GridFilterPipeline.Process"/> alongside the filter tree. That is what applies SC-1,
    /// SC-2's <c>groupable</c>/<c>aggregatable</c> capability flags, and — the one nobody expects —
    /// SEC-3's leaf budget over the <c>groupKeys</c> drill-down, which <c>groupSpec</c> has no
    /// <c>maxItems</c> of its own to bound. This builder assumes all three, the same precondition
    /// <see cref="Filter"/> already carries.</para>
    /// </summary>
    public GroupSpec? Group { get; init; }

    /// <summary>
    /// AR-8's <c>valueCols</c>. Compiled into the grouped level's projection alongside the group
    /// column, and ignored on the drill-down leaf and on an ungrouped request, both of which project
    /// rows rather than aggregates. <see langword="null"/> is legal on a grouped request too: AG Grid
    /// sends one whenever the user groups without adding an aggregation, and the answer is the bucket
    /// list alone. Client-controlled, and subject to the same allowlist precondition as
    /// <see cref="Group"/> — see its remarks.
    /// </summary>
    public IReadOnlyList<Aggregation>? Aggregations { get; init; }
}
