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

    /// <summary>
    /// GRP-1's arity rule, which lives here rather than in a compiler because
    /// <c>ir.schema.json</c> cannot express it (JSON Schema has no way to compare two arrays'
    /// lengths) and every backend would otherwise re-derive it. The failure it prevents is silent:
    /// a compiler consuming only as many keys as fit answers with a different bucket's rows.
    /// </summary>
    [Fact]
    public void More_GroupKeys_Than_GroupCols_Throws_GroupSpecValidationException()
    {
        var ex = Assert.Throws<GroupSpecValidationException>(() => GroupSpecValidator.Validate(
            Spec(["region"], [new StringValue { Value = "EMEA" }, new StringValue { Value = "Enterprise" }])));

        Assert.Single(ex.Errors);
        Assert.Contains("consumes 2 key(s) but groupCols declares only 1", ex.Errors[0]);
    }

    /// <summary>
    /// Equal lengths is the drill-down LEAF (GRP-1), the most easily off-by-one'd of the three
    /// dispatch outcomes — it must stay valid.
    /// </summary>
    [Fact]
    public void GroupKeys_Exactly_As_Many_As_GroupCols_Does_Not_Throw() =>
        GroupSpecValidator.Validate(Spec(
            ["region", "segment"],
            [new StringValue { Value = "EMEA" }, new StringValue { Value = "Enterprise" }]));

    /// <summary>
    /// The arity check stays quiet when <c>groupCols</c> is itself malformed: "5 keys against 0
    /// columns" is the same defect as "groupCols must not be empty", and reporting both would make
    /// the collect-all-then-throw list say it twice.
    /// </summary>
    [Fact]
    public void Surplus_Keys_Against_Empty_GroupCols_Reports_Only_The_Empty_GroupCols_Error()
    {
        var ex = Assert.Throws<GroupSpecValidationException>(() =>
            GroupSpecValidator.Validate(Spec([], [new StringValue { Value = "EMEA" }])));

        Assert.Single(ex.Errors);
        Assert.Contains("groupCols must not be empty", ex.Errors[0]);
    }

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

    /// <summary>
    /// The structural amplification cap (<see cref="GroupSpecValidator.MaxGroupCols"/>). It lives
    /// here, not in <c>ResourceLimits</c>, precisely so it holds on the paths that never run the SEC-3
    /// validator: a host may hand a <see cref="GroupSpec"/> straight to a compiler, and GRP-5 turns
    /// each consumed key into a leaf predicate, so an unbounded <c>groupCols</c> is an unbounded
    /// drill-down conjunction.
    /// </summary>
    [Fact]
    public void GroupCols_Beyond_The_Structural_Cap_Throws_GroupSpecValidationException()
    {
        var tooMany = Enumerable.Range(0, GroupSpecValidator.MaxGroupCols + 1).Select(i => $"col{i}").ToList();

        var ex = Assert.Throws<GroupSpecValidationException>(() => GroupSpecValidator.Validate(Spec(tooMany)));

        Assert.Single(ex.Errors);
        Assert.Contains("groupCols.maxItems", ex.Errors[0]);
    }

    [Fact]
    public void GroupCols_At_Exactly_The_Structural_Cap_Does_Not_Throw()
    {
        var atCap = Enumerable.Range(0, GroupSpecValidator.MaxGroupCols).Select(i => $"col{i}").ToList();

        GroupSpecValidator.Validate(Spec(atCap));
    }

    /// <summary>
    /// ...and <c>groupKeys</c> is bounded transitively rather than separately: GRP-1 already requires
    /// it to be no longer than <c>groupCols</c>, so capping one caps both.
    /// </summary>
    [Fact]
    public void GroupKeys_Is_Bounded_Through_GRP1_By_The_Same_Cap()
    {
        var atCap = Enumerable.Range(0, GroupSpecValidator.MaxGroupCols).Select(i => $"col{i}").ToList();
        var tooManyKeys = Enumerable
            .Range(0, GroupSpecValidator.MaxGroupCols + 1)
            .Select(i => (TypedValue)new StringValue { Value = $"k{i}" })
            .ToList();

        var ex = Assert.Throws<GroupSpecValidationException>(() => GroupSpecValidator.Validate(Spec(atCap, tooManyKeys)));

        Assert.Single(ex.Errors);
        Assert.Contains("GRP-1", ex.Errors[0]);
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
