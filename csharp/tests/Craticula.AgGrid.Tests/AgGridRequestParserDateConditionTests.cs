using Craticula.Core.Ir;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Hand-built boundary tests for <see cref="AgGridRequestParser.Parse"/>'s <c>date</c> filterType
/// dispatch — the same operator vocabulary as <c>number</c>, value taken from <c>dateFrom</c>
/// (<c>dateTo</c> unused for these non-range operators), plus AG-2 date presets rejected as a side
/// effect of the closed operator vocabulary (deferred to #1636, out of this story's scope).
/// </summary>
public class AgGridRequestParserDateConditionTests
{
    private const string Envelope = """
        {{
          "startRow": 0, "endRow": 100,
          "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
          "filterModel": {{ "birthDate": {0} }},
          "sortModel": []
        }}
        """;

    private static ComparisonNode ParseSingleComparison(string entryJson)
    {
        var request = string.Format(Envelope, entryJson);
        var parsed = AgGridRequestParser.Parse(request);
        return Assert.IsType<ComparisonNode>(parsed.NativeFilter);
    }

    [Theory]
    [InlineData("equals", ComparisonOperator.Eq)]
    [InlineData("notEqual", ComparisonOperator.Neq)]
    [InlineData("lessThan", ComparisonOperator.Lt)]
    [InlineData("lessThanOrEqual", ComparisonOperator.Lte)]
    [InlineData("greaterThan", ComparisonOperator.Gt)]
    [InlineData("greaterThanOrEqual", ComparisonOperator.Gte)]
    public void Binary_Operator_Uses_DateFrom_As_StringValue(string wireType, ComparisonOperator expectedOp)
    {
        var comparison = ParseSingleComparison(
            $$"""{ "filterType": "date", "type": "{{wireType}}", "dateFrom": "1990-05-12", "dateTo": null }""");

        Assert.Equal("birthDate", comparison.Field);
        Assert.Equal(expectedOp, comparison.Op);
        var value = Assert.IsType<StringValue>(comparison.Value);
        Assert.Equal("1990-05-12", value.Value);
    }

    [Theory]
    [InlineData("blank", ComparisonOperator.Blank)]
    [InlineData("notBlank", ComparisonOperator.NotBlank)]
    public void Unary_Operator_Ignores_Null_DateFrom_And_DateTo(string wireType, ComparisonOperator expectedOp)
    {
        var comparison = ParseSingleComparison(
            $$"""{ "filterType": "date", "type": "{{wireType}}", "dateFrom": null, "dateTo": null }""");

        Assert.Equal(expectedOp, comparison.Op);
        Assert.Null(comparison.Value);
    }

    [Fact]
    public void Native_InRange_Throws_UnknownOperator()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(() => ParseSingleComparison(
            """{ "filterType": "date", "type": "inRange", "dateFrom": "2026-01-01", "dateTo": "2026-12-31" }"""));

        Assert.Equal("unknown-operator", ex.Reason);
    }

    [Theory]
    [InlineData("today")]
    [InlineData("last7Days")]
    [InlineData("thisMonth")]
    public void Date_Preset_Type_Throws_UnknownOperator_Deferred_To_AG2(string presetType)
    {
        var ex = Assert.Throws<AgGridRequestParseException>(() => ParseSingleComparison(
            $$"""{ "filterType": "date", "type": "{{presetType}}" }"""));

        Assert.Equal("unknown-operator", ex.Reason);
    }
}
