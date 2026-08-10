using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

/// <summary>
/// Hand-built boundary tests for <see cref="ResourceLimitsValidator"/>, exercising each of SEC-3's
/// four enforced limits (max depth, max leaves, max in-values, max page size) exactly at its default
/// boundary (must NOT throw — the boundary is inclusive, per
/// <c>spec/fixtures/filter-tree/013-customfilters-depth-8-boundary</c>) and one beyond it (must
/// throw, citing the right limit). Real-fixture-driven coverage of the same limits lives in
/// <see cref="ResourceLimitsAdversarialFixtureTests"/>.
/// </summary>
public class ResourceLimitsValidatorTests
{
    private static readonly ResourceLimits DefaultLimits = new();
    private static readonly PageSpec NoPage = new NonePage();

    /// <summary>A chain of <paramref name="depth"/> nested <see cref="NotNode"/> wrappers around one
    /// leaf comparison — depth 1 is the bare leaf, per the root-node-is-depth-1 convention.</summary>
    private static FilterNode BuildDepthChain(int depth)
    {
        FilterNode node = new ComparisonNode
        {
            Field = "amount",
            Op = ComparisonOperator.Gt,
            Value = new Int64Value { Value = 1 },
        };

        for (var i = 1; i < depth; i++)
        {
            node = new NotNode { Node = node };
        }

        return node;
    }

    /// <summary>A single <see cref="AndNode"/> with <paramref name="count"/> leaf comparisons.</summary>
    private static FilterNode BuildLeaves(int count) => new AndNode
    {
        Nodes = Enumerable.Range(0, count)
            .Select(i => (FilterNode)new ComparisonNode
            {
                Field = "quantity",
                Op = ComparisonOperator.Eq,
                Value = new Int64Value { Value = i },
            })
            .ToList(),
    };

    /// <summary>A single set comparison (<c>in</c>) with <paramref name="count"/> values.</summary>
    private static FilterNode BuildSetValues(int count) => new ComparisonNode
    {
        Field = "region",
        Op = ComparisonOperator.In,
        Values = Enumerable.Range(0, count)
            .Select(i => (TypedValue)new Int64Value { Value = i })
            .ToList(),
    };

    [Fact]
    public void Null_Filter_And_NonePage_Never_Throws() =>
        ResourceLimitsValidator.Validate(null, NoPage, DefaultLimits);

    [Fact]
    public void Depth_Exactly_At_Max_Does_Not_Throw() =>
        ResourceLimitsValidator.Validate(BuildDepthChain(8), NoPage, DefaultLimits);

    [Fact]
    public void Depth_One_Beyond_Max_Throws_Citing_MaxDepthExceeded()
    {
        var ex = Assert.Throws<ResourceLimitExceededException>(
            () => ResourceLimitsValidator.Validate(BuildDepthChain(9), NoPage, DefaultLimits));

        var violation = Assert.Single(ex.Violations, v => v.LimitName == "max-depth-exceeded");
        Assert.Equal(9, violation.Actual);
        Assert.Equal(8, violation.Max);
    }

    [Fact]
    public void Leaves_Exactly_At_Max_Does_Not_Throw() =>
        ResourceLimitsValidator.Validate(BuildLeaves(64), NoPage, DefaultLimits);

    [Fact]
    public void Leaves_One_Beyond_Max_Throws_Citing_MaxLeavesExceeded()
    {
        var ex = Assert.Throws<ResourceLimitExceededException>(
            () => ResourceLimitsValidator.Validate(BuildLeaves(65), NoPage, DefaultLimits));

        var violation = Assert.Single(ex.Violations, v => v.LimitName == "max-leaves-exceeded");
        Assert.Equal(65, violation.Actual);
        Assert.Equal(64, violation.Max);
    }

    [Fact]
    public void Leaves_Are_Counted_TreeWide_Across_Two_Branches_Not_PerNode()
    {
        var tree = new OrNode { Nodes = [BuildLeaves(35), BuildLeaves(35)] };

        var ex = Assert.Throws<ResourceLimitExceededException>(
            () => ResourceLimitsValidator.Validate(tree, NoPage, DefaultLimits));

        var violation = Assert.Single(ex.Violations, v => v.LimitName == "max-leaves-exceeded");
        Assert.Equal(70, violation.Actual);
    }

    [Fact]
    public void InValues_Exactly_At_Max_Does_Not_Throw() =>
        ResourceLimitsValidator.Validate(BuildSetValues(1000), NoPage, DefaultLimits);

    [Fact]
    public void InValues_One_Beyond_Max_Throws_Citing_MaxInValuesExceeded()
    {
        var ex = Assert.Throws<ResourceLimitExceededException>(
            () => ResourceLimitsValidator.Validate(BuildSetValues(1001), NoPage, DefaultLimits));

        var violation = Assert.Single(ex.Violations, v => v.LimitName == "max-in-values-exceeded");
        Assert.Equal(1001, violation.Actual);
        Assert.Equal(1000, violation.Max);
    }

    [Fact]
    public void PageSize_Exactly_At_Max_Does_Not_Throw()
    {
        var page = new RangePage { StartRow = 0, EndRow = 1000 };
        ResourceLimitsValidator.Validate(null, page, DefaultLimits);
    }

    [Fact]
    public void PageSize_One_Beyond_Max_Throws_Citing_MaxPageSizeExceeded()
    {
        var page = new RangePage { StartRow = 0, EndRow = 1001 };

        var ex = Assert.Throws<ResourceLimitExceededException>(
            () => ResourceLimitsValidator.Validate(null, page, DefaultLimits));

        var violation = Assert.Single(ex.Violations, v => v.LimitName == "max-page-size-exceeded");
        Assert.Equal(1001, violation.Actual);
        Assert.Equal(1000, violation.Max);
    }

    [Fact]
    public void NonePage_Is_Exempt_From_The_Page_Size_Check_Regardless_Of_Filter_Size() =>
        ResourceLimitsValidator.Validate(BuildLeaves(64), NoPage, DefaultLimits);

    [Fact]
    public void Collects_Every_Violated_Limit_In_One_Exception()
    {
        var tree = BuildDepthChain(9); // one violation: depth
        var page = new RangePage { StartRow = 0, EndRow = 1001 }; // one violation: page size

        var ex = Assert.Throws<ResourceLimitExceededException>(
            () => ResourceLimitsValidator.Validate(tree, page, DefaultLimits));

        Assert.Equal(2, ex.Violations.Count);
        Assert.Contains(ex.Violations, v => v.LimitName == "max-depth-exceeded");
        Assert.Contains(ex.Violations, v => v.LimitName == "max-page-size-exceeded");
    }
}
