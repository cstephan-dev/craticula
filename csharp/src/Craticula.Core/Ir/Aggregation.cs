using System.Text.Json.Serialization;

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

    /// <summary>
    /// GRP-4: the name this aggregate's value is published under — <see cref="Alias"/> when the
    /// adapter set one, and otherwise the compiler default <c>&lt;field&gt;_&lt;func&gt;</c>
    /// (<c>amount_sum</c>, <c>id_count</c>) that the fixtures pin. It names the aggregate in a
    /// <c>SELECT</c> list, as a <c>$group</c> accumulator key and as an Elasticsearch sub-aggregation,
    /// and it is the key the grid reads the number back out of.
    ///
    /// <para><b>Why the rule lives on this type rather than in a compiler.</b> It is normative in
    /// <c>spec/semantics.md</c>'s GRP-4 — "stated here because it lands in query text as an identifier
    /// and every port must choose the same one" — and it is a function of <see cref="Field"/> and
    /// <see cref="Func"/> and nothing else. All four consumers (the SQL, Mongo and Search compilers,
    /// and the EF Core adapter) need exactly this name, so it belongs where the data it is derived
    /// from lives; three private copies of the expression were what #1885 removed.</para>
    ///
    /// <para><b>It is a name, not a validated identifier.</b> <see cref="AggregationValidator"/>
    /// holds an explicit <see cref="Alias"/> to <c>ir.schema.json</c>'s identifier pattern, but
    /// <c>$defs/fieldName</c> carries no pattern at all, so a nested colId such as
    /// <c>customer.name</c> composes a default <c>customer.name_sum</c> no validator has ever seen.
    /// Whether that matters is a backend fact rather than a shared one — Elasticsearch reads an
    /// aggregation name through a <c>buckets_path</c> mini-language in which <c>&gt;</c> and <c>.</c>
    /// are syntax, so <c>SearchQueryBuilder</c> re-checks the default it gets from here, while a
    /// quoted SQL alias and a <c>$group</c> key have no such grammar. So this property composes; it
    /// deliberately does not judge.</para>
    /// </summary>
    [JsonIgnore]
    public string OutputName => Alias ?? $"{Field}_{Func.ToString().ToLowerInvariant()}";

    /// <summary>
    /// AGG-2's <c>ColumnVO.id</c>, reconstructed — <see cref="Alias"/> when there is one, otherwise
    /// <see cref="Field"/>. This is what a sort key naming an aggregated column is matched against
    /// (GRP-4's first bullet): the grid sends the <em>column's</em> id in <c>sortModel</c>, and AGG-2
    /// sets <see cref="Alias"/> from that id exactly when it differs from <see cref="Field"/>, so
    /// inverting that one-line rule is the only way back to the id the client actually sent.
    ///
    /// <para><b>Not the same value as <see cref="OutputName"/>, and the difference is the whole
    /// point.</b> Both collapse to <see cref="Alias"/> when one is set, so an aliased aggregation
    /// makes them equal and hides the distinction; with no alias they diverge —
    /// <see cref="OutputName"/> is <c>amount_sum</c> while this is <c>amount</c>. That is correct in
    /// both directions: the grid's column id for an unaliased aggregation IS the bare field (AG Grid
    /// defaults <c>ColumnVO.id</c> to <c>ColumnVO.field</c>), while the compiled output must not be,
    /// or two aggregations on one field would collide. Matching a sort key against
    /// <see cref="OutputName"/> instead would silently reject every unaliased
    /// <c>sortModel</c> entry the grid can actually produce.</para>
    /// </summary>
    [JsonIgnore]
    public string ColumnId => Alias ?? Field;
}
