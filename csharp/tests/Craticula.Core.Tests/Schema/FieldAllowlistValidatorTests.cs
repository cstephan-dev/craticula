using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

public class FieldAllowlistValidatorTests
{
    private static GridSchema MakeSchema(GridSchemaMode mode) => new()
    {
        Mode = mode,
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = new() { Type = FieldType.Int64 },
            ["name"] = new() { Type = FieldType.String }
        }
    };

    [Fact]
    public void Strict_DoesNotThrow_WhenAllColIdsAreListed()
    {
        var schema = MakeSchema(GridSchemaMode.Strict);

        var result = FieldAllowlistValidator.Validate(schema, ["id", "name"]);

        Assert.Equal(["id", "name"], result.Allowed);
        Assert.Empty(result.Stripped);
    }

    [Fact]
    public void Strict_Throws_UnknownFieldException_ForAnUnlistedColId()
    {
        var schema = MakeSchema(GridSchemaMode.Strict);

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.Validate(schema, ["id", "secretNotes"]));

        Assert.Equal(["secretNotes"], ex.ColIds);
    }

    [Fact]
    public void Strict_CollectsEveryUnlistedColId_NotJustTheFirst()
    {
        var schema = MakeSchema(GridSchemaMode.Strict);

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.Validate(schema, ["secretNotes", "id", "internalRank"]));

        Assert.Equal(["secretNotes", "internalRank"], ex.ColIds);
    }

    [Fact]
    public void Lenient_StripsUnknownColIds_AndKeepsKnownOnes_WithoutThrowing()
    {
        var schema = MakeSchema(GridSchemaMode.Lenient);

        var result = FieldAllowlistValidator.Validate(schema, ["id", "secretNotes", "name"]);

        Assert.Equal(["id", "name"], result.Allowed);
        Assert.Equal(["secretNotes"], result.Stripped);
    }
}
