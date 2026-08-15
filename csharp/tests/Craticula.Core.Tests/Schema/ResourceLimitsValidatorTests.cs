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

    // ---- GRP-6: the set-values cap, which is APPLIED rather than enforced ---------------------------

    /// <summary>
    /// <c>spec/fixtures/group-aggregation/016</c>-<c>018</c>: no cap requested, so the host's default
    /// applies. A distinct-values query is never unbounded — "the client asked for no limit" is not
    /// "no limit".
    /// </summary>
    [Fact]
    public void No_Requested_Limit_Resolves_To_The_Hosts_Default()
    {
        Assert.Equal(1000, ResourceLimitsValidator.ResolveSetValuesLimit(null, DefaultLimits));
    }

    /// <summary>
    /// <c>spec/fixtures/group-aggregation/019</c>: 50,000 requested against a 10,000 ceiling compiles
    /// to 10,000. Deliberately NOT a rejection — a set filter asks for as many values as it can get by
    /// design, and making the ceiling a contract the client has to know is the opposite of what a
    /// server-side cap is for.
    /// </summary>
    [Fact]
    public void A_Request_Above_The_Configured_Maximum_Is_Clamped_Not_Rejected()
    {
        Assert.Equal(10_000, ResourceLimitsValidator.ResolveSetValuesLimit(50_000, DefaultLimits));
    }

    [Fact]
    public void A_Request_Below_The_Maximum_Is_Honoured_Even_Below_The_Default()
    {
        Assert.Equal(25, ResourceLimitsValidator.ResolveSetValuesLimit(25, DefaultLimits));
    }

    [Fact]
    public void A_Request_Exactly_At_The_Maximum_Is_Honoured()
    {
        Assert.Equal(10_000, ResourceLimitsValidator.ResolveSetValuesLimit(10_000, DefaultLimits));
    }

    /// <summary>
    /// The two knobs are independent and no ordering between them is enforced at construction time,
    /// so a host that configures a default above its own ceiling still gets the ceiling rather than a
    /// startup failure — the minimum is taken over both.
    /// </summary>
    [Fact]
    public void A_Default_Above_The_Maximum_Still_Yields_The_Maximum()
    {
        var limits = new ResourceLimits { DefaultSetValuesResults = 5000, MaxSetValuesResults = 500 };

        Assert.Equal(500, ResourceLimitsValidator.ResolveSetValuesLimit(null, limits));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_NonPositive_Request_Is_A_Caller_Error_Because_SetValuesSpecValidator_Already_Rejects_It(int requested)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ResourceLimitsValidator.ResolveSetValuesLimit(requested, DefaultLimits));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_NonPositive_Default_Cap_Is_Refused_By_ResourceLimits_Itself(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceLimits { DefaultSetValuesResults = value });
    }
}
