using Craticula.Core.Ir;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// AG-7 (story #2454): the fields AG Grid Community's Infinite Row Model sends — <c>startRow</c>, <c>endRow</c>,
/// <c>sortModel</c>, <c>filterModel</c>, and no group, value, pivot or context field (a v35+ grid also adds a sort item's
/// <c>type</c>, which the parser ignores) — parses to a flat request, and the native in-range option a
/// Community number or date filter offers is rejected exactly as it is for the SSRM (SEM-4; <c>customFilters</c> is the
/// way). The compile side of the same shape is graded by <c>spec/fixtures/community-infinite/</c>.
/// </summary>
public class AgGridRequestParserCommunityFlatRequestTests
{
    private const string Flat = """
        {{
          "startRow": 0, "endRow": 100,
          "filterModel": {0},
          "sortModel": [ {{ "colId": "id", "sort": "asc" }} ]
        }}
        """;

    [Fact]
    public void A_Request_With_Only_The_Infinite_Row_Model_Fields_Parses_To_A_Flat_Request()
    {
        var parsed = AgGridRequestParser.Parse(string.Format(
            Flat, """{ "age": { "filterType": "number", "type": "greaterThan", "filter": 18 } }"""));

        Assert.Null(parsed.Group);
        var page = Assert.IsType<RangePage>(parsed.Page);
        Assert.Equal(0, page.StartRow);
        Assert.Equal(100, page.EndRow);
        Assert.Single(parsed.Sort!);
        var comparison = Assert.IsType<ComparisonNode>(parsed.NativeFilter);
        Assert.Equal(ComparisonOperator.Gt, comparison.Op);
    }

    [Theory]
    [InlineData("""{ "age": { "filterType": "number", "type": "inRange", "filter": 18, "filterTo": 65 } }""")]
    [InlineData("""{ "hireDate": { "filterType": "date", "type": "inRange", "dateFrom": "2024-01-01 00:00:00", "dateTo": "2024-12-31 00:00:00" } }""")]
    public void The_Native_InRange_Option_Of_A_Community_Filter_Is_Rejected(string filterModel)
    {
        var exception = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(string.Format(Flat, filterModel)));

        Assert.Equal("unknown-operator", exception.Reason);
    }
}
