using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

public class FieldAllowlistValidatorTests
{
    private static ComparisonNode Eq(string field) => new()
    {
        Field = field,
        Op = ComparisonOperator.Eq,
        Value = new Int64Value { Value = 1 },
    };

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

    [Fact]
    public void Strip_ReturnsTheTreeUnchanged_WhenNothingIsStripped()
    {
        var tree = Eq("id");

        Assert.Same(tree, FieldAllowlistValidator.Strip(tree, []));
    }

    [Fact]
    public void Strip_RemovesTheLeafReferencingAStrippedField_FromAnAndsNodeList()
    {
        var kept = Eq("id");
        var tree = new AndNode { Nodes = [kept, Eq("secretNotes")] };

        var result = Assert.IsType<ComparisonNode>(FieldAllowlistValidator.Strip(tree, ["secretNotes"]));

        // Down to a single surviving child: unwrapped, not left as a spurious 1-child And.
        Assert.Same(kept, result);
    }

    [Fact]
    public void Strip_CollapsesAnAndNodeToNull_WhenEveryChildIsStripped()
    {
        var tree = new AndNode { Nodes = [Eq("secretNotes"), Eq("internalRank")] };

        Assert.Null(FieldAllowlistValidator.Strip(tree, ["secretNotes", "internalRank"]));
    }

    [Fact]
    public void Strip_DropsAWholeNotNode_WhenItsWrappedConditionIsFullyStripped()
    {
        // Not(nothing) has no coherent meaning under "silently ignore the unknown condition" — it
        // must disappear too, not flip into an always-false/always-true sentinel.
        var tree = new NotNode { Node = Eq("secretNotes") };

        Assert.Null(FieldAllowlistValidator.Strip(tree, ["secretNotes"]));
    }

    [Fact]
    public void Strip_LeavesAnUnrelatedBranchUntouched_ByReference()
    {
        var untouched = Eq("id");
        var tree = new AndNode { Nodes = [untouched, Eq("name"), Eq("secretNotes")] };

        var result = Assert.IsType<AndNode>(FieldAllowlistValidator.Strip(tree, ["secretNotes"]));

        Assert.Equal(2, result.Nodes.Count);
        Assert.Same(untouched, result.Nodes[0]);
    }
}
