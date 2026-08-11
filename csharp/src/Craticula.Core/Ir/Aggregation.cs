namespace Craticula.Core.Ir;

/// <summary>
/// AR-8's closed v1 aggregation function set, matching <c>spec/ir.schema.json</c>'s
/// <c>$defs/aggregationFunc</c> — <c>first</c>/<c>last</c> are deliberately excluded (v2 roadmap).
/// Ir-owned (mirroring why <see cref="ComparisonOperator"/>/<see cref="SortDirection"/> are also
/// Ir-owned) since <c>Craticula.Core.Ir</c> must never depend on <c>Craticula.Core.Schema</c>
/// (dependencies point inward) — but <c>Schema</c> already depends on <c>Ir</c> extensively, so
/// <see cref="Craticula.Core.Schema.FieldConfig.Aggregatable"/> (SC-2's per-field capability list)
/// references this same type directly rather than declaring an independent copy.
/// </summary>
public enum AggregationFunc
{
    Sum,
    Min,
    Max,
    Avg,
    Count
}

/// <summary>
/// AR-8: a single (field, func) aggregation pair, matching <c>spec/ir.schema.json</c>'s
/// <c>$defs/aggregation</c>.
/// </summary>
public sealed record Aggregation
{
    public required string Field { get; init; }

    public required AggregationFunc Func { get; init; }

    /// <summary>
    /// Optional output key for the aggregated value; a <see langword="null"/> alias defers to a
    /// compiler-chosen default name derived from <see cref="Field"/>+<see cref="Func"/>. Unlike
    /// <see cref="Field"/>, an alias is not gated by the GridSchema allowlist, yet it lands in
    /// generated query text as an identifier (<c>SUM(x) AS &lt;alias&gt;</c>, a Mongo <c>$group</c>
    /// output key, an ES agg name) where SEC-2's "values are parameters" rule cannot apply — see
    /// <see cref="AggregationValidator"/> for the identifier-safe pattern this is checked against.
    /// </summary>
    public string? Alias { get; init; }
}
