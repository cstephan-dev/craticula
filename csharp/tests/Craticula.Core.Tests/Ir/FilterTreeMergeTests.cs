using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Ir;

/// <summary>
/// Hand-built boundary tests for <see cref="FilterTreeMerge"/>'s pinned 3-rule merge convention
/// (<c>spec/fixtures/filter-tree/README.md</c>: both present -> opaque <c>And([a, b])</c>; one
/// present -> that one, unwrapped; neither -> <see langword="null"/>) plus <see cref="FilterTreeMerge.CustomFilterMerge"/>'s
/// defense-in-depth structural validation of both sides. Real-fixture-driven coverage of the same
/// convention lives in <see cref="FilterTreeMergeFixtureTests"/>.
/// </summary>
public class FilterTreeMergeTests
{
    private static ComparisonNode Leaf(string field, long value) => new()
    {
        Field = field,
        Op = ComparisonOperator.Eq,
        Value = new Int64Value { Value = value },
    };

    [Fact]
    public void And_Returns_Null_When_Both_Are_Null() =>
        Assert.Null(FilterTreeMerge.And(null, null));

    [Fact]
    public void And_Returns_A_Unwrapped_When_Only_A_Is_Present()
    {
        var a = Leaf("region", 1);

        Assert.Same(a, FilterTreeMerge.And(a, null));
    }

    [Fact]
    public void And_Returns_B_Unwrapped_When_Only_B_Is_Present()
    {
        var b = Leaf("region", 1);

        Assert.Same(b, FilterTreeMerge.And(null, b));
    }

    [Fact]
    public void And_Wraps_Both_In_A_New_AndNode_When_Both_Are_Present()
    {
        var a = Leaf("region", 1);
        var b = Leaf("quantity", 2);

        var merged = Assert.IsType<AndNode>(FilterTreeMerge.And(a, b));

        Assert.Equal(2, merged.Nodes.Count);
        Assert.Same(a, merged.Nodes[0]);
        Assert.Same(b, merged.Nodes[1]);
    }

    [Fact]
    public void And_Never_Flattens_A_When_A_Is_Itself_An_AndNode()
    {
        var a = new AndNode { Nodes = [Leaf("region", 1), Leaf("quantity", 2)] };
        var b = Leaf("price", 3);

        var merged = Assert.IsType<AndNode>(FilterTreeMerge.And(a, b));

        // Exactly 2 top-level children (a nested whole, b) — NOT 3 (a's own children flattened
        // alongside b), per the "opaque nesting" rule.
        Assert.Equal(2, merged.Nodes.Count);
        Assert.Same(a, merged.Nodes[0]);
        Assert.Same(b, merged.Nodes[1]);
    }

    [Fact]
    public void CustomFilterMerge_Degenerates_To_The_Fragment_When_No_Native_Filter()
    {
        var customFilters = Leaf("status", 1);

        Assert.Same(customFilters, FilterTreeMerge.CustomFilterMerge(null, customFilters));
    }

    [Fact]
    public void CustomFilterMerge_Returns_Native_Unchanged_When_No_CustomFilters()
    {
        var native = Leaf("status", 1);

        Assert.Same(native, FilterTreeMerge.CustomFilterMerge(native, null));
    }

    [Fact]
    public void CustomFilterMerge_Returns_Null_When_Neither_Side_Is_Present() =>
        Assert.Null(FilterTreeMerge.CustomFilterMerge(null, null));

    [Fact]
    public void CustomFilterMerge_Wraps_Native_As_One_Opaque_Child_Not_Flattened()
    {
        var native = new AndNode { Nodes = [Leaf("region", 1), Leaf("quantity", 2)] };
        var customFilters = new NotNode { Node = Leaf("status", 3) };

        var merged = Assert.IsType<AndNode>(FilterTreeMerge.CustomFilterMerge(native, customFilters));

        Assert.Equal(2, merged.Nodes.Count);
        Assert.Same(native, merged.Nodes[0]);
        Assert.Same(customFilters, merged.Nodes[1]);
    }

    [Fact]
    public void CustomFilterMerge_Throws_FilterNodeValidationException_When_Native_Is_Structurally_Invalid()
    {
        var malformedNative = new AndNode { Nodes = [] }; // minItems:1 violation
        var customFilters = Leaf("status", 1);

        Assert.Throws<FilterNodeValidationException>(
            () => FilterTreeMerge.CustomFilterMerge(malformedNative, customFilters));
    }

    [Fact]
    public void CustomFilterMerge_Throws_FilterNodeValidationException_When_CustomFilters_Is_Structurally_Invalid()
    {
        var native = Leaf("status", 1);
        var malformedCustomFilters = new ComparisonNode { Field = "region", Op = ComparisonOperator.Eq }; // 'eq' requires 'value'

        Assert.Throws<FilterNodeValidationException>(
            () => FilterTreeMerge.CustomFilterMerge(native, malformedCustomFilters));
    }
}
