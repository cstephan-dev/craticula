using Craticula.Core.Ir;

namespace Craticula.Core.Schema;

/// <summary>
/// The orchestrator pinning #1633's Definition of Done: <c>customFilters merge -> allowlist ->
/// limits -> coercion -> static injection</c>, per <c>docs/architecture/c4-components-core-ir.md</c>.
/// The single entry point a compiler-facing caller uses to turn a request's native filter tree plus
/// its <c>customFilters</c> fragment into the final <see cref="FilterNode"/>, fully validated and
/// ready to compile.
///
/// <para>Known, accepted scope limit: <see cref="GridSchema.StaticFilter"/> is ANDed in AFTER the
/// SEC-3 limits check (<see cref="ResourceLimitsValidator.Validate"/>), by the pinned order above —
/// so the static fragment's own depth/leaves/in-values are never counted against
/// <see cref="ResourceLimits"/>. This is intentional (the fragment is host-authored, never
/// client-influenced, per SC-5), not a client-reachable attack path — but it does mean a host that
/// configures an oversized <see cref="GridSchema.StaticFilter"/> gets no SEC-3 backstop of its own.</para>
/// </summary>
public static class GridFilterPipeline
{
    /// <summary>
    /// Runs the full pipeline: merges <paramref name="nativeFilter"/> with
    /// <paramref name="customFilters"/> (<see cref="FilterTreeMerge.CustomFilterMerge"/>, which also
    /// structurally validates both sides), allowlists every referenced field
    /// (<see cref="FieldAllowlistValidator.Validate"/>, then <see cref="FieldAllowlistValidator.Strip"/>
    /// to actually remove any field <see cref="GridSchemaMode.Lenient"/> stripped rather than
    /// throwing on — in <see cref="GridSchemaMode.Strict"/>, <c>Validate</c> already threw above if
    /// there was anything to strip, so <c>Strip</c> is a no-op there), enforces SEC-3's resource
    /// limits (<see cref="ResourceLimitsValidator.Validate"/>), coerces every value to its field's
    /// declared type (<see cref="TypeCoercionValidator.Coerce"/>), then ANDs in the host's
    /// <see cref="GridSchema.StaticFilter"/> (<see cref="StaticFilterInjector.Inject"/>). Throws
    /// whichever stage's exception fires first — this method never continues past a failed stage,
    /// matching the pinned linear order.
    /// </summary>
    public static FilterNode? Process(
        FilterNode? nativeFilter,
        FilterNode? customFilters,
        PageSpec page,
        GridSchema schema,
        ResourceLimits limits)
    {
        var merged = FilterTreeMerge.CustomFilterMerge(nativeFilter, customFilters);

        // TODO: refine — this only checks colId membership (SC-1), not the per-field
        // FieldConfig.Filterable capability flag (SC-2): a field declared filterable:false can still
        // be filtered on. Deferred here for the same reason #1631 deferred it in the first place
        // (no fixture exercises it yet) — flagging so the gap stays a decision, not an oversight.
        var allowlistResult = FieldAllowlistValidator.Validate(schema, CollectFieldNames(merged));
        var allowlisted = FieldAllowlistValidator.Strip(merged, allowlistResult.Stripped);

        ResourceLimitsValidator.Validate(allowlisted, page, limits);

        var coerced = TypeCoercionValidator.Coerce(allowlisted, schema);

        return StaticFilterInjector.Inject(coerced, schema);
    }

    /// <summary>
    /// Walks <paramref name="node"/>, collecting every distinct field name referenced by a
    /// <see cref="ComparisonNode"/> anywhere in the tree — mirrors the recursive-switch style already
    /// used by <see cref="ResourceLimitsValidator"/>/<see cref="FilterNodeValidator"/>'s own tree
    /// walks. Assumes <paramref name="node"/> already passed <see cref="FilterNodeValidator"/>, true
    /// by construction here since <see cref="FilterTreeMerge.CustomFilterMerge"/> validates both
    /// merge inputs before this runs.
    /// </summary>
    private static IReadOnlyList<string> CollectFieldNames(FilterNode? node)
    {
        if (node is null)
        {
            return [];
        }

        var fieldNames = new HashSet<string>();
        Walk(node, fieldNames);
        return fieldNames.ToList();
    }

    private static void Walk(FilterNode node, HashSet<string> fieldNames)
    {
        switch (node)
        {
            case CompositeFilterNode composite:
                foreach (var child in composite.Nodes)
                {
                    Walk(child, fieldNames);
                }

                return;

            case NotNode not:
                Walk(not.Node, fieldNames);
                return;

            case ComparisonNode comparison:
                fieldNames.Add(comparison.Field);
                return;

            default:
                throw new NotSupportedException($"Unknown FilterNode type '{node.GetType()}'.");
        }
    }
}
