using Craticula.Core.Ir;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Hand-built boundary tests for <see cref="AgGridRequestParser.Parse"/>'s <c>text</c> filterType
/// dispatch (AR-6's operator vocabulary on a <c>string</c> field) — real-fixture-driven coverage of
/// the same operators lives in <see cref="AgGridRequestParserOperatorsFixtureTests"/>.
/// </summary>
public class AgGridRequestParserTextConditionTests
{
    private const string Envelope = """
        {{
          "startRow": 0, "endRow": 100,
          "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
          "filterModel": {{ "name": {0} }},
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
    [InlineData("contains", ComparisonOperator.Contains)]
    [InlineData("notContains", ComparisonOperator.NotContains)]
    [InlineData("startsWith", ComparisonOperator.StartsWith)]
    [InlineData("endsWith", ComparisonOperator.EndsWith)]
    public void Binary_Operator_Maps_To_Expected_ComparisonOperator_With_StringValue(string wireType, ComparisonOperator expectedOp)
    {
        var comparison = ParseSingleComparison($$"""{ "filterType": "text", "type": "{{wireType}}", "filter": "acme" }""");

        Assert.Equal("name", comparison.Field);
        Assert.Equal(expectedOp, comparison.Op);
        var value = Assert.IsType<StringValue>(comparison.Value);
        Assert.Equal("acme", value.Value);
    }

    [Theory]
    [InlineData("blank", ComparisonOperator.Blank)]
    [InlineData("notBlank", ComparisonOperator.NotBlank)]
    public void Unary_Operator_Has_No_Value(string wireType, ComparisonOperator expectedOp)
    {
        var comparison = ParseSingleComparison($$"""{ "filterType": "text", "type": "{{wireType}}" }""");

        Assert.Equal(expectedOp, comparison.Op);
        Assert.Null(comparison.Value);
    }

    [Fact]
    public void Unrecognized_Type_Throws_AgGridRequestParseException_With_UnknownOperator_Reason()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => ParseSingleComparison("""{ "filterType": "text", "type": "regex", "filter": "^a.*z$" }"""));

        Assert.Equal("unknown-operator", ex.Reason);
    }

    [Fact]
    public void Filter_Value_As_Object_Throws_MalformedRequest()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => ParseSingleComparison("""{ "filterType": "text", "type": "equals", "filter": { "$ne": null } }"""));

        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void Filter_Value_As_Array_Throws_MalformedRequest()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => ParseSingleComparison("""{ "filterType": "text", "type": "equals", "filter": ["a", "b", "c"] }"""));

        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void Unrecognized_FilterType_Throws_UnknownOperator()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => ParseSingleComparison("""{ "filterType": "advanced", "type": "equals", "filter": "x" }"""));

        Assert.Equal("unknown-operator", ex.Reason);
    }
}
