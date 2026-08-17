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
        { "mode": "strict", "source": "items", "stableKey": "id", "fields": { "id": { "type": "int64" } }, "bogus": true }
        """;

        Assert.Throws<JsonException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Rejects_Duplicate_Json_Property()
    {
        const string json = """
        { "source": "items", "stableKey": "id", "stableKey": "other", "fields": { "id": { "type": "int64" } } }
        """;

        Assert.Throws<JsonException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Rejects_Null_For_Required_NonNullable_Property()
    {
        const string json = """
        { "source": "items", "stableKey": "id", "fields": null }
        """;

        Assert.Throws<JsonException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Rejects_Out_Of_Range_Mode_Value()
    {
        const string json = """
        { "mode": 3, "source": "items", "stableKey": "id", "fields": { "id": { "type": "int64" } } }
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
        { "mode": "STRICT", "source": "items", "stableKey": "id", "fields": { "id": { "type": "int64" } } }
        """;

        var schema = GridSchema.Parse(json);

        Assert.Equal(GridSchemaMode.Strict, schema.Mode);
    }

    [Fact]
    public void Rejects_Empty_Fields_Map()
    {
        const string json = """
        { "source": "items", "stableKey": "id", "fields": {} }
        """;

        Assert.Throws<GridSchemaValidationException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Rejects_Blank_StableKey()
    {
        const string json = """
        { "source": "items", "stableKey": "", "fields": { "id": { "type": "int64" } } }
        """;

        Assert.Throws<GridSchemaValidationException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Rejects_Absent_Source()
    {
        // `source` is required in grid-schema.schema.json and `required` on the record, so this is
        // caught by the deserializer rather than by a Parse invariant — pinned because a compiler
        // with no FROM target cannot emit a query at all, and silently defaulting one would let a
        // misconfigured host read the wrong table.
        const string json = """
        { "stableKey": "id", "fields": { "id": { "type": "int64" } } }
        """;

        Assert.Throws<JsonException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Rejects_Blank_Source()
    {
        const string json = """
        { "source": "   ", "stableKey": "id", "fields": { "id": { "type": "int64" } } }
        """;

        Assert.Throws<GridSchemaValidationException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Rejects_StableKey_Absent_From_Fields()
    {
        const string json = """
        { "source": "items", "stableKey": "notAField", "fields": { "id": { "type": "int64" } } }
        """;

        Assert.Throws<GridSchemaValidationException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Rejects_Sql_Mapping_With_Zero_Of_Three_Shapes_Set()
    {
        const string json = """
        { "source": "items", "stableKey": "id", "fields": { "id": { "type": "int64", "physical": { "sql": {} } } } }
        """;

        Assert.Throws<GridSchemaValidationException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void Rejects_Sql_Mapping_With_Multiple_Shapes_Set()
    {
        const string json = """
        { "source": "items", "stableKey": "id", "fields": { "id": {
          "type": "int64", "physical": { "sql": { "column": "id", "expression": "1=1" } }
        } } }
        """;

        Assert.Throws<GridSchemaValidationException>(() => GridSchema.Parse(json));
    }

    /// <summary>
    /// EXP-5's <c>decimalSeparator</c> is one character (<c>maxLength: 1</c> in the meta-schema), and
    /// the deserializer is what enforces it — a <c>char?</c> refuses anything but a single-character
    /// JSON string, exactly as the enum converter refuses an out-of-range <c>mode</c>. Pinned because
    /// the alternative modelling (a <c>string?</c> read as "take the first character") would silently
    /// accept <c>", "</c> and export a thousands separator nobody configured.
    /// </summary>
    [Theory]
    [InlineData("\",.\"")]
    [InlineData("\"\"")]
    [InlineData("1")]
    [InlineData("true")]
    public void Rejects_A_Format_Decimal_Separator_That_Is_Not_A_Single_Character(string wireValue)
    {
        var json = $$"""
        { "source": "items", "stableKey": "id", "fields": { "id": {
          "type": "decimal", "format": { "decimalSeparator": {{wireValue}} }
        } } }
        """;

        Assert.Throws<JsonException>(() => GridSchema.Parse(json));
    }

    /// <summary>
    /// A <c>datePattern</c> no formatter can apply is refused at parse time, not at the first date
    /// cell: the writer streams, so a <see cref="FormatException"/> there would land after the header
    /// row and an arbitrary number of data rows had already reached the client. <c>""</c> and
    /// <c>"   "</c> are refused although they format cleanly — an empty pattern is .NET's alias for the
    /// general format, so accepting it would silently substitute a shape nobody asked for, and the
    /// meta-schema's own <c>minLength: 1</c> says a pattern must be a pattern.
    /// </summary>
    [Theory]
    [InlineData("\"q\"")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void Rejects_A_Format_Date_Pattern_No_Formatter_Can_Apply(string wireValue)
    {
        var json = $$"""
        { "source": "items", "stableKey": "id", "fields": { "id": {
          "type": "date", "format": { "datePattern": {{wireValue}} }
        } } }
        """;

        var ex = Assert.Throws<GridSchemaValidationException>(() => GridSchema.Parse(json));

        // The colId is in the message for the same reason the writer's collision guard names its
        // field: a schema with twenty fields gives a host no other way to find the bad declaration.
        Assert.Contains("fields['id'].format.datePattern", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A pattern naming TIME specifiers is accepted, and must be: a <c>datetime</c> field's
    /// pattern is the natural place for them. It is the reason the writer lifts a <c>DateOnly</c> to
    /// midnight before applying an override rather than probing a <c>DateOnly</c> here — probing one
    /// would reject exactly this.</summary>
    [Fact]
    public void Accepts_A_Format_Date_Pattern_Carrying_Time_Specifiers()
    {
        var schema = GridSchema.Parse("""
        { "source": "items", "stableKey": "id", "fields": { "id": {
          "type": "datetime", "format": { "datePattern": "dd/MM/yyyy HH:mm:ss" }
        } } }
        """);

        Assert.Equal("dd/MM/yyyy HH:mm:ss", schema.Fields["id"].Format!.DatePattern);
    }

    [Fact]
    public void Rejects_An_Unknown_Property_Inside_Format()
    {
        const string json = """
        { "source": "items", "stableKey": "id", "fields": { "id": {
          "type": "decimal", "format": { "thousandsSeparator": " " }
        } } }
        """;

        Assert.Throws<JsonException>(() => GridSchema.Parse(json));
    }

    [Fact]
    public void FieldAllowlistValidator_Defaults_To_Deny_When_Mode_Is_Not_Explicitly_Lenient()
    {
        var schema = GridSchema.Parse("""
        { "mode": "strict", "source": "items", "stableKey": "id", "fields": { "id": { "type": "int64" } } }
        """);

        Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.Validate(schema, ["id", "unknownColumn"]));
    }
}
