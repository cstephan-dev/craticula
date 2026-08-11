using Craticula.Core.Ir;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Hand-built boundary tests for <see cref="AgGridRequestParser.Parse"/>'s <c>rowGroupCols</c>/
/// <c>groupKeys</c>/<c>valueCols</c> dispatch (BM-2/AR-8) — real-fixture-driven coverage lives in
/// <see cref="AgGridRequestParserGroupAggregationFixtureTests"/>.
/// </summary>
public class AgGridRequestParserGroupAggregationTests
{
    private static AgGridParsedRequest Parse(string request) => AgGridRequestParser.Parse(request);

    [Fact]
    public void Empty_RowGroupCols_Produces_Null_Group()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": null, "sortModel": []
            }
            """;

        Assert.Null(Parse(request).Group);
    }

    [Fact]
    public void NonEmpty_RowGroupCols_Builds_GroupSpec_Using_Field_Not_Id()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [
                { "id": "regionColId", "displayName": "Region", "field": "region" },
                { "id": "segmentColId", "displayName": "Segment", "field": "segment" }
              ],
              "valueCols": [], "pivotCols": [], "pivotMode": false,
              "groupKeys": [ { "type": "string", "value": "EMEA" } ],
              "filterModel": null, "sortModel": []
            }
            """;

        var group = Parse(request).Group;
        Assert.NotNull(group);
        Assert.Equal(["region", "segment"], group!.GroupCols);
        Assert.Single(group.GroupKeys);
        Assert.Equal("EMEA", Assert.IsType<StringValue>(group.GroupKeys[0]).Value);
    }

    [Fact]
    public void GroupKeys_Deserializes_Mixed_Typed_Values()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [
                { "id": "region", "displayName": "Region", "field": "region" },
                { "id": "fiscalYear", "displayName": "Fiscal Year", "field": "fiscalYear" }
              ],
              "valueCols": [], "pivotCols": [], "pivotMode": false,
              "groupKeys": [ { "type": "string", "value": "EMEA" }, { "type": "int64", "value": 2024 } ],
              "filterModel": null, "sortModel": []
            }
            """;

        var group = Parse(request).Group!;
        Assert.Equal(2, group.GroupKeys.Count);
        Assert.IsType<StringValue>(group.GroupKeys[0]);
        Assert.Equal(2024, Assert.IsType<Int64Value>(group.GroupKeys[1]).Value);
    }

    [Fact]
    public void Empty_ValueCols_Produces_Null_Aggregations()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": null, "sortModel": []
            }
            """;

        Assert.Null(Parse(request).Aggregations);
    }

    [Theory]
    [InlineData("sum", AggregationFunc.Sum)]
    [InlineData("min", AggregationFunc.Min)]
    [InlineData("max", AggregationFunc.Max)]
    [InlineData("avg", AggregationFunc.Avg)]
    [InlineData("count", AggregationFunc.Count)]
    public void ValueCols_AggFunc_Maps_To_Expected_AggregationFunc(string wireFunc, AggregationFunc expected)
    {
        var request = $$"""
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "valueCols": [ { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "{{wireFunc}}" } ],
              "filterModel": null, "sortModel": []
            }
            """;

        var aggregations = Parse(request).Aggregations!;
        Assert.Single(aggregations);
        Assert.Equal("amount", aggregations[0].Field);
        Assert.Equal(expected, aggregations[0].Func);
        Assert.Null(aggregations[0].Alias);
    }

    [Fact]
    public void ValueCols_Id_Differing_From_Field_Becomes_Alias()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "valueCols": [ { "id": "totalRevenue", "displayName": "Total Revenue", "field": "amount", "aggFunc": "sum" } ],
              "filterModel": null, "sortModel": []
            }
            """;

        var aggregations = Parse(request).Aggregations!;
        Assert.Equal("totalRevenue", aggregations[0].Alias);
    }

    [Fact]
    public void Multiple_ValueCols_On_Same_Field_Are_Distinct_Aggregations()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "valueCols": [
                { "id": "amountSum", "displayName": "Total", "field": "amount", "aggFunc": "sum" },
                { "id": "amountAvg", "displayName": "Average", "field": "amount", "aggFunc": "avg" }
              ],
              "filterModel": null, "sortModel": []
            }
            """;

        var aggregations = Parse(request).Aggregations!;
        Assert.Equal(2, aggregations.Count);
        Assert.Equal(AggregationFunc.Sum, aggregations[0].Func);
        Assert.Equal("amountSum", aggregations[0].Alias);
        Assert.Equal(AggregationFunc.Avg, aggregations[1].Func);
        Assert.Equal("amountAvg", aggregations[1].Alias);
    }

    [Fact]
    public void Unrecognized_AggFunc_Throws_UnknownOperator()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "valueCols": [ { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "first" } ],
              "filterModel": null, "sortModel": []
            }
            """;

        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse(request));
        Assert.Equal("unknown-operator", ex.Reason);
    }

    [Fact]
    public void Missing_AggFunc_Throws_MalformedRequest()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "valueCols": [ { "id": "amount", "displayName": "Amount", "field": "amount" } ],
              "filterModel": null, "sortModel": []
            }
            """;

        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse(request));
        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void Null_Element_In_RowGroupCols_Throws_MalformedRequest()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [ null ], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": null, "sortModel": []
            }
            """;

        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse(request));
        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void NonEmpty_GroupKeys_With_Empty_RowGroupCols_Throws_MalformedRequest_Rather_Than_Silently_Dropping_The_Key()
    {
        // A drill-down key with no group column to drill into would otherwise be silently dropped,
        // which WIDENS the query (BM-2: a group key restricts the result set) — the opposite of
        // AG-1/AG-5's "explicitly rejected, never silently mis-parsed".
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false,
              "groupKeys": [ { "type": "string", "value": "EMEA" } ],
              "filterModel": null, "sortModel": []
            }
            """;

        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse(request));
        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void RowGroupCols_Entry_Missing_Field_Falls_Back_To_Id()
    {
        // AG Grid's real ColumnVO declares 'field' optional (a valueGetter-backed column has no
        // plain field path) — 'id' is always present and is what GridSchema's allowlist keys on.
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [ { "id": "region", "displayName": "Region" } ],
              "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": null, "sortModel": []
            }
            """;

        var group = Parse(request).Group;
        Assert.Equal(["region"], group!.GroupCols);
    }
}
