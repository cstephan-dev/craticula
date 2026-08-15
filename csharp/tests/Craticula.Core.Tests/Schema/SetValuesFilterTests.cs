using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

/// <summary>
/// GRP-6's two filter-tree operations. The own-field exclusion is BM-3's defining semantic and the
/// one thing <c>spec/fixtures/group-aggregation/017</c> asserts by <em>not</em> changing, so it is
/// exercised here on the tree shapes an artifact comparison cannot distinguish — a composite mixing
/// the target column with others, a negation, and a filter model that is nothing but the target
/// column's own filter.
/// </summary>
public class SetValuesFilterTests
{
    private static ComparisonNode Eq(string field, string value) =>
        new() { Field = field, Op = ComparisonOperator.Eq, Value = new StringValue { Value = value } };

    [Fact]
    public void A_Null_Filter_Model_Stays_Null()
    {
        Assert.Null(SetValuesFilter.ExcludeOwnField(null, "region"));
    }

    [Fact]
    public void Another_Columns_Filter_Survives_Untouched()
    {
        var segment = Eq("segment", "Enterprise");

        Assert.Same(segment, SetValuesFilter.ExcludeOwnField(segment, "region"));
    }

    /// <summary>
    /// The whole point of the rule: a set filter whose own selection narrowed its own value list
    /// could never be widened again, because the values the user did not pick would stop being
    /// offered.
    /// </summary>
    [Fact]
    public void The_Target_Columns_Own_Filter_Is_Dropped_Leaving_No_Filter_At_All()
    {
        Assert.Null(SetValuesFilter.ExcludeOwnField(Eq("region", "EMEA"), "region"));
    }

    /// <summary>
    /// Conditions are pruned, not branches: this is exactly <c>017</c>'s shape, and it is why that
    /// fixture's artifacts are byte-identical whether or not its <c>filterModel</c> carries a
    /// <c>region</c> entry.
    /// </summary>
    [Fact]
    public void A_Composite_Mixing_The_Target_With_Others_Keeps_The_Others_And_Unwraps()
    {
        var tree = new AndNode { Nodes = [Eq("region", "EMEA"), Eq("segment", "Enterprise")] };

        var scoped = Assert.IsType<ComparisonNode>(SetValuesFilter.ExcludeOwnField(tree, "region"));
        Assert.Equal("segment", scoped.Field);
    }

    [Fact]
    public void A_Composite_Of_Nothing_But_The_Target_Disappears_Rather_Than_Becoming_Empty()
    {
        var tree = new OrNode { Nodes = [Eq("region", "EMEA"), Eq("region", "APAC")] };

        Assert.Null(SetValuesFilter.ExcludeOwnField(tree, "region"));
    }

    /// <summary>
    /// Inherited from <see cref="FieldAllowlistValidator.Strip"/>, and the reason the exclusion
    /// delegates to it rather than re-deriving the walk: a <c>Not</c> whose condition is removed must
    /// vanish with it, never become a negation of nothing that matches every row.
    /// </summary>
    [Fact]
    public void A_Negation_Of_The_Target_Vanishes_Rather_Than_Matching_Everything()
    {
        var tree = new NotNode { Node = Eq("region", "EMEA") };

        Assert.Null(SetValuesFilter.ExcludeOwnField(tree, "region"));
    }

    [Fact]
    public void A_Blank_ColId_Is_A_Caller_Error_Not_A_No_Op_Exclusion()
    {
        Assert.Throws<ArgumentException>(() => SetValuesFilter.ExcludeOwnField(Eq("region", "EMEA"), ""));
    }

    // ---- the search prefix ------------------------------------------------------------------------

    [Fact]
    public void No_SearchPrefix_Means_No_Predicate()
    {
        Assert.Null(SetValuesFilter.SearchPrefixPredicate(new SetValuesSpec { ColId = "region" }));
    }

    /// <summary>
    /// An ordinary <c>startsWith</c> on the target column carrying the RAW text: SEM-7's escaping is
    /// the compiler's, applied when the pattern is built, so a literal <c>%</c> typed into the search
    /// box must still be a literal <c>%</c> at this level.
    /// </summary>
    [Fact]
    public void A_SearchPrefix_Becomes_A_StartsWith_On_The_Target_Column_Carrying_The_Raw_Text()
    {
        var predicate = SetValuesFilter.SearchPrefixPredicate(new SetValuesSpec { ColId = "region", SearchPrefix = "50%" })!;

        Assert.Equal("region", predicate.Field);
        Assert.Equal(ComparisonOperator.StartsWith, predicate.Op);
        Assert.Equal("50%", Assert.IsType<StringValue>(predicate.Value).Value);
    }

    [Fact]
    public void A_Structurally_Invalid_Spec_Is_Rejected_Before_Any_Predicate_Is_Minted()
    {
        Assert.Throws<SetValuesSpecValidationException>(
            () => SetValuesFilter.SearchPrefixPredicate(new SetValuesSpec { ColId = "", SearchPrefix = "eu" }));
    }
}
