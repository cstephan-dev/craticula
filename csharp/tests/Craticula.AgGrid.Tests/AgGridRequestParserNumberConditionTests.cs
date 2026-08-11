using Craticula.Core.Ir;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Hand-built boundary tests for <see cref="AgGridRequestParser.Parse"/>'s <c>number</c> filterType
/// dispatch — in particular AR-7's "every scalar value is a <see cref="StringValue"/> verbatim, even
/// when the wire value is a JSON number" rule (Key design decision #1 of #1635's plan), and native
/// <c>inRange</c>'s explicit rejection (no inclusivity-flag wire path).
/// </summary>
public class AgGridRequestParserNumberConditionTests
{
    private const string Envelope = """
        {{
          "startRow": 0, "endRow": 100,
          "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
          "filterModel": {{ "amount": {0} }},
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
    public void Binary_Operator_Maps_To_Expected_ComparisonOperator(string wireType, ComparisonOperator expectedOp)
    {
        var comparison = ParseSingleComparison($$"""{ "filterType": "number", "type": "{{wireType}}", "filter": 9.99 }""");

        Assert.Equal("amount", comparison.Field);
        Assert.Equal(expectedOp, comparison.Op);
        var value = Assert.IsType<StringValue>(comparison.Value);
        Assert.Equal("9.99", value.Value);
    }

    [Fact]
    public void Integer_Literal_Becomes_StringValue_Without_A_Decimal_Point()
    {
        var comparison = ParseSingleComparison("""{ "filterType": "number", "type": "greaterThan", "filter": 100 }""");

        var value = Assert.IsType<StringValue>(comparison.Value);
        Assert.Equal("100", value.Value);
    }

    [Fact]
    public void Negative_Number_Preserves_Its_Sign()
    {
        var comparison = ParseSingleComparison("""{ "filterType": "number", "type": "lessThan", "filter": -42.5 }""");

        var value = Assert.IsType<StringValue>(comparison.Value);
        Assert.Equal("-42.5", value.Value);
    }

    [Theory]
    [InlineData("blank", ComparisonOperator.Blank)]
    [InlineData("notBlank", ComparisonOperator.NotBlank)]
    public void Unary_Operator_Has_No_Value(string wireType, ComparisonOperator expectedOp)
    {
        var comparison = ParseSingleComparison($$"""{ "filterType": "number", "type": "{{wireType}}" }""");

        Assert.Equal(expectedOp, comparison.Op);
        Assert.Null(comparison.Value);
    }

    [Fact]
    public void Native_InRange_Throws_AgGridRequestParseException_With_UnknownOperator_Reason()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => ParseSingleComparison("""{ "filterType": "number", "type": "inRange", "filter": 10, "filterTo": 20 }"""));

        Assert.Equal("unknown-operator", ex.Reason);
        Assert.Contains("customFilters", ex.Message);
    }

    [Fact]
    public void Unrecognized_Type_Throws_UnknownOperator()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => ParseSingleComparison("""{ "filterType": "number", "type": "greaterThanOrEqualTo", "filter": 5 }"""));

        Assert.Equal("unknown-operator", ex.Reason);
    }

    [Fact]
    public void NonNumeric_String_Filter_Value_Is_Accepted_As_StringValue_For_Downstream_Coercion_To_Reject()
    {
        // spec/fixtures/adversarial/012-non-numeric-string-int64-field deliberately sends a string
        // "filter" against a number filterType, expecting rejection at the typeCoercion stage
        // (TypeCoercionValidator, SEM-5) — NOT here at requestParsing. Rejecting it here would 400 at
        // the wrong stage for a request the conformance suite pins as valid-shaped-but-uncoercible.
        var comparison = ParseSingleComparison("""{ "filterType": "number", "type": "equals", "filter": "not-a-number" }""");

        Assert.Equal("not-a-number", Assert.IsType<StringValue>(comparison.Value).Value);
    }

    [Fact]
    public void NonNumeric_NonString_Filter_Value_Throws_MalformedRequest()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => ParseSingleComparison("""{ "filterType": "number", "type": "equals", "filter": true }"""));

        Assert.Equal("malformed-request", ex.Reason);
    }
}
