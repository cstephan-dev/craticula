using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

/// <summary>
/// Hand-built tests for <see cref="StaticFilterInjector"/> — thin by design, since it is a direct
/// call to <see cref="FilterTreeMerge.And"/> (already exhaustively tested in
/// <see cref="Ir.FilterTreeMergeTests"/>); these only confirm the specific wiring (which side is
/// which, and the <see cref="GridSchema.StaticFilter"/> source) rather than re-proving the merge
/// rules themselves.
/// </summary>
public class StaticFilterInjectorTests
{
    private static ComparisonNode Leaf(string field) => new()
    {
        Field = field,
        Op = ComparisonOperator.Eq,
        Value = new Int64Value { Value = 1 },
    };

    private static GridSchema MakeSchema(FilterNode? staticFilter) => new()
    {
        StableKey = "id",
        StaticFilter = staticFilter,
        Fields = new Dictionary<string, FieldConfig> { ["id"] = new() { Type = FieldType.Int64 } },
    };

    [Fact]
    public void Injects_StaticFilter_As_The_Second_And_Operand()
    {
        var filter = Leaf("region");
        var staticFilter = Leaf("tenantId");
        var schema = MakeSchema(staticFilter);

        var result = Assert.IsType<AndNode>(StaticFilterInjector.Inject(filter, schema));

        Assert.Equal(2, result.Nodes.Count);
        Assert.Same(filter, result.Nodes[0]);
        Assert.Same(staticFilter, result.Nodes[1]);
    }

    [Fact]
    public void Returns_The_Filter_Unchanged_When_Schema_Has_No_StaticFilter()
    {
        var filter = Leaf("region");
        var schema = MakeSchema(null);

        Assert.Same(filter, StaticFilterInjector.Inject(filter, schema));
    }

    [Fact]
    public void Returns_The_StaticFilter_Unwrapped_When_The_Filter_Is_Null()
    {
        var staticFilter = Leaf("tenantId");
        var schema = MakeSchema(staticFilter);

        Assert.Same(staticFilter, StaticFilterInjector.Inject(null, schema));
    }

    [Fact]
    public void Returns_Null_When_Neither_The_Filter_Nor_The_StaticFilter_Is_Present() =>
        Assert.Null(StaticFilterInjector.Inject(null, MakeSchema(null)));

    [Fact]
    public void Malformed_StaticFilter_Throws_FilterNodeValidationException_Even_When_Schema_Bypassed_Parse()
    {
        // GridSchema.Parse would normally catch this at deserialization time — this schema is built
        // directly (as every test in this file does), the way a caller who never goes through JSON
        // deserialization could too, so Inject must not blindly trust an unvalidated StaticFilter.
        var malformedStaticFilter = new ComparisonNode { Field = "tenantId", Op = ComparisonOperator.Eq }; // 'eq' requires 'value'
        var schema = MakeSchema(malformedStaticFilter);

        Assert.Throws<FilterNodeValidationException>(() => StaticFilterInjector.Inject(Leaf("region"), schema));
    }
}
