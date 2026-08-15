using Craticula.Core.Ir;

namespace Craticula.Core.Tests.Ir;

/// <summary>
/// <see cref="SetValuesSpecValidator"/>'s structural invariants — <c>ir.schema.json</c>'s
/// <c>$defs/setValuesSpec</c> bounds that System.Text.Json cannot enforce on its own. Mirrors
/// <see cref="GroupSpecValidatorTests"/>' shape.
/// </summary>
public class SetValuesSpecValidatorTests
{
    private static SetValuesSpec Spec(string colId = "region", string? searchPrefix = null, int? limit = null) =>
        new() { ColId = colId, SearchPrefix = searchPrefix, Limit = limit };

    [Fact]
    public void Null_Spec_Is_Valid_Because_The_Request_Is_Simply_Not_A_Distinct_Values_Query()
    {
        SetValuesSpecValidator.Validate(null);
    }

    [Fact]
    public void The_Minimal_Spec_Is_A_ColId_Alone()
    {
        SetValuesSpecValidator.Validate(Spec());
    }

    [Fact]
    public void Blank_ColId_Is_Rejected()
    {
        var ex = Assert.Throws<SetValuesSpecValidationException>(() => SetValuesSpecValidator.Validate(Spec(colId: "")));
        Assert.Contains("colId must not be blank", ex.Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Overlong_ColId_Is_Rejected_At_FieldNames_Own_256_Character_Bound()
    {
        var ex = Assert.Throws<SetValuesSpecValidationException>(() => SetValuesSpecValidator.Validate(Spec(colId: new string('c', 257))));
        Assert.Contains("256-character limit", ex.Errors[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty search box is expressed by OMITTING the property, never by sending <c>""</c>: an
    /// empty prefix would compile to a predicate matching every row — a clause with no effect and a
    /// real cost — and the envelope already has an unambiguous way to say "no prefix".
    /// </summary>
    [Fact]
    public void Empty_SearchPrefix_Is_Rejected_Rather_Than_Read_As_No_Prefix()
    {
        var ex = Assert.Throws<SetValuesSpecValidationException>(() => SetValuesSpecValidator.Validate(Spec(searchPrefix: "")));
        Assert.Contains("searchPrefix must not be blank", ex.Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Overlong_SearchPrefix_Is_Rejected_At_StringValues_Own_8192_Character_Bound()
    {
        var ex = Assert.Throws<SetValuesSpecValidationException>(
            () => SetValuesSpecValidator.Validate(Spec(searchPrefix: new string('e', 8193))));
        Assert.Contains("8192-character limit", ex.Errors[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositive_Requested_Limit_Is_Rejected_Because_It_Can_Never_Return_A_Value(int limit)
    {
        var ex = Assert.Throws<SetValuesSpecValidationException>(() => SetValuesSpecValidator.Validate(Spec(limit: limit)));
        Assert.Contains("must be a positive integer", ex.Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_Limit_Far_Above_The_Hosts_Maximum_Is_Structurally_Fine_Because_The_Cap_Clamps_Rather_Than_Rejects()
    {
        // group-aggregation/019 sends 50000 against a 10000 ceiling and expects an ANSWER, not a 400 —
        // the clamp lives in ResourceLimitsValidator.ResolveSetValuesLimit, not here.
        SetValuesSpecValidator.Validate(Spec(limit: 50_000));
    }

    [Fact]
    public void Every_Violation_Is_Reported_At_Once_Not_Just_The_First()
    {
        var ex = Assert.Throws<SetValuesSpecValidationException>(
            () => SetValuesSpecValidator.Validate(Spec(colId: "", searchPrefix: "", limit: 0)));

        Assert.Equal(3, ex.Errors.Count);
    }
}
