using Craticula.Core.Ir;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Hand-built boundary tests for <see cref="AgGridRequestParser.Parse"/>'s <c>set</c> filterType
/// dispatch — always <see cref="ComparisonOperator.In"/>, values wrapped as <see cref="StringValue"/>
/// verbatim (this is also how boolean columns are filtered on the real wire, per the plan's Key
/// design decision #1 — this parser never rewrites a boolean-shaped set selection into AR-6's
/// dedicated <c>true</c>/<c>false</c> unary operator; that translation, if ever needed, belongs to a
/// later semantic/compiler stage, not request parsing).
/// </summary>
public class AgGridRequestParserSetConditionTests
{
    private const string Envelope = """
        {{
          "startRow": 0, "endRow": 100,
          "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
          "filterModel": {{ "status": {0} }},
          "sortModel": []
        }}
        """;

    private static ComparisonNode ParseSingleComparison(string entryJson)
    {
        var request = string.Format(Envelope, entryJson);
        var parsed = AgGridRequestParser.Parse(request);
        return Assert.IsType<ComparisonNode>(parsed.NativeFilter);
    }

    [Fact]
    public void Values_Array_Maps_To_In_With_StringValue_Elements()
    {
        var comparison = ParseSingleComparison("""{ "filterType": "set", "values": ["active", "pending"] }""");

        Assert.Equal("status", comparison.Field);
        Assert.Equal(ComparisonOperator.In, comparison.Op);
        Assert.Equal(2, comparison.Values!.Count);
        Assert.Equal("active", Assert.IsType<StringValue>(comparison.Values[0]).Value);
        Assert.Equal("pending", Assert.IsType<StringValue>(comparison.Values[1]).Value);
    }

    [Fact]
    public void Single_Boolean_Like_Value_Still_Maps_To_In_Not_To_Unary_True()
    {
        var comparison = ParseSingleComparison("""{ "filterType": "set", "values": ["true"] }""");

        Assert.Equal(ComparisonOperator.In, comparison.Op);
        Assert.Equal("true", Assert.IsType<StringValue>(comparison.Values![0]).Value);
    }

    [Fact]
    public void Null_Values_Throws_MalformedRequest()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => ParseSingleComparison("""{ "filterType": "set", "values": null }"""));

        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void NonArray_Values_Throws_MalformedRequest()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => ParseSingleComparison("""{ "filterType": "set", "values": "active" }"""));

        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void NonString_Values_Element_Throws_MalformedRequest()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => ParseSingleComparison("""{ "filterType": "set", "values": [1, 2] }"""));

        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void Empty_Values_Array_Passes_Parsing_But_Fails_The_Defense_In_Depth_FilterNodeValidator_Check()
    {
        // The parser itself does not special-case an empty 'values' array — it flows through as a
        // ComparisonNode with Values=[] and is caught by the FilterNodeValidator.Validate call
        // AgGridRequestParser.Parse itself runs as defense in depth (mirrors every other public IR
        // entry point in this codebase), giving a more specific downstream error than a bespoke
        // parser-level check would.
        var request = string.Format(Envelope, """{ "filterType": "set", "values": [] }""");

        Assert.Throws<FilterNodeValidationException>(() => AgGridRequestParser.Parse(request));
    }
}
