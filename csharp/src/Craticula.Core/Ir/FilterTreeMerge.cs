namespace Craticula.Core.Ir;

/// <summary>
/// AR-5/OQ-11: the single generic tree-merge primitive reused by both merge sites pinned in
/// <c>docs/architecture/c4-components-core-ir.md</c> — the pre-allowlist Custom Filter Merge
/// (<see cref="CustomFilterMerge"/>) and the post-coercion static filter injection
/// (<see cref="Craticula.Core.Schema.StaticFilterInjector"/>). Matches
/// <c>spec/fixtures/filter-tree/README.md</c>'s pinned 3-rule merge convention exactly.
/// </summary>
public static class FilterTreeMerge
{
    /// <summary>
    /// Logically ANDs <paramref name="a"/> and <paramref name="b"/>: both present ->
    /// <c>AndNode([a, b])</c> with <paramref name="a"/> nested as one opaque child (never flattened,
    /// even when <paramref name="a"/> is itself an <see cref="AndNode"/>); only one present -> that
    /// one, unwrapped (no spurious single-child <see cref="AndNode"/>); neither ->
    /// <see langword="null"/>.
    /// </summary>
    public static FilterNode? And(FilterNode? a, FilterNode? b) => (a, b) switch
    {
        (null, null) => null,
        ({ } left, null) => left,
        (null, { } right) => right,
        ({ } left, { } right) => new AndNode { Nodes = [left, right] }
    };

    /// <summary>
    /// OQ-11: merges a native filter tree (<paramref name="native"/>, built from AG Grid's own
    /// <c>filterModel</c>) with a client-supplied <c>customFilters</c> fragment
    /// (<paramref name="customFilters"/>), via <see cref="And"/>. Run before allowlist/limits
    /// validation, per <c>docs/architecture/c4-components-core-ir.md</c>'s Custom Filter Merge
    /// component, so the combined tree is checked as one unit. Both sides are validated through
    /// <see cref="FilterNodeValidator.Validate"/> first — defense in depth, the same precedent as
    /// <see cref="Craticula.Core.Schema.GridSchema.Parse"/>/<see cref="Craticula.Core.Schema.ResourceLimitsValidator"/>.
    /// </summary>
    public static FilterNode? CustomFilterMerge(FilterNode? native, FilterNode? customFilters)
    {
        FilterNodeValidator.Validate(native);
        FilterNodeValidator.Validate(customFilters);

        return And(native, customFilters);
    }
}
