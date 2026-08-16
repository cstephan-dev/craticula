using Craticula.Core.Ir;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Fixture-driven coverage against the real <c>spec/fixtures/group-aggregation/</c> suite (23 cases,
/// BM-2/BM-3/AR-8), for the 18 that are row-fetch requests. Cases <c>016</c>-<c>019</c> and
/// <c>022</c> are the
/// distinct <c>setValuesRequest</c> envelope, which <see cref="AgGridRequestParser.Parse"/> still
/// rejects and <see cref="AgGridRequestParser.ParseSetValues"/> handles — they are covered by
/// <see cref="AgGridRequestParserSetValuesTests"/>, and their rejection from this entry point by
/// <see cref="AgGridRequestParserRejectionTests"/>.
/// </summary>
public class AgGridRequestParserGroupAggregationFixtureTests
{
    private static AgGridParsedRequest Parse(string fixtureDir) =>
        AgGridRequestParser.Parse(File.ReadAllText(RepoPaths.Fixture("group-aggregation", fixtureDir, "agGridRequest.json")));

    [Fact]
    public void Fixture001_Group_Top_Level_Zero_Keys_Consumed()
    {
        var parsed = Parse("001-group-top-level-no-keys-consumed");

        Assert.Equal(["region", "segment", "product"], parsed.Group!.GroupCols);
        Assert.Empty(parsed.Group.GroupKeys);
        Assert.Single(parsed.Aggregations!);
        Assert.Equal("amount", parsed.Aggregations![0].Field);
        Assert.Equal(AggregationFunc.Sum, parsed.Aggregations[0].Func);
        Assert.Null(parsed.Aggregations[0].Alias);
    }

    [Fact]
    public void Fixture002_Group_Second_Level_One_Key_Consumed()
    {
        var parsed = Parse("002-group-second-level-one-key-consumed");

        Assert.Equal(3, parsed.Group!.GroupCols.Count);
        Assert.Single(parsed.Group.GroupKeys);
        Assert.Equal("EMEA", Assert.IsType<StringValue>(parsed.Group.GroupKeys[0]).Value);
    }

    [Fact]
    public void Fixture004_Group_Deepest_Level_Mixes_String_And_Int64_Typed_Keys()
    {
        var parsed = Parse("004-group-deepest-level-three-keys-consumed");

        Assert.Equal(4, parsed.Group!.GroupCols.Count);
        Assert.Equal(3, parsed.Group.GroupKeys.Count);
        Assert.IsType<StringValue>(parsed.Group.GroupKeys[0]);
        Assert.IsType<StringValue>(parsed.Group.GroupKeys[1]);
        Assert.Equal(2024, Assert.IsType<Int64Value>(parsed.Group.GroupKeys[2]).Value);
    }

    [Fact]
    public void Fixture005_Leaf_Query_Two_Group_Cols_Fully_Consumed()
    {
        var parsed = Parse("005-leaf-query-two-group-cols-fully-consumed");

        Assert.Equal(2, parsed.Group!.GroupCols.Count);
        Assert.Equal(2, parsed.Group.GroupKeys.Count);
        Assert.Equal(parsed.Group.GroupCols.Count, parsed.Group.GroupKeys.Count);
    }

    [Theory]
    [InlineData("007-aggregation-sum-only", AggregationFunc.Sum)]
    [InlineData("008-aggregation-min-only", AggregationFunc.Min)]
    [InlineData("009-aggregation-max-only", AggregationFunc.Max)]
    [InlineData("010-aggregation-avg-only", AggregationFunc.Avg)]
    [InlineData("011-aggregation-count-only", AggregationFunc.Count)]
    public void Single_Function_Aggregation_Fixtures(string fixtureDir, AggregationFunc expected)
    {
        var parsed = Parse(fixtureDir);

        Assert.Single(parsed.Aggregations!);
        Assert.Equal(expected, parsed.Aggregations![0].Func);
        Assert.Null(parsed.Aggregations[0].Alias);
    }

    [Fact]
    public void Fixture012_Multiple_Aggregations_On_Same_Field_Get_Distinct_Aliases()
    {
        var parsed = Parse("012-aggregation-multiple-sum-avg");

        Assert.Equal(2, parsed.Aggregations!.Count);
        Assert.Equal("amount", parsed.Aggregations[0].Field);
        Assert.Equal(AggregationFunc.Sum, parsed.Aggregations[0].Func);
        Assert.Equal("amountSum", parsed.Aggregations[0].Alias);
        Assert.Equal("amount", parsed.Aggregations[1].Field);
        Assert.Equal(AggregationFunc.Avg, parsed.Aggregations[1].Func);
        Assert.Equal("amountAvg", parsed.Aggregations[1].Alias);
    }

    [Fact]
    public void Fixture013_Three_Functions_Together()
    {
        var parsed = Parse("013-aggregation-multiple-sum-min-max");

        Assert.Equal(3, parsed.Aggregations!.Count);
        Assert.Equal([AggregationFunc.Sum, AggregationFunc.Min, AggregationFunc.Max], parsed.Aggregations.Select(a => a.Func));
    }

    [Fact]
    public void Fixture014_Explicit_Alias_Present()
    {
        var parsed = Parse("014-aggregation-alias-explicit");

        Assert.Equal("totalRevenue", parsed.Aggregations![0].Alias);
    }

    [Fact]
    public void Fixture015_Default_Alias_Absent_When_Id_Equals_Field()
    {
        var parsed = Parse("015-aggregation-alias-default-name");

        Assert.Null(parsed.Aggregations![0].Alias);
    }

    [Fact]
    public void Fixture020_Grouped_Query_With_Filter_And_Sort()
    {
        var parsed = Parse("020-grouped-query-with-filter-and-sort");

        Assert.NotNull(parsed.Group);
        Assert.Empty(parsed.Group!.GroupKeys);

        var comparison = Assert.IsType<ComparisonNode>(parsed.NativeFilter);
        Assert.Equal("amount", comparison.Field);
        Assert.Equal(ComparisonOperator.Gt, comparison.Op);
        Assert.Equal("1000", Assert.IsType<StringValue>(comparison.Value).Value);

        Assert.Single(parsed.Sort!);
        Assert.Equal("amount", parsed.Sort![0].Field);
        Assert.Equal(SortDirection.Desc, parsed.Sort[0].Direction);
        Assert.Equal(NullsPosition.First, parsed.Sort[0].NullsPosition);
    }

    /// <summary>
    /// AG-6, against the payload AG Grid really sends: sort coupling emits the display column plus
    /// the WHOLE <c>rowGroupCols</c> set (1 + 3 entries here), and all four collapse into the single
    /// level this request produces. Asserted from the fixture rather than only from a hand-written
    /// request, because the defect #1640's second review found was precisely a fixture carrying a
    /// payload its own <c>rowGroupCols</c> could never produce.
    /// </summary>
    [Fact]
    public void Fixture021_The_Coupled_Auto_Column_Payload_Collapses_To_The_Level_It_Produces()
    {
        var parsed = Parse("021-group-column-sort-via-auto-column-colid");

        Assert.Equal(["region", "segment", "product"], parsed.Group!.GroupCols);
        Assert.Single(parsed.Group.GroupKeys);

        Assert.Single(parsed.Sort!);
        Assert.Equal("segment", parsed.Sort![0].Field);
        Assert.Equal(SortDirection.Desc, parsed.Sort[0].Direction);
        Assert.Equal(NullsPosition.First, parsed.Sort[0].NullsPosition);
    }

    public static readonly TheoryData<string> AllRowFetchFixtures = new()
    {
        "001-group-top-level-no-keys-consumed",
        "002-group-second-level-one-key-consumed",
        "003-group-third-level-two-keys-consumed",
        "004-group-deepest-level-three-keys-consumed",
        "005-leaf-query-two-group-cols-fully-consumed",
        "006-leaf-query-single-group-col-fully-consumed",
        "007-aggregation-sum-only",
        "008-aggregation-min-only",
        "009-aggregation-max-only",
        "010-aggregation-avg-only",
        "011-aggregation-count-only",
        "012-aggregation-multiple-sum-avg",
        "013-aggregation-multiple-sum-min-max",
        "014-aggregation-alias-explicit",
        "015-aggregation-alias-default-name",
        "020-grouped-query-with-filter-and-sort",
        "021-group-column-sort-via-auto-column-colid",
        "023-grouped-drill-down-with-active-row-filter"
    };

    [Theory]
    [MemberData(nameof(AllRowFetchFixtures))]
    public void Every_Row_Fetch_Fixture_Parses_Without_Throwing(string fixtureDir)
    {
        Parse(fixtureDir);
    }
}
