using Craticula.Core.Ir;

namespace Craticula.Core.Schema;

/// <summary>
/// SEC-3: enforces the configurable resource ceilings over an IR filter tree and page spec.
/// <see cref="Validate"/> first runs <see cref="FilterNodeValidator"/> (structural shape
/// correctness — a malformed tree is rejected before any limit is even computed, and this is what
/// makes the tree walk below safe to assume well-formed input, e.g. no null nodes). A single tree
/// walk (<see cref="Walk"/>) then computes max depth reached (root node = depth 1, per
/// <c>spec/fixtures/filter-tree/README.md</c>'s depth-counting convention) and the tree-wide leaf
/// count (every <see cref="ComparisonNode"/> counts as one leaf, regardless of branch), while also
/// collecting every set-operator's (<c>in</c>/<c>notIn</c>) values-array length that exceeds
/// <see cref="ResourceLimits.MaxInValues"/> along the way. <see cref="ResourceLimits.MaxSetValuesResults"/>
/// is deliberately not checked here — see its own doc comment.
/// </summary>
public static class ResourceLimitsValidator
{
    private const string MaxDepthExceeded = "max-depth-exceeded";
    private const string MaxLeavesExceeded = "max-leaves-exceeded";
    private const string MaxInValuesExceeded = "max-in-values-exceeded";
    private const string MaxPageSizeExceeded = "max-page-size-exceeded";

    /// <summary>
    /// Checks <paramref name="filter"/> (nullable — a request may carry no filter at all, in which
    /// case depth/leaves/in-values are all vacuously within budget) and <paramref name="page"/>
    /// against <paramref name="limits"/>. Only <see cref="RangePage"/> has a size to check; a
    /// <see cref="NonePage"/> is exempt (EXP-2's full-export mode). Throws
    /// <see cref="ResourceLimitExceededException"/> naming every violated limit, not just the first
    /// — but throws <see cref="FilterNodeValidationException"/> or <see cref="PageSpecValidationException"/>
    /// immediately (not collected alongside SEC-3 violations) if the input is structurally malformed
    /// rather than merely too large, since "how much over the limit" is meaningless for a shape
    /// that was never a coherent size to begin with (e.g. a negative or inverted page range).
    /// </summary>
    public static void Validate(FilterNode? filter, PageSpec page, ResourceLimits limits)
    {
        FilterNodeValidator.Validate(filter);

        if (page is RangePage rangePage && (rangePage.StartRow < 0 || rangePage.EndRow < rangePage.StartRow))
        {
            throw new PageSpecValidationException(
                $"pageSpec.range is malformed: startRow={rangePage.StartRow}, endRow={rangePage.EndRow} " +
                "(ir.schema.json requires startRow >= 0 and endRow >= startRow).");
        }

        var violations = new List<ResourceLimitViolation>();

        if (filter is not null)
        {
            var (depth, leaves) = Walk(filter, limits, violations);

            if (depth > limits.MaxDepth)
            {
                violations.Add(new ResourceLimitViolation(MaxDepthExceeded, depth, limits.MaxDepth));
            }

            if (leaves > limits.MaxLeaves)
            {
                violations.Add(new ResourceLimitViolation(MaxLeavesExceeded, leaves, limits.MaxLeaves));
            }
        }

        if (page is RangePage validatedRangePage)
        {
            // long arithmetic: startRow/endRow are validated non-negative and endRow >= startRow
            // above, but both are still plain `int`, and their difference must not be computed in
            // 32-bit arithmetic — SEC-3's cap must hold even at the extremes of the declared range.
            var pageSize = (long)validatedRangePage.EndRow - validatedRangePage.StartRow;
            if (pageSize > limits.MaxPageSize)
            {
                violations.Add(new ResourceLimitViolation(MaxPageSizeExceeded, pageSize, limits.MaxPageSize));
            }
        }

        if (violations.Count > 0)
        {
            throw new ResourceLimitExceededException(violations);
        }
    }

    /// <summary>
    /// Recursively walks <paramref name="node"/>, returning (depth reached in this subtree, leaf
    /// count in this subtree) and appending any <see cref="ResourceLimits.MaxInValues"/> violation
    /// found at a set-operator leaf directly to <paramref name="violations"/>. Assumes
    /// <paramref name="node"/> and every descendant already passed <see cref="FilterNodeValidator"/>
    /// (called by <see cref="Validate"/> before this runs) — no null nodes, no malformed shapes.
    /// </summary>
    private static (int Depth, int Leaves) Walk(FilterNode node, ResourceLimits limits, List<ResourceLimitViolation> violations)
    {
        switch (node)
        {
            case CompositeFilterNode composite:
                var maxChildDepth = 0;
                var totalLeaves = 0;
                foreach (var child in composite.Nodes)
                {
                    var (childDepth, childLeaves) = Walk(child, limits, violations);
                    maxChildDepth = Math.Max(maxChildDepth, childDepth);
                    totalLeaves += childLeaves;
                }

                return (1 + maxChildDepth, totalLeaves);

            case NotNode not:
                var (notDepth, notLeaves) = Walk(not.Node, limits, violations);
                return (1 + notDepth, notLeaves);

            case ComparisonNode comparison:
                if (comparison.Values is { } values && values.Count > limits.MaxInValues)
                {
                    violations.Add(new ResourceLimitViolation(MaxInValuesExceeded, values.Count, limits.MaxInValues));
                }

                return (1, 1);

            default:
                throw new NotSupportedException($"Unknown FilterNode type '{node.GetType()}'.");
        }
    }
}
