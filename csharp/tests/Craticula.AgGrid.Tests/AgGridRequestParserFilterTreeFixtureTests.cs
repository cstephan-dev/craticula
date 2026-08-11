using Craticula.Core.Ir;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Fixture-driven coverage against the real <c>spec/fixtures/filter-tree/</c> suite (15 cases, AR-5's
/// And/Or/Not composition). Cases <c>007</c>-<c>015</c> pair a native <c>filterModel</c> with
/// <c>customFilters</c> — this parser deliberately keeps them SEPARATE (per the plan's Key design
/// decision #4; merging is <see cref="FilterTreeMerge.CustomFilterMerge"/>'s job, already exercised
/// against these same fixtures by <c>FilterTreeMergeFixtureTests</c> in <c>Craticula.Core.Tests</c>),
/// so each is asserted independently rather than as one merged tree.
/// </summary>
public class AgGridRequestParserFilterTreeFixtureTests
{
    private static string LoadRequest(string fixtureDir) =>
        File.ReadAllText(RepoPaths.Fixture("filter-tree", fixtureDir, "agGridRequest.json"));

    private static AgGridParsedRequest Parse(string fixtureDir) => AgGridRequestParser.Parse(LoadRequest(fixtureDir));

    [Fact]
    public void Fixture001_Implicit_And_Across_Four_Columns()
    {
        var andNode = Assert.IsType<AndNode>(Parse("001-implicit-and-four-columns").NativeFilter);

        Assert.Equal(4, andNode.Nodes.Count);
        var fields = andNode.Nodes.Select(n => Assert.IsType<ComparisonNode>(n).Field);
        Assert.Equal(["customerName", "amount", "status", "isActive"], fields);
    }

    [Fact]
    public void Fixture002_Current_Conditions_Array_Or_Form()
    {
        var orNode = Assert.IsType<OrNode>(Parse("002-within-column-or-conditions-array").NativeFilter);

        Assert.Equal(2, orNode.Nodes.Count);
    }

    [Fact]
    public void Fixture003_Legacy_Condition1_Condition2_Or_Form()
    {
        var orNode = Assert.IsType<OrNode>(Parse("003-within-column-or-legacy-condition1-condition2").NativeFilter);

        Assert.Equal(2, orNode.Nodes.Count);
    }

    /// <summary>AG-1's Definition of Done, proven against real ground truth: fixtures 002 (current
    /// <c>conditions[]</c> form) and 003 (legacy <c>condition1</c>/<c>condition2</c> form) encode the
    /// exact same logical filter (<c>region = 'west' OR region = 'east'</c>) — this parser must
    /// produce structurally identical trees for both.</summary>
    [Fact]
    public void Fixture002_And_Fixture003_Produce_Structurally_Identical_Trees()
    {
        var fromCurrent = Parse("002-within-column-or-conditions-array").NativeFilter;
        var fromLegacy = Parse("003-within-column-or-legacy-condition1-condition2").NativeFilter;

        FilterNodeAssert.StructurallyEqual(fromCurrent, fromLegacy);
    }

    [Fact]
    public void Fixture004_Legacy_And_Form_On_A_Number_Field()
    {
        var andNode = Assert.IsType<AndNode>(Parse("004-within-column-and-legacy-condition1-condition2").NativeFilter);

        Assert.Equal(2, andNode.Nodes.Count);
        Assert.Equal(ComparisonOperator.Gt, Assert.IsType<ComparisonNode>(andNode.Nodes[0]).Op);
        Assert.Equal(ComparisonOperator.Lt, Assert.IsType<ComparisonNode>(andNode.Nodes[1]).Op);
    }

    [Fact]
    public void Fixture005_Native_And_Or_Combination_Entirely_From_FilterModel()
    {
        var andNode = Assert.IsType<AndNode>(Parse("005-native-and-or-combination").NativeFilter);

        // region's own OR, status eq, quantity gt -- 3 top-level AND children, first one an OrNode.
        Assert.Equal(3, andNode.Nodes.Count);
        Assert.IsType<OrNode>(andNode.Nodes[0]);
        Assert.Equal(ComparisonOperator.Eq, Assert.IsType<ComparisonNode>(andNode.Nodes[1]).Op);
        Assert.Equal(ComparisonOperator.Gt, Assert.IsType<ComparisonNode>(andNode.Nodes[2]).Op);
    }

    [Fact]
    public void Fixture006_Multi_FilterType_Stacks_Two_Text_Conditions_Via_And()
    {
        var andNode = Assert.IsType<AndNode>(Parse("006-multi-filter-type-stacked-and").NativeFilter);

        Assert.Equal(2, andNode.Nodes.Count);
        Assert.Equal(ComparisonOperator.Contains, Assert.IsType<ComparisonNode>(andNode.Nodes[0]).Op);
        Assert.Equal(ComparisonOperator.NotContains, Assert.IsType<ComparisonNode>(andNode.Nodes[1]).Op);
    }

    [Theory]
    [InlineData("007-customfilters-not-wraps-leaf-merged")]
    [InlineData("008-customfilters-not-wraps-and-merged")]
    [InlineData("009-customfilters-not-nested-in-or-merged")]
    [InlineData("014-native-multicolumn-and-plus-customfilters-merge")]
    [InlineData("015-kitchen-sink-and-or-not-merge")]
    public void Fixtures_Pairing_Native_FilterModel_With_CustomFilters_Produce_Both_Sides_Kept_Separate(string fixtureDir)
    {
        var parsed = Parse(fixtureDir);

        Assert.NotNull(parsed.NativeFilter);
        Assert.NotNull(parsed.CustomFilters);
    }

    [Theory]
    [InlineData("010-customfilters-only-not-no-native-filter")]
    [InlineData("011-customfilters-depth-3")]
    [InlineData("012-customfilters-depth-5")]
    [InlineData("013-customfilters-depth-8-boundary")]
    public void Fixtures_With_No_Native_FilterModel_Have_Null_NativeFilter(string fixtureDir)
    {
        var parsed = Parse(fixtureDir);

        Assert.Null(parsed.NativeFilter);
        Assert.NotNull(parsed.CustomFilters);
    }

    [Fact]
    public void Fixture010_CustomFilters_Not_Wraps_A_Single_Comparison()
    {
        var notNode = Assert.IsType<NotNode>(Parse("010-customfilters-only-not-no-native-filter").CustomFilters);

        var comparison = Assert.IsType<ComparisonNode>(notNode.Node);
        Assert.Equal("status", comparison.Field);
        Assert.Equal(ComparisonOperator.Eq, comparison.Op);
    }

    [Fact]
    public void Fixture013_CustomFilters_Reaches_Depth_8_Boundary_And_Parses_Cleanly()
    {
        var customFilters = Parse("013-customfilters-depth-8-boundary").CustomFilters;

        // Just proves the parser itself accepts the depth-8 boundary tree (SEC-3's own accept/reject
        // check against this same fixture is ResourceLimitsAdversarialFixtureTests' job in
        // Craticula.Core.Tests) -- walk down to confirm the expected alternating Not/Or/And shape.
        var outer = Assert.IsType<NotNode>(customFilters);
        var innerOr = Assert.IsType<OrNode>(outer.Node);
        var innerAnd = Assert.IsType<AndNode>(innerOr.Nodes[0]);
        Assert.IsType<NotNode>(innerAnd.Nodes[0]);
    }

    [Fact]
    public void Fixture014_Native_Three_Column_And_Plus_CustomFilters_Not()
    {
        var andNode = Assert.IsType<AndNode>(Parse("014-native-multicolumn-and-plus-customfilters-merge").NativeFilter);
        Assert.Equal(3, andNode.Nodes.Count);

        var notNode = Assert.IsType<NotNode>(Parse("014-native-multicolumn-and-plus-customfilters-merge").CustomFilters);
        Assert.Equal(ComparisonOperator.Contains, Assert.IsType<ComparisonNode>(notNode.Node).Op);
    }

    [Fact]
    public void Fixture015_KitchenSink_Native_And_Or_Plus_CustomFilters_Not_And()
    {
        var parsed = Parse("015-kitchen-sink-and-or-not-merge");

        var nativeAnd = Assert.IsType<AndNode>(parsed.NativeFilter);
        Assert.Equal(2, nativeAnd.Nodes.Count);
        Assert.IsType<OrNode>(nativeAnd.Nodes[0]);

        var customNot = Assert.IsType<NotNode>(parsed.CustomFilters);
        var customAnd = Assert.IsType<AndNode>(customNot.Node);
        Assert.Equal(2, customAnd.Nodes.Count);
    }
}
