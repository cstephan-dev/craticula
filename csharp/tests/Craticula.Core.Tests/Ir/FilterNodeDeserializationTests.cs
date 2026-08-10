using System.Text.Json;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Ir;

/// <summary>
/// Round-trip deserialization tests for <see cref="FilterNode"/>, <see cref="TypedValue"/>, and
/// <see cref="PageSpec"/> against <c>spec/ir.schema.json</c>'s shapes, using
/// <see cref="GridSchemaJson.Options"/> (the same fail-closed camelCase options
/// <see cref="GridSchema"/> itself uses, since <c>grid-schema.schema.json</c> $refs into
/// <c>ir.schema.json</c>) — mirrors <see cref="Schema.GridSchemaDeserializationTests"/>'s style for
/// the sibling <c>Schema</c> types.
/// </summary>
public class FilterNodeDeserializationTests
{
    private static FilterNode ParseFilterNode(string json) =>
        JsonSerializer.Deserialize<FilterNode>(json, GridSchemaJson.Options)
        ?? throw new JsonException("FilterNode JSON deserialized to null.");

    private static TypedValue ParseTypedValue(string json) =>
        JsonSerializer.Deserialize<TypedValue>(json, GridSchemaJson.Options)
        ?? throw new JsonException("TypedValue JSON deserialized to null.");

    private static PageSpec ParsePageSpec(string json) =>
        JsonSerializer.Deserialize<PageSpec>(json, GridSchemaJson.Options)
        ?? throw new JsonException("PageSpec JSON deserialized to null.");

    [Fact]
    public void Deserializes_AndNode_With_Two_Comparison_Children()
    {
        var node = ParseFilterNode("""
        {
          "type": "and",
          "nodes": [
            { "type": "comparison", "field": "region", "op": "eq", "value": { "type": "string", "value": "west" } },
            { "type": "comparison", "field": "quantity", "op": "gt", "value": { "type": "int64", "value": 10 } }
          ]
        }
        """);

        var and = Assert.IsType<AndNode>(node);
        Assert.Equal(2, and.Nodes.Count);
        Assert.IsType<ComparisonNode>(and.Nodes[0]);
        Assert.IsType<ComparisonNode>(and.Nodes[1]);
    }

    [Fact]
    public void Deserializes_OrNode_With_Two_Comparison_Children()
    {
        var node = ParseFilterNode("""
        {
          "type": "or",
          "nodes": [
            { "type": "comparison", "field": "region", "op": "eq", "value": { "type": "string", "value": "west" } },
            { "type": "comparison", "field": "region", "op": "eq", "value": { "type": "string", "value": "east" } }
          ]
        }
        """);

        var or = Assert.IsType<OrNode>(node);
        Assert.Equal(2, or.Nodes.Count);
    }

    [Fact]
    public void Deserializes_NotNode_Wrapping_A_Comparison()
    {
        var node = ParseFilterNode("""
        { "type": "not", "node": { "type": "comparison", "field": "region", "op": "blank" } }
        """);

        var not = Assert.IsType<NotNode>(node);
        var comparison = Assert.IsType<ComparisonNode>(not.Node);
        Assert.Equal("region", comparison.Field);
        Assert.Equal(ComparisonOperator.Blank, comparison.Op);
    }

    [Fact]
    public void Deserializes_Nested_And_Or_Not_Tree()
    {
        var node = ParseFilterNode("""
        {
          "type": "not",
          "node": {
            "type": "or",
            "nodes": [
              { "type": "and", "nodes": [
                { "type": "comparison", "field": "a", "op": "eq", "value": { "type": "int64", "value": 1 } }
              ] },
              { "type": "comparison", "field": "b", "op": "eq", "value": { "type": "int64", "value": 2 } }
            ]
          }
        }
        """);

        var not = Assert.IsType<NotNode>(node);
        var or = Assert.IsType<OrNode>(not.Node);
        Assert.Equal(2, or.Nodes.Count);
        var and = Assert.IsType<AndNode>(or.Nodes[0]);
        Assert.Single(and.Nodes);
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("notBlank")]
    [InlineData("true")]
    [InlineData("false")]
    public void Deserializes_Unary_Comparison_With_No_Value(string op)
    {
        var node = ParseFilterNode($$"""{ "type": "comparison", "field": "active", "op": "{{op}}" }""");

        var comparison = Assert.IsType<ComparisonNode>(node);
        Assert.Null(comparison.Value);
        Assert.Null(comparison.Values);
    }

