using System.Text.Json;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Ir;

/// <summary>
/// Round-trip deserialization tests for <see cref="SortItem"/>, <see cref="SortDirection"/>, and
/// <see cref="NullsPosition"/> against <c>spec/ir.schema.json</c>'s <c>$defs/sortItem</c> shape,
/// using <see cref="GridSchemaJson.Options"/> — mirrors <see cref="FilterNodeDeserializationTests"/>'s
/// style for the sibling <c>Ir</c> types.
/// </summary>
public class SortItemDeserializationTests
{
    private static SortItem ParseSortItem(string json) =>
        JsonSerializer.Deserialize<SortItem>(json, GridSchemaJson.Options)
        ?? throw new JsonException("SortItem JSON deserialized to null.");

    [Fact]
    public void Deserializes_A_SortItem_With_Asc_Direction_And_Last_NullsPosition()
    {
        var item = ParseSortItem("""{ "field": "lastName", "direction": "asc", "nullsPosition": "last" }""");

        Assert.Equal("lastName", item.Field);
        Assert.Equal(SortDirection.Asc, item.Direction);
        Assert.Equal(NullsPosition.Last, item.NullsPosition);
    }

    [Fact]
    public void Deserializes_A_SortItem_With_Desc_Direction_And_First_NullsPosition()
    {
        var item = ParseSortItem("""{ "field": "age", "direction": "desc", "nullsPosition": "first" }""");

        Assert.Equal(SortDirection.Desc, item.Direction);
        Assert.Equal(NullsPosition.First, item.NullsPosition);
    }

    [Fact]
    public void Round_Trips_Through_Serialize_Then_Deserialize()
    {
        var original = new SortItem { Field = "id", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last };

        var json = JsonSerializer.Serialize(original, GridSchemaJson.Options);
        var roundTripped = ParseSortItem(json);

        Assert.Equal(original, roundTripped);
        Assert.Contains("\"direction\":\"asc\"", json);
        Assert.Contains("\"nullsPosition\":\"last\"", json);
    }

    [Fact]
    public void Rejects_Missing_Required_Field() =>
        Assert.Throws<JsonException>(() => ParseSortItem("""{ "direction": "asc", "nullsPosition": "last" }"""));

    [Fact]
    public void Rejects_Unmapped_Property() =>
        Assert.Throws<JsonException>(() => ParseSortItem("""
        { "field": "id", "direction": "asc", "nullsPosition": "last", "bogus": true }
        """));

    [Fact]
    public void Rejects_Unknown_Direction_Value() =>
        Assert.Throws<JsonException>(() => ParseSortItem("""{ "field": "id", "direction": "sideways", "nullsPosition": "last" }"""));

    [Fact]
    public void Rejects_Unknown_NullsPosition_Value() =>
        Assert.Throws<JsonException>(() => ParseSortItem("""{ "field": "id", "direction": "asc", "nullsPosition": "middle" }"""));
}
