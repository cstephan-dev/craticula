namespace Craticula.Core.Ir;

/// <summary>
/// AR-8: AG Grid SSRM's row-group drill-down protocol, matching <c>spec/ir.schema.json</c>'s
/// <c>$defs/groupSpec</c>. <see cref="GroupKeys"/>.Count &lt; <see cref="GroupCols"/>.Count compiles
/// a grouped query (the next not-yet-consumed group column, plus aggregations, filtered by the
/// already-consumed keys); equal length compiles the leaf query (BM-2) — that comparison is a
/// compiler concern, not something this record enforces itself.
/// </summary>
public sealed record GroupSpec
{
    /// <summary>Ordered list of row-group column field names, as configured on the grid.</summary>
    public required IReadOnlyList<string> GroupCols { get; init; }

    /// <summary>
    /// Group key values already consumed by the drill-down so far, one per already-expanded group
    /// level, in <see cref="GroupCols"/> order. Each key is a <see cref="TypedValue"/> (AR-7) — a
    /// group key compiles into an equality predicate against its group column (BM-2), i.e. it is a
    /// filter value like any other, so it is typed the same way. May be empty (top-level group view,
    /// no keys consumed yet), but never null.
    /// </summary>
    public required IReadOnlyList<TypedValue> GroupKeys { get; init; }
}
