using Craticula.Core.Ir;

namespace Craticula.Core.Schema;

/// <summary>
/// SC-1/SEC-1: the field allowlist check every colId reference (filter, sort, group, aggregation)
/// must pass before anything else runs. Implemented purely via <see cref="GridSchema.Fields"/>
/// dictionary lookups — no reflection over <see cref="GridSchema"/>'s own properties, and no
/// "map everything" fallback path of any kind.
///
/// <para><b>Two entry points, because two kinds of colId reference exist and they must fail
/// differently.</b> <see cref="Validate"/>/<see cref="Strip"/> handle a <em>filter</em> colId, which
/// <see cref="GridSchemaMode.Lenient"/> may silently drop. <see cref="ValidateStructuralColumns"/>
/// handles the colIds that define what the answer IS — a <c>groupSpec</c>'s columns and an
/// aggregation's field — which no mode may drop, and which additionally have to satisfy SC-2's
/// per-field <see cref="FieldConfig.Groupable"/>/<see cref="FieldConfig.Aggregatable"/>
/// capability flags. See that method for the reasoning.</para>
/// </summary>
public static class FieldAllowlistValidator
{
    /// <summary>
    /// Checks <paramref name="colIds"/> against <paramref name="schema"/>'s field allowlist.
    /// In <see cref="GridSchemaMode.Strict"/>, throws <see cref="UnknownFieldException"/> naming
    /// every unlisted colId found (all of them, not just the first). In
    /// <see cref="GridSchemaMode.Lenient"/>, returns the allowed/stripped split without throwing —
    /// callers walking a <see cref="Ir.FilterNode"/> tree must then apply that split via
    /// <see cref="Strip"/>; this method alone never mutates a tree.
    /// </summary>
    public static AllowlistResult Validate(GridSchema schema, IEnumerable<string> colIds)
    {
        var allowed = new List<string>();
        var stripped = new List<string>();

        foreach (var colId in colIds)
        {
            if (schema.Fields.ContainsKey(colId))
            {
                allowed.Add(colId);
            }
            else
            {
                stripped.Add(colId);
            }
        }

        // Default-deny: throw on anything other than an explicit Lenient opt-out, rather than
        // only on an exact Strict match. SC-1's [DEFAULT] is Strict — a mode value that is
        // neither Strict nor Lenient (unreachable today now that GridSchemaJson.Options rejects
        // out-of-range enum values, but this is the allowlist's own defense-in-depth) must fail
        // closed, not silently strip.
        if (schema.Mode != GridSchemaMode.Lenient && stripped.Count > 0)
        {
            throw new UnknownFieldException(stripped);
        }

        return new AllowlistResult(allowed, stripped);
    }

    /// <summary>
    /// SC-1/SC-2 for the colIds that define the <em>shape of the answer</em> rather than which rows
    /// it contains: <paramref name="group"/>'s <see cref="GroupSpec.GroupCols"/> and each
    /// <see cref="Aggregation.Field"/> in <paramref name="aggregations"/>. Every one must be present
    /// in <paramref name="schema"/>'s allowlist AND declared capable of the use it is being put to —
    /// <see cref="FieldConfig.Groupable"/> for a group column,
    /// <see cref="FieldConfig.Aggregatable"/> containing the requested
    /// <see cref="Aggregation.Func"/> for an aggregation. Reports every violation found, not just the
    /// first, matching this file's own collect-then-throw shape.
    ///
    /// <para><b>Lenient mode does not apply here, deliberately.</b> SC-1's Lenient contract is that
    /// an unknown <em>condition</em> silently vanishes; a condition that vanishes only widens the
    /// result set, which is a trade a host can knowingly accept. Neither of these references is a
    /// condition. Dropping a <c>groupCol</c> changes which level the response describes, and dropping
    /// a consumed <c>groupKey</c> along with it returns a <b>different bucket's rows under the label
    /// the client asked for</b> — the very failure GRP-1 rejects "more keys than columns" to prevent,
    /// arrived at silently instead. Dropping an aggregation is no better: the grid asked for a column
    /// of numbers keyed by <c>alias ?? field</c> and would get a response missing that key, i.e. a
    /// column of blanks indistinguishable from genuine nulls. This is the same structural reason
    /// <see cref="Strip"/> refuses to leave a dangling <c>Not(nothing)</c> behind — silence is only
    /// acceptable while the result is still an honest answer to the question that was asked.</para>
    ///
    /// <para>The capability flags are likewise mode-independent: <c>groupable: false</c> is a host
    /// policy statement about a field that IS allowlisted, so there is no "unknown colId" for Lenient
    /// to have an opinion about. Both failures surface as <see cref="UnknownFieldException"/> so a
    /// host maps them onto the one <c>rejectedAt: "allowlist"</c> stage
    /// <c>spec/fixtures/adversarial/003-unknown-colid-row-group</c> pins.</para>
    /// </summary>
    public static void ValidateStructuralColumns(
        GridSchema schema, GroupSpec? group, IReadOnlyList<Aggregation>? aggregations)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var rejected = new List<string>();

