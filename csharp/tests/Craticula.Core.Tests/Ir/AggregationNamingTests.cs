using Craticula.Core.Ir;

namespace Craticula.Core.Tests.Ir;

/// <summary>
/// GRP-4's output name and AGG-2's reconstructed <c>ColumnVO.id</c>, tested where they now live
/// (#1885). Both were inline expressions repeated in three compilers, so every assertion about them
/// was previously an assertion about a compiled artifact; these pin the rules themselves, including
/// the one case where the two answers differ.
/// </summary>
public class AggregationNamingTests
{
    private static Aggregation Agg(string field, AggregationFunc func, string? alias = null) =>
        new() { Field = field, Func = func, Alias = alias };

    /// <summary>GRP-4's compiler default, in the exact spelling the fixtures pin — the function name
    /// lower-cased, which is what makes <c>amount_sum</c> rather than <c>amount_Sum</c> the name every
    /// port must choose.</summary>
    [Theory]
    [InlineData(AggregationFunc.Sum, "amount_sum")]
    [InlineData(AggregationFunc.Min, "amount_min")]
    [InlineData(AggregationFunc.Max, "amount_max")]
    [InlineData(AggregationFunc.Avg, "amount_avg")]
    [InlineData(AggregationFunc.Count, "amount_count")]
    public void Unaliased_OutputName_Is_Field_Underscore_Lowercased_Func(AggregationFunc func, string expected) =>
        Assert.Equal(expected, Agg("amount", func).OutputName);

    [Fact]
    public void An_Alias_Wins_Over_The_Default() =>
        Assert.Equal("totalRevenue", Agg("amount", AggregationFunc.Sum, "totalRevenue").OutputName);

    /// <summary>AGG-2's rule inverted: the adapter sets the alias from <c>ColumnVO.id</c> exactly when
    /// it differs from <c>ColumnVO.field</c>, so an absent alias means the grid's column id WAS the
    /// field.</summary>
    [Fact]
    public void Unaliased_ColumnId_Is_The_Bare_Field() =>
        Assert.Equal("amount", Agg("amount", AggregationFunc.Sum).ColumnId);

    [Fact]
    public void Aliased_ColumnId_Is_The_Alias() =>
        Assert.Equal("totalRevenue", Agg("amount", AggregationFunc.Sum, "totalRevenue").ColumnId);

    /// <summary>
    /// The distinction the two members exist to keep apart. An unaliased aggregation is published as
    /// <c>amount_sum</c> but is addressed by <c>sortModel</c> as <c>amount</c>; matching a sort key
    /// against the output name would reject every unaliased key the grid can actually send, and it
    /// would do so with a message about a column the projection "does not contain" — which is the
    /// failure a compiled artifact cannot show, because no fixture would ever have been written to
    /// pin an error that plausible.
    /// </summary>
    [Fact]
    public void Unaliased_OutputName_And_ColumnId_Differ()
    {
        var aggregation = Agg("amount", AggregationFunc.Sum);

        Assert.Equal("amount_sum", aggregation.OutputName);
        Assert.Equal("amount", aggregation.ColumnId);
    }

    /// <summary>And an alias collapses them, which is why the case above is the one worth pinning: an
    /// aliased aggregation hides the distinction entirely.</summary>
    [Fact]
    public void An_Alias_Collapses_The_Two()
    {
        var aggregation = Agg("amount", AggregationFunc.Avg, "amountAvg");

        Assert.Equal(aggregation.OutputName, aggregation.ColumnId);
    }

    /// <summary>
    /// Two aggregations over one field are the reason the default carries the function at all: they
    /// are the common AG Grid pattern AGG-2 exists for, and without the suffix they would compile to
    /// one output key — silently, since a <c>$group</c> stage is a map (MDB-10).
    /// </summary>
    [Fact]
    public void Two_Funcs_On_One_Field_Get_Distinct_Default_Names() =>
        Assert.NotEqual(
            Agg("amount", AggregationFunc.Sum).OutputName,
            Agg("amount", AggregationFunc.Avg).OutputName);

    /// <summary>
    /// The default composes an identifier-hostile name without complaint, and that is deliberate:
    /// <c>$defs/fieldName</c> carries no pattern, so a nested colId reaches here legally. Whether the
    /// result is usable is a BACKEND fact — <c>SearchQueryBuilder.RequireOutputName</c> refuses it
    /// because an Elasticsearch aggregation name is read through a <c>buckets_path</c> mini-language,
    /// while a quoted SQL alias and a <c>$group</c> key have no such grammar — so the shared rule
    /// composes and does not judge. This test is what says that is a decision rather than an
    /// oversight.
    /// </summary>
    [Fact]
    public void A_Nested_ColId_Composes_A_Default_Name_Without_Judging_It() =>
        Assert.Equal("customer.name_count", Agg("customer.name", AggregationFunc.Count).OutputName);
}
