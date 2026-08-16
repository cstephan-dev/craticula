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
    /// <para>A caller that holds group state but wants the plain rows anyway passes
    /// <see langword="null"/> here. That is a real case — <c>spec/fixtures/page-export/010</c> and
    /// <c>/011</c> carry <c>rowGroupCols</c> and still compile to a flat export — and the decision
    /// belongs to the export feature, not to this builder: since #1650
    /// <c>Craticula.AgGrid.AgGridRequestParser</c> applies EXP-1's carve-out itself, so an export
    /// never reaches here carrying a <see cref="GroupSpec"/> at all. See <see cref="Projection"/>,
    /// which the same carve-out makes mutually exclusive with this property.</para>
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

    /// <summary>
    /// EXP-1(b): the export's output columns, in the order they must appear —
    /// <c>Craticula.AgGrid.ExportSpec.ProjectionColumns</c>, already resolved (an explicit
    /// <c>exportColumns</c>, or every <see cref="FieldConfig.Exportable"/> field in declaration
    /// order). <see langword="null"/> — the default — is an ordinary row query, which still renders
    /// <c>SELECT *</c> per GRP-3.
    ///
    /// <para>A plain list of logical field names rather than the adapter's own export type, so this
    /// compiler needs no reference to <c>Craticula.AgGrid</c>: the format, the scope and the header
    /// strings are all invisible here (a header never reaches a compiled artifact — see
    /// <c>page-export/008</c>), and the only thing the compiler is owed is which columns, in what
    /// order.</para>
    ///
    /// <para>Mutually exclusive with <see cref="Group"/>, and checked: EXP-1's carve-out discards
    /// group state on an export, so a request carrying both has already gone wrong upstream. An
    /// EMPTY list is checked too, and refused: <c>AgGridRequestParser</c> rejects an empty
    /// <c>exportColumns</c> at the wire, and rendering one here would emit <c>SELECT  FROM …</c>.</para>
    /// </summary>
    public IReadOnlyList<string>? Projection { get; init; }
}
