using Craticula.Core.Ir;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Hand-built boundary tests for <see cref="AgGridRequestParser.Parse"/>'s combined-form dispatch:
/// the current <c>conditions[]</c> array, the legacy <c>condition1</c>/<c>condition2</c> pair, and the
/// <c>multi</c> filterType's <c>filterModels[]</c> stack — plus multi-column implicit AND across
/// <c>filterModel</c>. AG-1's Definition of Done ("both filter-model forms parse to the same IR") is
/// proven directly by <see cref="Legacy_And_Current_Forms_Produce_Structurally_Identical_Trees"/> and,
/// against real fixtures, by <see cref="AgGridRequestParserFilterTreeFixtureTests"/>.
/// </summary>
public class AgGridRequestParserCombinedFormTests
{
    private static AgGridParsedRequest Parse(string request) => AgGridRequestParser.Parse(request);

    private const string Envelope = """
        {{
          "startRow": 0, "endRow": 100,
          "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
          "filterModel": {0},
          "sortModel": []
        }}
        """;

    [Fact]
    public void Current_Conditions_Array_Form_Combines_Via_OR()
    {
        var request = string.Format(Envelope, """
            {
              "region": {
                "filterType": "text", "operator": "OR",
                "conditions": [
                  { "filterType": "text", "type": "equals", "filter": "west" },
                  { "filterType": "text", "type": "equals", "filter": "east" }
                ]
              }
            }
            """);

        var orNode = Assert.IsType<OrNode>(Parse(request).NativeFilter);
        Assert.Equal(2, orNode.Nodes.Count);
        Assert.Equal("west", Assert.IsType<StringValue>(Assert.IsType<ComparisonNode>(orNode.Nodes[0]).Value).Value);
        Assert.Equal("east", Assert.IsType<StringValue>(Assert.IsType<ComparisonNode>(orNode.Nodes[1]).Value).Value);
    }

    [Fact]
    public void Legacy_Condition1_Condition2_Form_Combines_Via_OR()
    {
        var request = string.Format(Envelope, """
            {
              "region": {
                "filterType": "text", "operator": "OR",
                "condition1": { "filterType": "text", "type": "equals", "filter": "west" },
                "condition2": { "filterType": "text", "type": "equals", "filter": "east" }
              }
            }
            """);

        var orNode = Assert.IsType<OrNode>(Parse(request).NativeFilter);
        Assert.Equal(2, orNode.Nodes.Count);
    }

    [Fact]
    public void Legacy_And_Current_Forms_Produce_Structurally_Identical_Trees()
    {
        var currentRequest = string.Format(Envelope, """
            {
              "region": {
                "filterType": "text", "operator": "OR",
                "conditions": [
                  { "filterType": "text", "type": "equals", "filter": "west" },
                  { "filterType": "text", "type": "equals", "filter": "east" }
                ]
              }
            }
            """);
        var legacyRequest = string.Format(Envelope, """
            {
              "region": {
                "filterType": "text", "operator": "OR",
                "condition1": { "filterType": "text", "type": "equals", "filter": "west" },
                "condition2": { "filterType": "text", "type": "equals", "filter": "east" }
              }
            }
            """);

        var fromCurrent = Parse(currentRequest).NativeFilter;
        var fromLegacy = Parse(legacyRequest).NativeFilter;

        FilterNodeAssert.StructurallyEqual(fromCurrent, fromLegacy);
    }

    [Fact]
    public void Legacy_Form_Missing_Condition2_Throws_MalformedRequest()
    {
        var request = string.Format(Envelope, """
            {
              "region": {
                "filterType": "text", "operator": "OR",
                "condition1": { "filterType": "text", "type": "equals", "filter": "west" }
              }
            }
            """);

        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse(request));
        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void Within_Column_And_Legacy_Form_Combines_Via_AND()
    {
        var request = string.Format(Envelope, """
            {
              "quantity": {
                "filterType": "number", "operator": "AND",
                "condition1": { "filterType": "number", "type": "greaterThanOrEqual", "filter": 100 },
                "condition2": { "filterType": "number", "type": "lessThanOrEqual", "filter": 999 }
              }
            }
            """);

        var andNode = Assert.IsType<AndNode>(Parse(request).NativeFilter);
        Assert.Equal(2, andNode.Nodes.Count);
    }

