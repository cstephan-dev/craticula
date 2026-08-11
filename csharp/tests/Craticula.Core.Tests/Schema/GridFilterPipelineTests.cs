using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

/// <summary>
/// Hand-built tests for <see cref="GridFilterPipeline"/> — pins the DoD's actual ask: the linear
/// order <c>customFilters merge -&gt; allowlist -&gt; limits -&gt; coercion -&gt; static injection</c>,
/// each stage's exception surfacing when it is the one that should fire, and the end-to-end happy
/// path producing a fully merged/coerced/statically-injected tree. Each individual stage's own
/// behavior is already exhaustively tested by its own validator's test class; these tests only
/// confirm the wiring and ordering.
/// </summary>
public class GridFilterPipelineTests
{
    private static readonly PageSpec NoPage = new NonePage();
    private static readonly ResourceLimits DefaultLimits = new();

    private static GridSchema MakeSchema(FilterNode? staticFilter = null, GridSchemaMode mode = GridSchemaMode.Strict) => new()
    {
        Mode = mode,
        StableKey = "id",
        StaticFilter = staticFilter,
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = new() { Type = FieldType.Int64 },
            ["region"] = new() { Type = FieldType.String },
            ["quantity"] = new() { Type = FieldType.Int64 },
        }
    };

    private static ComparisonNode Eq(string field, TypedValue value) => new()
    {
        Field = field,
        Op = ComparisonOperator.Eq,
        Value = value,
    };

    [Fact]
    public void Happy_Path_Merges_Allowlists_Coerces_And_Injects_The_StaticFilter()
    {
        var native = Eq("region", new StringValue { Value = "west" });
        var customFilters = Eq("quantity", new StringValue { Value = "10" }); // needs coercion to Int64Value
        var staticFilter = Eq("id", new Int64Value { Value = 1 });
        var schema = MakeSchema(staticFilter);

        var result = Assert.IsType<AndNode>(GridFilterPipeline.Process(native, customFilters, NoPage, schema, DefaultLimits));

        // Outer AND is the static-injection wrap: [mergedAndCoerced, staticFilter].
        Assert.Equal(2, result.Nodes.Count);
        Assert.Same(staticFilter, result.Nodes[1]);

        var mergedAndCoerced = Assert.IsType<AndNode>(result.Nodes[0]);
        Assert.Same(native, mergedAndCoerced.Nodes[0]);
        var coercedQuantity = Assert.IsType<ComparisonNode>(mergedAndCoerced.Nodes[1]);
        Assert.Equal(10L, Assert.IsType<Int64Value>(coercedQuantity.Value).Value);
    }

    [Fact]
    public void Returns_Null_When_No_Filter_And_No_StaticFilter_Are_Present() =>
        Assert.Null(GridFilterPipeline.Process(null, null, NoPage, MakeSchema(), DefaultLimits));

    [Fact]
    public void Unknown_Field_Throws_UnknownFieldException_Before_Limits_Or_Coercion_Run()
    {
        var customFilters = Eq("secretNotes", new StringValue { Value = "irrelevant" });

        Assert.Throws<UnknownFieldException>(
            () => GridFilterPipeline.Process(null, customFilters, NoPage, MakeSchema(), DefaultLimits));
    }

    [Fact]
    public void Oversized_Tree_Throws_ResourceLimitExceededException_Before_Coercion_Runs()
    {
        // Depth 9 (one beyond the SEC-3 default max of 8) built from allowlisted, correctly-typed
        // leaves — this would pass allowlist and coercion cleanly, proving limits gates first.
        FilterNode deepTree = Eq("id", new Int64Value { Value = 1 });
        for (var i = 1; i < 9; i++)
        {
            deepTree = new NotNode { Node = deepTree };
        }

        Assert.Throws<ResourceLimitExceededException>(
            () => GridFilterPipeline.Process(null, deepTree, NoPage, MakeSchema(), DefaultLimits));
    }

    [Fact]
    public void Uncoercible_Value_Throws_TypeCoercionException()
    {
        var customFilters = Eq("id", new StringValue { Value = "not-a-number" });

        Assert.Throws<TypeCoercionException>(
            () => GridFilterPipeline.Process(null, customFilters, NoPage, MakeSchema(), DefaultLimits));
    }

    [Fact]
    public void Malformed_CustomFilters_Throws_FilterNodeValidationException_Before_Allowlist_Runs()
    {
        var malformed = new ComparisonNode { Field = "id", Op = ComparisonOperator.Eq }; // 'eq' requires 'value'

        Assert.Throws<FilterNodeValidationException>(
            () => GridFilterPipeline.Process(null, malformed, NoPage, MakeSchema(), DefaultLimits));
    }

    [Fact]
    public void Lenient_Mode_Strips_An_Unknown_Field_Instead_Of_Throwing()
    {
        var schema = MakeSchema(mode: GridSchemaMode.Lenient);
        var native = Eq("region", new StringValue { Value = "west" });
        var customFilters = Eq("secretNotes", new StringValue { Value = "irrelevant" });

        var result = Assert.IsType<ComparisonNode>(
            GridFilterPipeline.Process(native, customFilters, NoPage, schema, DefaultLimits));

        // The AND merge degenerates to just the surviving native leaf once the unknown-field
        // customFilters leaf is stripped — proving the stripped field never reaches coercion.
        Assert.Same(native, result);
    }

    [Fact]
    public void Lenient_Mode_Stripping_Every_Field_In_An_And_Yields_No_Filter_At_All()
    {
        var schema = MakeSchema(mode: GridSchemaMode.Lenient);
        var customFilters = Eq("secretNotes", new StringValue { Value = "irrelevant" });

        var result = GridFilterPipeline.Process(null, customFilters, NoPage, schema, DefaultLimits);

        Assert.Null(result);
    }
}
