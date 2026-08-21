using Craticula.Core.Ir;

namespace Craticula.Core.Tests.Ir;

/// <summary>
/// Hand-built boundary tests for <see cref="AggregationValidator"/>: a <see langword="null"/>
/// aggregations array is valid (no aggregations at all), an empty non-null array is rejected
/// (<c>ir.schema.json</c>'s <c>aggregations.minItems:1</c>, mirroring <see cref="SortItemValidator"/>'s
/// own canonical-form rule for <c>sort</c>), field-length/blank checks per entry, the
/// <c>alias</c> identifier-pattern/length bound, GRP-4 output-name uniqueness (#1886), and the
/// collect-all-then-throw pattern.
/// </summary>
public class AggregationValidatorTests
{
    private static Aggregation Agg(string field, AggregationFunc func = AggregationFunc.Sum, string? alias = null) =>
        new() { Field = field, Func = func, Alias = alias };

    [Fact]
    public void Null_Aggregations_Is_Valid() => AggregationValidator.Validate(null);

    [Fact]
    public void Empty_Aggregations_Array_Throws_AggregationValidationException()
    {
        var ex = Assert.Throws<AggregationValidationException>(() => AggregationValidator.Validate([]));

        Assert.Single(ex.Errors);
        Assert.Contains("must not be empty", ex.Errors[0]);
    }

    [Fact]
    public void Single_Valid_Aggregation_Does_Not_Throw() => AggregationValidator.Validate([Agg("amount")]);

    [Fact]
    public void Valid_Alias_Does_Not_Throw() => AggregationValidator.Validate([Agg("amount", alias: "totalRevenue")]);

    [Fact]
    public void Alias_Starting_With_Underscore_Does_Not_Throw() => AggregationValidator.Validate([Agg("amount", alias: "_total")]);

    [Theory]
    [InlineData("")]
    public void Blank_Field_Throws_AggregationValidationException(string field)
    {
        var ex = Assert.Throws<AggregationValidationException>(() => AggregationValidator.Validate([Agg(field)]));

        Assert.Single(ex.Errors);
        Assert.Contains("field must not be blank", ex.Errors[0]);
    }

    [Fact]
    public void Overlong_Field_Throws_AggregationValidationException()
    {
        var overlong = new string('a', 257);

        var ex = Assert.Throws<AggregationValidationException>(() => AggregationValidator.Validate([Agg(overlong)]));

        Assert.Single(ex.Errors);
        Assert.Contains("256-character limit", ex.Errors[0]);
    }

    [Theory]
    [InlineData("1totalRevenue")] // starts with a digit
    [InlineData("total-revenue")] // hyphen not allowed
    [InlineData("total revenue")] // space not allowed
    [InlineData("")] // empty when present is still a violation (present-but-blank)
    public void Malformed_Alias_Throws_AggregationValidationException(string alias)
    {
        var ex = Assert.Throws<AggregationValidationException>(() => AggregationValidator.Validate([Agg("amount", alias: alias)]));

        Assert.Single(ex.Errors);
        Assert.Contains("alias", ex.Errors[0]);
    }

    [Fact]
    public void Alias_At_Exactly_64_Characters_Does_Not_Throw()
    {
        var boundary = "a" + new string('b', 63);

        AggregationValidator.Validate([Agg("amount", alias: boundary)]);
    }

    [Fact]
    public void Alias_At_65_Characters_Throws_AggregationValidationException()
    {
        var overlong = "a" + new string('b', 64);

        var ex = Assert.Throws<AggregationValidationException>(() => AggregationValidator.Validate([Agg("amount", alias: overlong)]));

        Assert.Single(ex.Errors);
    }

    [Fact]
    public void Null_Element_Throws_AggregationValidationException_Not_NullReferenceException()
    {
        var ex = Assert.Throws<AggregationValidationException>(() => AggregationValidator.Validate([Agg("amount"), null!]));

        Assert.Single(ex.Errors);
        Assert.Contains("must not be null", ex.Errors[0]);
    }

    [Fact]
    public void Collects_Every_Violation_Not_Just_The_First()
    {
        var overlong = new string('a', 300);

        var ex = Assert.Throws<AggregationValidationException>(
            () => AggregationValidator.Validate([Agg(""), Agg(overlong, alias: "bad alias")]));

        Assert.Equal(3, ex.Errors.Count);
    }

    // ---- GRP-4 output-name uniqueness (#1886) -----------------------------------------------------

