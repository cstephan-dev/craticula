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
    ///
    /// <para><b><paramref name="group"/>/<paramref name="aggregations"/> (#1640) close the one gap in
    /// "every colId reference passes the allowlist".</b> Both are as client-controlled as the filter
    /// tree — they come straight off the wire's <c>rowGroupCols</c>/<c>valueCols</c>/<c>groupKeys</c>
    /// — and before this they went from the adapter to a compiler untouched. Passing them here does
    /// four things the filter path already had: SC-1/SC-2 through
    /// <see cref="FieldAllowlistValidator.ValidateStructuralColumns"/>; SEC-3 over the tree that will
    /// actually be compiled, i.e. including the <c>groupKeys</c> drill-down
    /// (<see cref="DrillDownFilter.Synthesize"/>), since N client-supplied keys are N leaf predicates;
    /// SEM-5's declared-type check over that same drill-down
    /// (<see cref="TypeCoercionValidator.ValidateDeclaredTypes"/>), so a mistyped <c>groupKey</c> is a
    /// staged client rejection rather than a compiler precondition failure; and one place where the
    /// "may Lenient strip this?" question is answered rather than assumed. They are optional so an
    /// ungrouped caller is unaffected, but a grouped one that omits them gets none of the above —
    /// which is why <c>SqlQueryBuilder</c>'s own doc points here.</para>
    ///
    /// <para><b>Residual, deliberately left as a documented gap.</b> Nothing forces a host to pass
    /// them: the five-argument call still compiles, and a <see cref="GroupSpec"/> handed straight to a
    /// compiler bypasses everything above. Making omission a compile error would not be worth the API
    /// churn — the required <paramref name="schema"/> parameter carries the identical precondition and
    /// is likewise unenforceable. The one part of it that must NOT depend on this method being called
    /// is the amplification bound, which is why it lives in <see cref="GroupSpecValidator.MaxGroupCols"/>
    /// (a structural invariant every entry point re-runs) rather than in <see cref="ResourceLimits"/>.</para>
    ///
    /// <para><b>What is returned is still the user's filter alone</b>, deliberately: the drill-down
    /// is synthesised again by the compiler that renders it, because the two halves resolve case
    /// sensitivity by different rules (GRP-2 versus SEM-1) and a compiler handed one pre-merged tree
    /// could no longer tell them apart. <see cref="DrillDownFilter.Synthesize"/> being a pure
    /// function of the <see cref="GroupSpec"/> is what makes deriving it twice safe.</para>
    /// </summary>
    public static FilterNode? Process(
        FilterNode? nativeFilter,
        FilterNode? customFilters,
        PageSpec page,
        GridSchema schema,
        ResourceLimits limits,
        GroupSpec? group = null,
        IReadOnlyList<Aggregation>? aggregations = null)
    {
        var merged = FilterTreeMerge.CustomFilterMerge(nativeFilter, customFilters);

        // Ahead of the filter allowlist within the same stage: a group column that is not allowlisted
        // (or not groupable) means the request is asking a question this schema does not answer, and
        // reporting that before "...and one of your filters names an unknown column" names the more
        // fundamental of the two first.
        FieldAllowlistValidator.ValidateStructuralColumns(schema, group, aggregations);

        // #1872: this also enforces the per-field FieldConfig.Filterable capability flag (SC-2), not
        // just colId membership (SC-1) — a field declared filterable:false is treated exactly like an
        // unknown colId (Strict rejects, Lenient strips the condition), for the reason Validate's own
        // doc gives. BM-3's setValuesSpec.colId is held to the identical flag in
        // FieldAllowlistValidator.ValidateStructuralColumns (ProcessSetValues below), closing both
        // call sites together — a host can no longer refuse a column's set-filter values while still
        // honouring that column's own filterModel entry.
        var allowlistResult = FieldAllowlistValidator.Validate(schema, CollectFieldNames(merged));
        var allowlisted = FieldAllowlistValidator.Strip(merged, allowlistResult.Stripped);

        var drillDown = DrillDownFilter.Synthesize(group);

        // SEC-3 over the WHOLE compiled predicate, drill-down included. The merge order matches
        // GRP-5's (drill-down first) so the counted tree is the shape a compiler renders, though only
        // its depth and leaf count matter here.
        //
        // One deliberate imprecision, conservative and fail-closed: FilterTreeMerge.And mints an
        // `and` node a compiler never builds — SqlQueryBuilder.RenderWhere renders the two halves as
        // separate walks and composes the text — so a grouped request is charged one level of depth
        // it will not emit, i.e. its effective MaxDepth is 7 rather than 8. Left as is rather than
        // counted more cleverly: the two halves ARE conjoined in every backend's output, the error is
        // in the safe direction, and the alternative is a second, subtly different tree walk here.
        ResourceLimitsValidator.Validate(FilterTreeMerge.And(drillDown, allowlisted), page, limits);

        var coerced = TypeCoercionValidator.Coerce(allowlisted, schema);

        // SEM-5 over the drill-down too, in the same stage. groupKeys are as client-controlled as any
        // filter value and reach a compiler untyped-checked otherwise: SqlQueryBuilder.RequireDeclaredType
        // would then throw its "run TypeCoercionValidator (SEM-5) before compiling" CALLER-precondition
        // failure on ordinary client input, where spec/fixtures/adversarial/012-015 pin the identical
        // defect class on the filter path as a staged client rejection. Strict rather than coercing,
        // because what this method returns is the user's filter alone (see above) — a corrected group
        // key has nowhere to go, and the compiler re-synthesises from the original GroupSpec. See
        // TypeCoercionValidator.ValidateDeclaredTypes for why the group path does not need the
        // string-parsing branch in the first place.
        TypeCoercionValidator.ValidateDeclaredTypes(drillDown, schema);

        return StaticFilterInjector.Inject(coerced, schema);
    }

    /// <summary>
    /// GRP-6: the same pipeline for a BM-3 distinct-values request, with the one stage that request
    /// adds — <b>the own-field exclusion</b> — in front of it.
    ///
    /// <para>Full order: <c>customFilters merge -> structural colId (SC-1) -> own-field exclusion ->
    /// search-prefix predicate -> allowlist -> limits -> coercion -> static injection</c>. Everything
    /// from the allowlist on is <see cref="Process"/>'s own pinned sequence, unchanged, over a
    /// different tree.</para>
    ///
    /// <para><b>Why the exclusion runs first, and not after the limits check.</b> Every stage below it
    /// then validates <em>exactly</em> the tree that gets compiled — the same property #1640 wanted
    /// when it started charging the <c>groupKeys</c> drill-down against SEC-3, read the other way
    /// round. The alternative (charge the submitted model, then drop) rejects requests over conditions
    /// this query is about to throw away, and 400s a set filter for a filter the user's grid
    /// legitimately shows. Nothing is weakened by it: the dropped subtree reaches no engine, and the
    /// cost of walking it is the cost of parsing the document, which is a host's request-size
    /// concern rather than SEC-3's.</para>
    ///
    /// <para><b>The search prefix is injected before the allowlist, deliberately.</b> It is an
    /// ordinary <c>startsWith</c> predicate (<see cref="SetValuesFilter.SearchPrefixPredicate"/>), so
    /// letting it flow through the remaining stages is what gives it SEM-5 coercion and its own SEC-3
    /// leaf for free. It cannot be stripped on the way: its field is
    /// <see cref="SetValuesSpec.ColId"/>, which the structural check two lines above has already
    /// proved present in <see cref="GridSchema.Fields"/>, so
    /// <see cref="GridSchemaMode.Lenient"/> has nothing to strip it for.</para>
    ///
    /// <para><b>Order inside the <c>WHERE</c>: the user's filter leads, the prefix trails.</b> No
    /// fixture pins it (none carries both), and it is stated in GRP-6 rather than left open because
    /// the choice is visible in <c>params</c>, which is compared exactly. The filter model is the
    /// ambient state of the grid; the prefix is the narrowing the user is typing on top of it, so the
    /// statement reads in the order the two were established. (GRP-5's drill-down leads for the
    /// mirror-image reason — a group path is structural context, not a transient narrowing.)</para>
    /// </summary>
    /// <exception cref="UnknownFieldException"><see cref="SetValuesSpec.ColId"/> is not in the
    /// schema's allowlist, or (#1872) is allowlisted but declared
    /// <see cref="FieldConfig.Filterable"/><c> == false</c> — in every <see cref="GridSchemaMode"/>
    /// for both, since it is the question rather than a condition on it.</exception>
    /// <exception cref="TypeCoercionException">A <see cref="SetValuesSpec.SearchPrefix"/> was supplied
    /// against a column that is not declared <see cref="FieldType.String"/>.</exception>
    public static FilterNode? ProcessSetValues(
        FilterNode? nativeFilter,
        FilterNode? customFilters,
        SetValuesSpec setValues,
        GridSchema schema,
        ResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(setValues);
        ArgumentNullException.ThrowIfNull(schema);

        SetValuesSpecValidator.Validate(setValues);

        var merged = FilterTreeMerge.CustomFilterMerge(nativeFilter, customFilters);

        FieldAllowlistValidator.ValidateStructuralColumns(schema, group: null, aggregations: null, setValues);

        var scoped = SetValuesFilter.ExcludeOwnField(merged, setValues.ColId);
        var withPrefix = FilterTreeMerge.And(scoped, RequireTextTarget(setValues, schema));

        var allowlistResult = FieldAllowlistValidator.Validate(schema, CollectFieldNames(withPrefix));
        var allowlisted = FieldAllowlistValidator.Strip(withPrefix, allowlistResult.Stripped);

        // NonePage, and that is not a placeholder: GRP-6's cap is a ceiling on distinct values, not a
        // row window, so there is no page size for SEC-3 to check here. The cap itself is applied —
        // never rejected — by ResourceLimitsValidator.ResolveSetValuesLimit, which the compiler calls
        // for the number it renders.
        ResourceLimitsValidator.Validate(allowlisted, new NonePage(), limits);

        var coerced = TypeCoercionValidator.Coerce(allowlisted, schema);

        return StaticFilterInjector.Inject(coerced, schema);
    }

    /// <summary>
    /// SEM-7/SEM-5: a <see cref="SetValuesSpec.SearchPrefix"/> only has a defined meaning against a
    /// text column, so a prefix aimed at any other declared type is rejected here, at the
    /// <c>typeCoercion</c> stage, as the wrongly-typed request it is.
    ///
    /// <para>An explicit check rather than letting coercion catch it: most non-string types reject
    /// <c>"eu"</c> on their own, but a prefix that happens to parse (<c>"2024"</c> against an
    /// <c>int64</c> column) would coerce cleanly and then hit <c>SqlQueryBuilder</c>'s
    /// declared-type substring guard — a CALLER-precondition failure on ordinary client input, which
    /// is precisely the defect class the group-key type check beside it was added to close.</para>
    /// </summary>
    private static ComparisonNode? RequireTextTarget(SetValuesSpec setValues, GridSchema schema)
    {
        var predicate = SetValuesFilter.SearchPrefixPredicate(setValues);

        if (predicate is not null && schema.Fields[setValues.ColId].Type != FieldType.String)
        {
            throw new TypeCoercionException(
            [
                new TypeCoercionViolation(
                    setValues.ColId,
                    schema.Fields[setValues.ColId].Type,
                    $"searchPrefix stringValue('{setValues.SearchPrefix}')",
                    Reason: "uncoercible-value")
            ]);
        }

        return predicate;
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
