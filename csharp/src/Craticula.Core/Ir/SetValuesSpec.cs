namespace Craticula.Core.Ir;

/// <summary>
/// BM-3: the distinct-values request behind a set filter's value list, matching
/// <c>spec/ir.schema.json</c>'s <c>$defs/setValuesSpec</c>. It is not a variant of the row-fetch
/// query — it answers "which values does this one column take, under the filters currently in
/// effect", so it carries no sort (GRP-6 fixes the ordering to the distinct column itself), no page
/// (the cap below is a ceiling, not a window) and no aggregation.
///
/// <para><b>The wire shape it models is Craticula's own, not AG Grid's.</b> AG Grid Community has no
/// request/response type for set-filter values at all: it hands the whole job to a host-supplied
/// <c>SetFilterParams.values</c> callback. <c>spec/fixtures/group-aggregation/README.md</c> defines
/// the synthetic <c>setValuesRequest</c> envelope this record is the IR of, and
/// <c>016</c>-<c>019</c> are its artifact oracle.</para>
/// </summary>
public sealed record SetValuesSpec
{
    /// <summary>
    /// The field whose distinct values are being resolved — the <c>SELECT DISTINCT</c> column, the
    /// sole <c>ORDER BY</c> key, and the field whose own filter is dropped from the accompanying
    /// filter model (GRP-6). Client-controlled, and unlike a filter's colId it is the question rather
    /// than a condition on it, so no <see cref="Craticula.Core.Schema.GridSchemaMode"/> may drop it —
    /// see <see cref="Craticula.Core.Schema.FieldAllowlistValidator.ValidateStructuralColumns"/>.
    /// </summary>
    public required string ColId { get; init; }

    /// <summary>
    /// The set filter's own inline search box text, or <see langword="null"/> when the box is empty.
    /// Compiled into an ordinary <c>startsWith</c> predicate against <see cref="ColId"/> (GRP-6), so
    /// SEM-1's per-field mechanism and SEM-7's escaping govern it exactly as they would a user's own
    /// prefix filter — it is deliberately NOT a construct of its own.
    /// </summary>
    public string? SearchPrefix { get; init; }

    /// <summary>
    /// The client's requested cap on how many distinct values come back, or <see langword="null"/>
    /// when it asks for none. Never authoritative: the host's own
    /// <see cref="Craticula.Core.Schema.ResourceLimits.MaxSetValuesResults"/> always wins, and an
    /// absent request falls back to
    /// <see cref="Craticula.Core.Schema.ResourceLimits.DefaultSetValuesResults"/> — the resolution
    /// lives in <see cref="Craticula.Core.Schema.ResourceLimitsValidator.ResolveSetValuesLimit"/>, in
    /// one place, so no compiler re-derives it.
    /// </summary>
    public int? Limit { get; init; }
}
