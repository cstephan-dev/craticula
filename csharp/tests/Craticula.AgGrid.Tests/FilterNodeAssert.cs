using Craticula.Core.Ir;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Recursive structural-equality assertion for <see cref="FilterNode"/> trees. Plain
/// <c>Assert.Equal</c> does not work here: every record's generated <c>Equals</c> compares its
/// <c>IReadOnlyList&lt;...&gt;</c>-typed properties (<see cref="CompositeFilterNode.Nodes"/>,
/// <see cref="ComparisonNode.Values"/>) via <see cref="object.Equals(object?)"/>, which for the
/// concrete <see cref="List{T}"/> instances the parser builds is reference equality, not content
/// equality — so two independently-built-but-logically-identical trees (e.g. AG-1's legacy vs.
/// current combined-form outputs) would spuriously compare unequal. This walks both trees in lock
/// step and compares leaf content element-by-element instead.
/// </summary>
internal static class FilterNodeAssert
{
    public static void StructurallyEqual(FilterNode? expected, FilterNode? actual)
    {
        switch (expected, actual)
        {
            case (null, null):
                return;

            case (CompositeFilterNode e, CompositeFilterNode a) when e.GetType() == a.GetType():
                Assert.Equal(e.Nodes.Count, a.Nodes.Count);
                for (var i = 0; i < e.Nodes.Count; i++)
                {
                    StructurallyEqual(e.Nodes[i], a.Nodes[i]);
                }

                return;

            case (NotNode e, NotNode a):
                StructurallyEqual(e.Node, a.Node);
                return;

            case (ComparisonNode e, ComparisonNode a):
                Assert.Equal(e.Field, a.Field);
                Assert.Equal(e.Op, a.Op);
                Assert.Equal(e.Value, a.Value);
                Assert.Equal(e.From, a.From);
                Assert.Equal(e.To, a.To);
                Assert.Equal(e.FromInclusive, a.FromInclusive);
                Assert.Equal(e.ToInclusive, a.ToInclusive);
                Assert.Equal(e.Values?.Count ?? 0, a.Values?.Count ?? 0);
                if (e.Values is not null && a.Values is not null)
                {
                    for (var i = 0; i < e.Values.Count; i++)
                    {
                        Assert.Equal(e.Values[i], a.Values[i]);
                    }
                }

                return;

            default:
                Assert.Fail($"Filter node shape mismatch: expected {Describe(expected)}, got {Describe(actual)}.");
                return;
        }
    }

    private static string Describe(FilterNode? node) => node?.GetType().Name ?? "null";
}