    [Fact]
    public void Deserializes_Binary_Comparison_With_A_Single_Value()
    {
        var node = ParseFilterNode("""
        { "type": "comparison", "field": "name", "op": "startsWith", "value": { "type": "string", "value": "Acme" } }
        """);

        var comparison = Assert.IsType<ComparisonNode>(node);
        Assert.Equal(ComparisonOperator.StartsWith, comparison.Op);
        Assert.Equal("Acme", Assert.IsType<StringValue>(comparison.Value).Value);
    }

    [Theory]
    [InlineData("in")]
    [InlineData("notIn")]
    public void Deserializes_Set_Comparison_With_A_Values_Array(string op)
    {
        var node = ParseFilterNode($$"""
        {
          "type": "comparison", "field": "region", "op": "{{op}}",
          "values": [
            { "type": "string", "value": "west" },
            { "type": "string", "value": "east" }
          ]
        }
        """);

        var comparison = Assert.IsType<ComparisonNode>(node);
        Assert.Equal(2, comparison.Values!.Count);
        Assert.Null(comparison.Value);
    }

    [Fact]
    public void Deserializes_Range_Comparison_With_From_To_And_Inclusivity_Flags()
    {
        var node = ParseFilterNode("""
        {
          "type": "comparison", "field": "amount", "op": "inRange",
          "from": { "type": "decimal", "value": "10.00" },
          "to": { "type": "decimal", "value": "20.00" },
          "fromInclusive": true,
          "toInclusive": false
        }
        """);

        var comparison = Assert.IsType<ComparisonNode>(node);
        Assert.Equal(ComparisonOperator.InRange, comparison.Op);
        Assert.Equal("10.00", Assert.IsType<DecimalValue>(comparison.From).Value);
        Assert.Equal("20.00", Assert.IsType<DecimalValue>(comparison.To).Value);
        Assert.True(comparison.FromInclusive);
        Assert.False(comparison.ToInclusive);
    }

    [Fact]
    public void Deserializes_StringValue() =>
        Assert.Equal("hello", Assert.IsType<StringValue>(ParseTypedValue("""{ "type": "string", "value": "hello" }""")).Value);

    [Fact]
    public void Deserializes_Int64Value() =>
        Assert.Equal(42L, Assert.IsType<Int64Value>(ParseTypedValue("""{ "type": "int64", "value": 42 }""")).Value);

    [Fact]
    public void Deserializes_DecimalValue_As_A_Numeric_String() =>
        Assert.Equal("10.50", Assert.IsType<DecimalValue>(ParseTypedValue("""{ "type": "decimal", "value": "10.50" }""")).Value);

    [Fact]
    public void Deserializes_DoubleValue() =>
        Assert.Equal(3.14, Assert.IsType<DoubleValue>(ParseTypedValue("""{ "type": "double", "value": 3.14 }""")).Value);

    [Fact]
    public void Deserializes_BooleanValue() =>
        Assert.True(Assert.IsType<BooleanValue>(ParseTypedValue("""{ "type": "boolean", "value": true }""")).Value);

    [Fact]
    public void Deserializes_DateValue() =>
        Assert.Equal("2026-08-11", Assert.IsType<DateValue>(ParseTypedValue("""{ "type": "date", "value": "2026-08-11" }""")).Value);

    [Fact]
    public void Deserializes_DatetimeValue_With_Zone()
    {
        var value = Assert.IsType<DatetimeValue>(ParseTypedValue("""
        { "type": "datetime", "value": "2026-08-11T10:00:00Z", "zone": "Europe/Paris" }
        """));

        Assert.Equal("2026-08-11T10:00:00Z", value.Value);
        Assert.Equal("Europe/Paris", value.Zone);
    }

    [Fact]
    public void Deserializes_NullValue() =>
        Assert.IsType<NullValue>(ParseTypedValue("""{ "type": "null" }"""));

    [Fact]
    public void Deserializes_RangePage()
    {
        var page = Assert.IsType<RangePage>(ParsePageSpec("""{ "mode": "range", "startRow": 0, "endRow": 100 }"""));

        Assert.Equal(0, page.StartRow);
        Assert.Equal(100, page.EndRow);
    }

    [Fact]
    public void Deserializes_NonePage() =>
        Assert.IsType<NonePage>(ParsePageSpec("""{ "mode": "none" }"""));

    [Fact]
    public void Rejects_Unknown_TypedValue_Type_Discriminator() =>
        Assert.Throws<JsonException>(() => ParseTypedValue("""{ "type": "bogus", "value": 1 }"""));

    [Fact]
    public void Rejects_Unmapped_Property_On_A_Comparison_Node() =>
        Assert.Throws<JsonException>(() => ParseFilterNode("""
        { "type": "comparison", "field": "region", "op": "blank", "bogus": true }
        """));
}
