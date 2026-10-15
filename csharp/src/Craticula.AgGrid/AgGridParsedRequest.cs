using Craticula.Core.Ir;

namespace Craticula.AgGrid;

/// <summary>
/// AR-2.1: the output of <see cref="AgGridRequestParser.Parse"/> — an AG Grid SSRM request already
/// turned into candidate IR pieces, but not yet merged/allowlisted/limited/coerced. The filter half
/// (<see cref="NativeFilter"/>, <see cref="CustomFilters"/>) feeds directly into
/// <see cref="Craticula.Core.Schema.GridFilterPipeline.Process"/>, which is the component that runs
/// <see cref="Craticula.Core.Ir.FilterTreeMerge.CustomFilterMerge"/> to combine them — this record
/// deliberately keeps them separate rather than pre-merging, per
/// <c>docs/architecture/c4-components-core-ir.md</c>'s "Custom Filter Merge" component boundary.
///
/// <para><see cref="Sort"/>/<see cref="Group"/>/<see cref="Aggregations"/> have no consuming pipeline
/// yet — the same documented-gap pattern already used for
/// <see cref="Craticula.Core.Schema.SemanticsResolver.AppendStableKeyTiebreaker(IReadOnlyList{SortItem}?, string)"/> in #1634: a future
/// compiler-facing orchestrator must allowlist their field names against a
/// <see cref="Craticula.Core.Schema.GridSchema"/> (SC-1) and check the relevant capability flags
/// (<see cref="Craticula.Core.Schema.FieldConfig.Sortable"/>/<see cref="Craticula.Core.Schema.FieldConfig.Groupable"/>/
/// <see cref="Craticula.Core.Schema.FieldConfig.Aggregatable"/>) before compiling them, the same way
/// <see cref="Craticula.Core.Schema.GridFilterPipeline"/> already does for filter field names.</para>
/// </summary>
public sealed record AgGridParsedRequest
{
    /// <summary>The filter tree built from the request's own <c>filterModel</c>. <see langword="null"/>
    /// when <c>filterModel</c> is absent, JSON <c>null</c>, or an empty object.</summary>
    public FilterNode? NativeFilter { get; init; }

    /// <summary>The <c>customFilters</c> (OQ-11) extension fragment, deserialized directly as IR —
    /// not yet merged with <see cref="NativeFilter"/>. <see langword="null"/> when absent.</summary>
    public FilterNode? CustomFilters { get; init; }

    /// <summary>The request's <c>sortModel</c>, mapped to IR sort keys with an adapter-filled
    /// <see cref="NullsPosition"/> per SEM-3. <see langword="null"/> when <c>sortModel</c> is empty
    /// or absent — never an empty list (matches <c>ir.schema.json</c>'s own canonical-form rule).</summary>
    public IReadOnlyList<SortItem>? Sort { get; init; }

    /// <summary>The request's <c>rowGroupCols</c>/<c>groupKeys</c>, or <see langword="null"/> when
    /// <c>rowGroupCols</c> is empty (the request is not grouped at all). Also <see langword="null"/>
    /// on every export request, whatever the wire sent — see <see cref="Export"/>.</summary>
    public GroupSpec? Group { get; init; }

    /// <summary>The request's <c>valueCols</c>, or <see langword="null"/> when <c>valueCols</c> is
    /// empty — never an empty list. Also <see langword="null"/> on every export request, for the same
    /// reason <see cref="Group"/> is.</summary>
    public IReadOnlyList<Aggregation>? Aggregations { get; init; }

    /// <summary>The request's <c>startRow</c>/<c>endRow</c> resolved to AG-3/EXP-2's page spec — or,
    /// on an <see cref="ExportScope.All"/> export, <see cref="NonePage"/> regardless of what those two
    /// numbers said.</summary>
    public required PageSpec Page { get; init; }

    /// <summary>
    /// EXP-1's export extension (<c>exportFormat</c>/<c>exportScope</c>/<c>exportColumns</c>), or
    /// <see langword="null"/> when the request is an ordinary row fetch. Two things follow from it
    /// being non-null, and both are already applied to the properties above:
    /// <list type="bullet">
    /// <item><description><see cref="Page"/> is <see cref="NonePage"/> under
    /// <see cref="ExportScope.All"/> (EXP-2), the request's own <c>startRow</c>/<c>endRow</c> under
    /// <see cref="ExportScope.Page"/>.</description></item>
    /// <item><description><see cref="Group"/> and <see cref="Aggregations"/> are
    /// <see langword="null"/>: EXP-1's group-export carve-out emits leaf rows under the current
    /// filter and sort, and group-structured exports are out of scope for v1
    /// (<c>page-export/010</c>, <c>/011</c>).</description></item>
    /// </list>
    /// <see cref="ExportSpec.ProjectionColumns"/> is the third consequence, and the only one a caller
    /// still has to carry: hand it to the compiler as its <c>Projection</c>.
    /// </summary>
    public ExportSpec? Export { get; init; }
}