        foreach (var colId in group?.GroupCols ?? [])
        {
            if (!schema.Fields.TryGetValue(colId, out var config))
            {
                rejected.Add($"{colId} (rowGroupCols: not present in GridSchema.fields)");
            }
            else if (!config.Groupable)
            {
                rejected.Add($"{colId} (rowGroupCols: declared groupable:false)");
            }
        }

        foreach (var aggregation in aggregations ?? [])
        {
            if (!schema.Fields.TryGetValue(aggregation.Field, out var config))
            {
                rejected.Add($"{aggregation.Field} (valueCols: not present in GridSchema.fields)");
            }
            else if (!config.Aggregatable.Contains(aggregation.Func))
            {
                rejected.Add(
                    $"{aggregation.Field} (valueCols: '{aggregation.Func.ToString().ToLowerInvariant()}' is not in " +
                    $"its declared aggregatable set)");
            }
        }

        if (rejected.Count > 0)
        {
            throw new UnknownFieldException(rejected);
        }
    }

    /// <summary>
    /// SC-1's Lenient-mode companion to <see cref="Validate"/>: removes every
    /// <see cref="Ir.ComparisonNode"/> referencing a colId in <paramref name="strippedFields"/> from
    /// <paramref name="node"/>. A <see cref="Ir.CompositeFilterNode"/> that loses all its children
    /// this way disappears too (returns <see langword="null"/>, not an empty node — an
    /// <c>ir.schema.json</c> composite requires <c>minItems:1</c>); one that loses down to a single
    /// child is unwrapped rather than left as a spurious 1-child And/Or (matching
    /// <see cref="FilterTreeMerge"/>'s own "no spurious single-child wrapper" convention). A
    /// <see cref="Ir.NotNode"/> whose wrapped condition disappears entirely also disappears — "the
    /// unknown condition silently vanishes" (SC-1) must not turn into "the unknown condition's
    /// negation now matches everything", which is what leaving a dangling <c>Not(nothing)</c> or
    /// substituting an always-false sentinel would do. Only meaningful in
    /// <see cref="GridSchemaMode.Lenient"/> — <see cref="Validate"/> already throws before this could
    /// run in <see cref="GridSchemaMode.Strict"/> whenever there is anything to strip.
    /// </summary>
    public static FilterNode? Strip(FilterNode? node, IReadOnlyCollection<string> strippedFields)
    {
        if (node is null || strippedFields.Count == 0)
        {
            return node;
        }

        var strippedSet = strippedFields as IReadOnlySet<string> ?? new HashSet<string>(strippedFields);
        return StripNode(node, strippedSet);
    }

    private static FilterNode? StripNode(FilterNode node, IReadOnlySet<string> strippedFields)
    {
        switch (node)
        {
            case CompositeFilterNode composite:
            {
                var kept = new List<FilterNode>(composite.Nodes.Count);
                foreach (var child in composite.Nodes)
                {
                    var strippedChild = StripNode(child, strippedFields);
                    if (strippedChild is not null)
                    {
                        kept.Add(strippedChild);
                    }
                }

                return kept.Count switch
                {
                    0 => null,
                    1 => kept[0],
                    _ => composite with { Nodes = kept }
                };
            }

            case NotNode not:
            {
                var inner = StripNode(not.Node, strippedFields);
                return inner is null ? null : not with { Node = inner };
            }

            case ComparisonNode comparison:
                return strippedFields.Contains(comparison.Field) ? null : comparison;

            default:
                throw new NotSupportedException($"Unknown FilterNode type '{node.GetType()}'.");
        }
    }
}
