using System.Text.Json;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

/// <summary>
/// GridSchema is the security/typing boundary (SC-1, SEC-1) — these cases pin the "must reject"
/// side that <see cref="GridSchemaDeserializationTests"/>'s happy-path cases don't cover. Each one
/// corresponds to a finding from the #1631 code review: a lax deserializer or a missing
/// post-deserialization invariant check would silently accept exactly these malformed documents.
/// </summary>
public class GridSchemaEdgeCaseTests
{
    [Fact]
    public void Rejects_Unknown_Top_Level_Property()
    {
        const string json = """
        { "mode": "strict", "stableKey": "id", "fields": { "id": { "type": "int64" } }, "bogus": true }
        """;

        Assert.Throws<JsonException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Rejects_Duplicate_Json_Property()
    {
        const string json = """
        { "stableKey": "id", "stableKey": "other", "fields": { "id": { "type": "int64" } } }
        """;

        Assert.Throws<JsonException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Rejects_Null_For_Required_NonNullable_Property()
    {
        const string json = """
        { "stableKey": "id", "fields": null }
        """;

        Assert.Throws<JsonException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Rejects_Out_Of_Range_Mode_Value()
    {
        const string json = """
        { "mode": 3, "stableKey": "id", "fields": { "id": { "type": "int64" } } }
        """;

        Assert.Throws<JsonException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Accepts_Wrong_Case_Mode_Value_As_Case_Insensitive_Match()
    {
        // JsonStringEnumConverter's read path is inherently case-insensitive — confirmed via a
        // scratch probe against the exact converter configuration GridSchemaJson.Options uses;
        // there is no public option to make it case-sensitive short of a fully custom converter.
        // This is accepted (not fixed): unlike an out-of-range integer, "STRICT" carries the same
        // semantic intent as "strict", so mis-cased-but-recognizable input resolving correctly is
        // a convenience, not a security hole the way silent integer coercion would be.
        const string json = """
        { "mode": "STRICT", "stableKey": "id", "fields": { "id": { "type": "int64" } } }
        """;

        var schema = GridSchema.Parse(json);

        Assert.Equal(GridSchemaMode.Strict, schema.Mode);
    }

    [Fact]
    public void Rejects_Empty_Fields_Map()
    {
        const string json = """
        { "stableKey": "id", "fields": {} }
        """;

        Assert.Throws<GridSchemaValidationException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Rejects_Blank_StableKey()
    {
        const string json = """
        { "stableKey": "", "fields": { "id": { "type": "int64" } } }
        """;

        Assert.Throws<GridSchemaValidationException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Rejects_StableKey_Absent_From_Fields()
    {
        const string json = """
        { "stableKey": "notAField", "fields": { "id": { "type": "int64" } } }
        """;

        Assert.Throws<GridSchemaValidationException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Rejects_Sql_Mapping_With_Zero_Of_Three_Shapes_Set()
    {
        const string json = """
        { "stableKey": "id", "fields": { "id": { "type": "int64", "physical": { "sql": {} } } } }
        """;

        Assert.Throws<GridSchemaValidationException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Rejects_Sql_Mapping_With_Multiple_Shapes_Set()
    {
        const string json = """
        { "stableKey": "id", "fields": { "id": {
          "type": "int64", "physical": { "sql": { "column": "id", "expression": "1=1" } }
        } } }
        """;

        Assert.Throws<GridSchemaValidationException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void FieldAllowlistValidator_Defaults_To_Deny_When_Mode_Is_Not_Explicitly_Lenient()
    {
        var schema = GridSchema.Parse("""
        { "mode": "strict", "stableKey": "id", "fields": { "id": { "type": "int64" } } }
        """);

        Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.Validate(schema, ["id", "unknownColumn"]));
    }
}