    [Fact]
    public void Unrecognized_Operator_Value_Throws_MalformedRequest()
    {
        var request = string.Format(Envelope, """
            {
              "region": {
                "filterType": "text", "operator": "XOR",
                "conditions": [
                  { "filterType": "text", "type": "equals", "filter": "west" },
                  { "filterType": "text", "type": "equals", "filter": "east" }
                ]
              }
            }
            """);

        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse(request));
        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void Multi_FilterType_Stacks_FilterModels_Via_Implicit_AND()
    {
        var request = string.Format(Envelope, """
            {
              "customerName": {
                "filterType": "multi",
                "filterModels": [
                  { "filterType": "text", "type": "contains", "filter": "acme" },
                  { "filterType": "text", "type": "notContains", "filter": "test" }
                ]
              }
            }
            """);

        var andNode = Assert.IsType<AndNode>(Parse(request).NativeFilter);
        Assert.Equal(2, andNode.Nodes.Count);
        Assert.Equal(ComparisonOperator.Contains, Assert.IsType<ComparisonNode>(andNode.Nodes[0]).Op);
        Assert.Equal(ComparisonOperator.NotContains, Assert.IsType<ComparisonNode>(andNode.Nodes[1]).Op);
    }

    [Fact]
    public void Multi_FilterType_Skips_An_Inactive_Null_Slot_Rather_Than_Rejecting_It()
    {
        // AG Grid's real MultiFilter (packages/ag-grid-enterprise/multiFilterUtil.ts) leaves an
        // inactive child filter's slot as JSON null — only "every slot is null" means no active
        // filter on this column at all. Only fixture-tree/006 has both slots active, so this
        // single-active-slot shape was previously untested and, before this fix, threw instead of
        // being handled.
        var request = string.Format(Envelope, """
            {
              "customerName": {
                "filterType": "multi",
                "filterModels": [
                  { "filterType": "text", "type": "contains", "filter": "acme" },
                  null
                ]
              }
            }
            """);

        var comparison = Assert.IsType<ComparisonNode>(Parse(request).NativeFilter);
        Assert.Equal(ComparisonOperator.Contains, comparison.Op);
    }

    [Fact]
    public void Multi_FilterType_With_Every_Slot_Null_Contributes_Nothing_To_The_Filter()
    {
        var request = string.Format(Envelope, """
            {
              "customerName": { "filterType": "multi", "filterModels": [null, null] }
            }
            """);

        Assert.Null(Parse(request).NativeFilter);
    }

    [Fact]
    public void Multi_FilterType_Slot_Can_Itself_Be_A_Combined_Form()
    {
        // A multi slot is a full child filter model, not guaranteed to be a bare single condition —
        // it can be a current-form conditions[] combined filter in its own right.
        var request = string.Format(Envelope, """
            {
              "customerName": {
                "filterType": "multi",
                "filterModels": [
                  {
                    "filterType": "text", "operator": "OR",
                    "conditions": [
                      { "filterType": "text", "type": "equals", "filter": "acme" },
                      { "filterType": "text", "type": "equals", "filter": "acme-corp" }
                    ]
                  },
                  { "filterType": "text", "type": "notContains", "filter": "test" }
                ]
              }
            }
            """);

        var outer = Assert.IsType<AndNode>(Parse(request).NativeFilter);
        Assert.Equal(2, outer.Nodes.Count);
        var orNode = Assert.IsType<OrNode>(outer.Nodes[0]);
        Assert.Equal(2, orNode.Nodes.Count);
    }

    [Fact]
    public void Multiple_Columns_Combine_Via_Implicit_AND_Across_The_Whole_FilterModel()
    {
        var request = string.Format(Envelope, """
            {
              "customerName": { "filterType": "text", "type": "contains", "filter": "acme" },
              "amount": { "filterType": "number", "type": "greaterThan", "filter": 100 },
              "status": { "filterType": "text", "type": "equals", "filter": "active" }
            }
            """);

        var andNode = Assert.IsType<AndNode>(Parse(request).NativeFilter);
        Assert.Equal(3, andNode.Nodes.Count);
    }

    [Fact]
    public void Single_Column_Is_Unwrapped_Not_Wrapped_In_A_Spurious_AndNode()
    {
        var request = string.Format(Envelope, """{ "status": { "filterType": "text", "type": "equals", "filter": "active" } }""");

        Assert.IsType<ComparisonNode>(Parse(request).NativeFilter);
    }

    [Fact]
    public void Null_FilterModel_Produces_Null_NativeFilter()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": null,
              "sortModel": []
            }
            """;

        Assert.Null(Parse(request).NativeFilter);
    }

    [Fact]
    public void Empty_FilterModel_Object_Produces_Null_NativeFilter()
    {
        var request = string.Format(Envelope, "{}");

        Assert.Null(Parse(request).NativeFilter);
    }
}
