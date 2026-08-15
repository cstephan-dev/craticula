using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

/// <summary>
/// <see cref="GridFilterPipeline.ProcessSetValues"/> — GRP-6's stage order
/// (<c>merge -&gt; structural colId -&gt; own-field exclusion -&gt; search prefix -&gt; allowlist
/// -&gt; limits -&gt; coercion -&gt; static injection</c>) and the four decisions in it that no
/// artifact can show: that the target colId is refused in <em>every</em> schema mode, that the
/// prefix predicate cannot be stripped, that the exclusion runs BEFORE the limits check, and that a
/// prefix against a non-text column is a staged client rejection rather than a compiler precondition
/// failure. Each stage's own behavior is tested by its own validator's class; this file is the
/// wiring.
/// </summary>
public class GridFilterPipelineSetValuesTests
{
    private static readonly ResourceLimits DefaultLimits = new();

    private static GridSchema MakeSchema(
        FilterNode? staticFilter = null, GridSchemaMode mode = GridSchemaMode.Strict) => new()
    {
        Mode = mode,
        Source = "sales_fact",
        StableKey = "id",
        StaticFilter = staticFilter,
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = new() { Type = FieldType.Int64 },
            ["region"] = new() { Type = FieldType.String },
            ["segment"] = new() { Type = FieldType.String },
            ["fiscalYear"] = new() { Type = FieldType.Int64 }
        }
    };

    private static ComparisonNode Eq(string field, TypedValue value) =>
        new() { Field = field, Op = ComparisonOperator.Eq, Value = value };

    private static SetValuesSpec Spec(string colId = "region", string? searchPrefix = null) =>
        new() { ColId = colId, SearchPrefix = searchPrefix };

    [Fact]
    public void The_Target_Columns_Own_Filter_Is_Dropped_And_Every_Other_Columns_Survives()
    {
        var model = new AndNode
        {
            Nodes = [Eq("region", new StringValue { Value = "EMEA" }), Eq("segment", new StringValue { Value = "Enterprise" })]
        };

        var result = Assert.IsType<ComparisonNode>(
            GridFilterPipeline.ProcessSetValues(model, null, Spec(), MakeSchema(), DefaultLimits));

        Assert.Equal("segment", result.Field);
    }

    [Fact]
    public void A_CustomFilters_Fragment_Is_Merged_Before_The_Exclusion_So_It_Is_Scoped_Too()
    {
        var result = GridFilterPipeline.ProcessSetValues(
            Eq("segment", new StringValue { Value = "Enterprise" }),
            Eq("region", new StringValue { Value = "EMEA" }),
            Spec(),
            MakeSchema(),
            DefaultLimits);

        Assert.Equal("segment", Assert.IsType<ComparisonNode>(result).Field);
    }

    /// <summary>
    /// The user's filter leads and the prefix trails — GRP-6 states the order because it is visible
    /// in the compiled <c>params</c>, which the fixture suite compares exactly.
    /// </summary>
    [Fact]
    public void The_SearchPrefix_Is_Appended_After_The_Users_Own_Filter()
    {
        var result = Assert.IsType<AndNode>(GridFilterPipeline.ProcessSetValues(
            Eq("segment", new StringValue { Value = "Enterprise" }),
            null,
            Spec(searchPrefix: "eu"),
            MakeSchema(),
            DefaultLimits));

        Assert.Equal("segment", Assert.IsType<ComparisonNode>(result.Nodes[0]).Field);

        var prefix = Assert.IsType<ComparisonNode>(result.Nodes[1]);
        Assert.Equal("region", prefix.Field);
        Assert.Equal(ComparisonOperator.StartsWith, prefix.Op);
    }

    /// <summary>
    /// The exclusion drops the target column's conditions; the prefix is on that same column and must
    /// NOT be dropped with them, which is why it is injected after the exclusion rather than merged
    /// into the model beforehand.
    /// </summary>
    [Fact]
    public void The_SearchPrefix_Survives_The_Exclusion_Of_Its_Own_Column()
    {
        var result = Assert.IsType<ComparisonNode>(GridFilterPipeline.ProcessSetValues(
            Eq("region", new StringValue { Value = "EMEA" }),
            null,
            Spec(searchPrefix: "eu"),
            MakeSchema(),
            DefaultLimits));

        Assert.Equal(ComparisonOperator.StartsWith, result.Op);
    }

    /// <summary>
    /// Lenient mode strips an unknown <em>condition</em>; the target colId is the question itself, so
    /// there is no narrowed answer to give and every mode refuses it. Asserted in BOTH modes, because
    /// the Strict half passes for the ordinary allowlist reason and would hide a missing structural
    /// check.
    /// </summary>
    [Theory]
    [InlineData(GridSchemaMode.Strict)]
    [InlineData(GridSchemaMode.Lenient)]
    public void An_Unknown_Target_ColId_Is_Refused_In_Every_Schema_Mode(GridSchemaMode mode)
    {
        var ex = Assert.Throws<UnknownFieldException>(() => GridFilterPipeline.ProcessSetValues(
            null, null, Spec(colId: "ssn"), MakeSchema(mode: mode), DefaultLimits));

        Assert.Contains("setValuesRequest.colId", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lenient still strips an unknown colId inside the filter MODEL — the contract it does have —
    /// so the two behaviours coexist in one request rather than one overriding the other.
    /// </summary>
    [Fact]
    public void Lenient_Still_Strips_An_Unknown_Condition_While_Refusing_An_Unknown_Target()
    {
        var result = GridFilterPipeline.ProcessSetValues(
            Eq("ssn", new StringValue { Value = "x" }),
            null,
            Spec(),
            MakeSchema(mode: GridSchemaMode.Lenient),
            DefaultLimits);

        Assert.Null(result);
    }

    /// <summary>
    /// SEM-7 has no meaning against a non-text column, and the check is explicit rather than left to
    /// coercion: <c>"2024"</c> would coerce cleanly into an <c>int64</c> and then hit the compiler's
    /// declared-type substring guard — a caller-precondition failure on ordinary client input.
    /// </summary>
    [Fact]
    public void A_SearchPrefix_Against_A_Non_Text_Column_Is_Rejected_At_The_TypeCoercion_Stage()
    {
        var ex = Assert.Throws<TypeCoercionException>(() => GridFilterPipeline.ProcessSetValues(
            null, null, Spec(colId: "fiscalYear", searchPrefix: "2024"), MakeSchema(), DefaultLimits));

        Assert.Equal("uncoercible-value", ex.Violations[0].Reason);
        Assert.Equal("fiscalYear", ex.Violations[0].Field);
    }

    [Fact]
    public void The_Surviving_Filter_Is_Coerced_And_The_StaticFilter_Is_Injected_Exactly_As_On_The_Row_Path()
    {
        var staticFilter = Eq("id", new Int64Value { Value = 1 });

        var result = Assert.IsType<AndNode>(GridFilterPipeline.ProcessSetValues(
            Eq("fiscalYear", new StringValue { Value = "2024" }),
            null,
            Spec(),
            MakeSchema(staticFilter),
            DefaultLimits));

        Assert.Equal(2024L, Assert.IsType<Int64Value>(Assert.IsType<ComparisonNode>(result.Nodes[0]).Value).Value);
        Assert.Same(staticFilter, result.Nodes[1]);
    }

    /// <summary>
    /// SEC-3 is charged over the tree that actually compiles, i.e. AFTER the exclusion. A filter model
    /// that is over budget only because of conditions this query is about to discard is answered, not
    /// rejected — a set filter must not 400 over a filter the user's own grid legitimately shows.
    /// </summary>
    [Fact]
    public void Limits_Are_Charged_After_The_Exclusion_Not_Before_It()
    {
        var limits = new ResourceLimits { MaxLeaves = 2 };
        var model = new AndNode
        {
            Nodes =
            [
                Eq("region", new StringValue { Value = "EMEA" }),
                Eq("region", new StringValue { Value = "APAC" }),
                Eq("region", new StringValue { Value = "AMER" }),
                Eq("segment", new StringValue { Value = "Enterprise" })
            ]
        };

        var result = Assert.IsType<ComparisonNode>(
            GridFilterPipeline.ProcessSetValues(model, null, Spec(), MakeSchema(), limits));

        Assert.Equal("segment", result.Field);

        // ...and what survives IS still charged: the same model on a different target column, where
        // nothing is excluded, blows the same budget.
        Assert.Throws<ResourceLimitExceededException>(
            () => GridFilterPipeline.ProcessSetValues(model, null, Spec(colId: "segment"), MakeSchema(), limits));
    }
}
