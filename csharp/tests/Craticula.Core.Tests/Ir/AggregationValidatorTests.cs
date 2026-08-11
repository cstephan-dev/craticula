using Craticula.Core.Ir;

namespace Craticula.Core.Tests.Ir;

/// <summary>
/// Hand-built boundary tests for <see cref="AggregationValidator"/>: a <see langword="null"/>
/// aggregations array is valid (no aggregations at all), an empty non-null array is rejected
/// (<c>ir.schema.json</c>'s <c>aggregations.minItems:1</c>, mirroring <see cref="SortItemValidator"/>'s
/// own canonical-form rule for <c>sort</c>), field-length/blank checks per entry, the
/// <c>alias</c> identifier-pattern/length bound, and the collect-all-then-throw pattern.
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
}
