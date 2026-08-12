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
    /// Carried but deliberately NOT compiled here. A request can hold group state and still need the
    /// plain leaf query — <c>spec/fixtures/page-export/010</c> and <c>/011</c> are exactly that shape
    /// — so rejecting a non-null <see cref="Group"/> would break a legitimate caller. Compiling the
    /// <c>GROUP BY</c>/aggregation/drill-down forms is BM-2's Grouping Strategy (#1640), which will
    /// dispatch on this state before delegating the leaf case here; until it exists, a caller that
    /// hands a genuinely grouped request straight to this builder gets the ungrouped query, which is
    /// why the dispatch belongs in that story rather than being half-guessed in this one.
    /// </summary>
    public GroupSpec? Group { get; init; }

    /// <summary>Carried and ignored for the same reason as <see cref="Group"/> (BM-2, #1640).</summary>
    public IReadOnlyList<Aggregation>? Aggregations { get; init; }
}
