using Craticula.Core.Ir;

namespace Craticula.Core.Tests.Ir;

/// <summary>
/// Hand-built boundary tests for <see cref="GroupSpecValidator"/>: a <see langword="null"/>
/// <see cref="GroupSpec"/> is valid (the request is not grouped at all), a non-empty
/// <see cref="GroupSpec.GroupCols"/> is required, field-length/blank checks per entry, no null
/// <see cref="GroupSpec.GroupKeys"/> elements, and the collect-all-then-throw pattern mirroring
/// <see cref="SortItemValidator"/>'s own tests.
/// </summary>
public class GroupSpecValidatorTests
{
    private static GroupSpec Spec(IReadOnlyList<string> groupCols, IReadOnlyList<TypedValue>? groupKeys = null) =>
        new() { GroupCols = groupCols, GroupKeys = groupKeys ?? [] };

    [Fact]
    public void Null_Group_Is_Valid() => GroupSpecValidator.Validate(null);

    [Fact]
    public void NonEmpty_GroupCols_With_Empty_GroupKeys_Does_Not_Throw() =>
        GroupSpecValidator.Validate(Spec(["region", "segment"]));

    [Fact]
    public void GroupKeys_Shorter_Than_GroupCols_Does_Not_Throw() =>
        GroupSpecValidator.Validate(Spec(["region", "segment"], [new StringValue { Value = "EMEA" }]));

    [Fact]
    public void Empty_GroupCols_Throws_GroupSpecValidationException()
    {
        var ex = Assert.Throws<GroupSpecValidationException>(() => GroupSpecValidator.Validate(Spec([])));

        Assert.Single(ex.Errors);
        Assert.Contains("groupCols must not be empty", ex.Errors[0]);
    }

    [Fact]
    public void Blank_GroupCols_Entry_Throws_GroupSpecValidationException()
    {
        var ex = Assert.Throws<GroupSpecValidationException>(() => GroupSpecValidator.Validate(Spec(["region", ""])));

        Assert.Single(ex.Errors);
        Assert.Contains("must not be blank", ex.Errors[0]);
    }

    [Fact]
    public void Overlong_GroupCols_Entry_Throws_GroupSpecValidationException()
    {
        var overlong = new string('a', 257);

        var ex = Assert.Throws<GroupSpecValidationException>(() => GroupSpecValidator.Validate(Spec([overlong])));

        Assert.Single(ex.Errors);
        Assert.Contains("256-character limit", ex.Errors[0]);
    }

    [Fact]
    public void GroupCols_Entry_At_Exactly_256_Characters_Does_Not_Throw()
    {
        var boundary = new string('a', 256);

        GroupSpecValidator.Validate(Spec([boundary]));
    }

    [Fact]
    public void Null_GroupKeys_Element_Throws_GroupSpecValidationException()
    {
        var ex = Assert.Throws<GroupSpecValidationException>(
            () => GroupSpecValidator.Validate(Spec(["region"], [null!])));

        Assert.Single(ex.Errors);
        Assert.Contains("groupKeys[0] must not be null", ex.Errors[0]);
    }

    [Fact]
    public void Collects_Every_Violation_Not_Just_The_First()
    {
        var overlong = new string('a', 300);

        var ex = Assert.Throws<GroupSpecValidationException>(
            () => GroupSpecValidator.Validate(Spec(["", overlong], [null!])));

        Assert.Equal(3, ex.Errors.Count);
    }
}