    /// <summary>
    /// The shape <c>adversarial/023</c> pins: two aliases colliding over two different fields. It is
    /// refused here, upstream of all four backends, because two of the three compilers refused it and
    /// <c>Craticula.Sql</c> did not — it emitted the column twice and let the driver decide.
    /// </summary>
    [Fact]
    public void Two_Aggregations_Sharing_An_Alias_Throw_And_The_Message_Names_Both_Positions()
    {
        var ex = Assert.Throws<AggregationValidationException>(() => AggregationValidator.Validate(
            [Agg("amount", alias: "total"), Agg("quantity", alias: "total")]));

        Assert.Single(ex.Errors);
        Assert.Contains("aggregations[0, 1]", ex.Errors[0]);
        Assert.Contains("'total'", ex.Errors[0]);
    }

    /// <summary>The same collision reached through GRP-4's DEFAULT name rather than an alias — the
    /// half a check written only against <see cref="Aggregation.Alias"/> would miss.</summary>
    [Fact]
    public void Two_Unaliased_Aggregations_With_The_Same_Field_And_Func_Collide_On_The_Default_Name()
    {
        var ex = Assert.Throws<AggregationValidationException>(() => AggregationValidator.Validate(
            [Agg("amount"), Agg("amount")]));

        Assert.Single(ex.Errors);
        Assert.Contains("'amount_sum'", ex.Errors[0]);
    }

    /// <summary>An alias colliding with another aggregation's DEFAULT name: the two halves of GRP-4's
    /// rule meeting, which is why the check is on the resolved output name and not on either input.
    /// </summary>
    [Fact]
    public void An_Alias_Colliding_With_Another_Aggregations_Default_Name_Is_Rejected()
    {
        var ex = Assert.Throws<AggregationValidationException>(() => AggregationValidator.Validate(
            [Agg("amount"), Agg("quantity", alias: "amount_sum")]));

        Assert.Single(ex.Errors);
        Assert.Contains("'amount_sum'", ex.Errors[0]);
    }

    /// <summary>
    /// The counter-case, and the reason the rule is <see cref="Aggregation.OutputName"/> rather than
    /// <see cref="Aggregation.ColumnId"/>: two functions over one unaliased field is a legal shape —
    /// <c>page-export/011</c> compiles it on Postgres (its two <c>valueCols</c> both carry
    /// <c>id == field == "totalAmount"</c>, so neither gets an alias) — and its two output names
    /// differ (<c>totalAmount_sum</c>, <c>totalAmount_avg</c>). <c>group-aggregation/012</c> is a
    /// DIFFERENT shape: its two <c>valueCols</c> carry distinct ids (<c>amountSum</c>/<c>amountAvg</c>),
    /// so AGG-2 assigns each an explicit alias — it never reaches the unaliased default this test is
    /// about. A uniqueness check written against the column id would reject the real shape.
    /// </summary>
    [Fact]
    public void Two_Different_Funcs_Over_One_Unaliased_Field_Do_Not_Collide()
    {
        AggregationValidator.Validate([Agg("amount"), Agg("amount", AggregationFunc.Avg)]);
    }

    /// <summary>Ordinal comparison: the name lands in query text as an identifier and no backend here
    /// folds identifier case, so <c>Total</c> and <c>total</c> are two columns, not one.</summary>
    [Fact]
    public void Output_Names_Differing_Only_In_Case_Do_Not_Collide()
    {
        AggregationValidator.Validate([Agg("amount", alias: "Total"), Agg("quantity", alias: "total")]);
    }

    /// <summary>Three-way collision reported once, naming all three positions — the collect-all
    /// pattern applied to the group rather than to the pairs, so a request with N duplicates yields
    /// one error rather than N-1.</summary>
    [Fact]
    public void A_Three_Way_Collision_Is_One_Error_Naming_All_Three_Positions()
    {
        var ex = Assert.Throws<AggregationValidationException>(() => AggregationValidator.Validate(
            [Agg("amount", alias: "total"), Agg("quantity", alias: "total"), Agg("price", alias: "total")]));

        Assert.Single(ex.Errors);
        Assert.Contains("aggregations[0, 1, 2]", ex.Errors[0]);
    }

    /// <summary>A duplicate is collected beside the other violations, not instead of them: two
    /// distinct collisions plus a malformed alias in one call.</summary>
    [Fact]
    public void Duplicates_Are_Collected_Alongside_The_Other_Violations()
    {
        var ex = Assert.Throws<AggregationValidationException>(() => AggregationValidator.Validate(
        [
            Agg("amount", alias: "total"),
            Agg("quantity", alias: "total"),
            Agg("price"),
            Agg("price"),
            Agg("age", alias: "bad alias")
        ]));

        Assert.Equal(3, ex.Errors.Count);
        Assert.Contains(ex.Errors, error => error.Contains("'total'"));
        Assert.Contains(ex.Errors, error => error.Contains("'price_sum'"));
        Assert.Contains(ex.Errors, error => error.Contains("alias 'bad alias'"));
    }
}
