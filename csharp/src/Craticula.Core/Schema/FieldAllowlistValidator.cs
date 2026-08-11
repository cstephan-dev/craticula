using Craticula.Core.Ir;

namespace Craticula.Core.Schema;

/// <summary>
/// SC-1/SEC-1: the field allowlist check every colId reference (filter, sort, group, aggregation)
/// must pass before anything else runs. Implemented purely via <see cref="GridSchema.Fields"/>
/// dictionary lookups — no reflection over <see cref="GridSchema"/>'s own properties, and no
/// "map everything" fallback path of any kind.
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
