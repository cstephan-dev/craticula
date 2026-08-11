using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

/// <summary>
/// Hand-built boundary tests for <see cref="SemanticsResolver"/> (SEM-1/SEM-2/SEM-3/SEM-6): every
/// <see cref="ComparisonOperator"/>'s resolved case-sensitivity mechanism, the sort/group-key
/// mechanism always collapsing to <see cref="SemanticsResolver.CaseSensitivityMechanism.Collation"/>,
/// SEM-2's null-inclusive operator set and text-vs-other-type blank condition, SEM-3's pinned
/// adapter default, and SEM-6's stableKey-tiebreaker append/no-op/validation behavior. Real-fixture-
/// driven coverage of SEM-3/SEM-6 against <c>spec/fixtures/sort-paging/</c> lives in
/// <see cref="SemanticsResolverSortPagingFixtureTests"/>.
/// </summary>
public class SemanticsResolverTests
{
    private static readonly FieldConfig InsensitiveField = new() { Type = FieldType.String };

    private static readonly FieldConfig SensitiveField = new()
    {
        Type = FieldType.String,
        Text = new TextOptions { CaseSensitivity = CaseSensitivity.Sensitive }
    };

    private static readonly FieldConfig ExplicitlyInsensitiveField = new()
    {
        Type = FieldType.String,
        Text = new TextOptions { CaseSensitivity = CaseSensitivity.Insensitive }
    };

    // -- SEM-1: ResolveCaseSensitivity --------------------------------------------------------

    [Theory]
    [InlineData(ComparisonOperator.Eq)]
    [InlineData(ComparisonOperator.Neq)]
    [InlineData(ComparisonOperator.Gt)]
    [InlineData(ComparisonOperator.Gte)]
    [InlineData(ComparisonOperator.Lt)]
    [InlineData(ComparisonOperator.Lte)]
    [InlineData(ComparisonOperator.In)]
    [InlineData(ComparisonOperator.NotIn)]
    public void Exact_Match_And_Ordering_Family_Resolves_To_Collation_When_Insensitive(ComparisonOperator op)
    {
        var decision = SemanticsResolver.ResolveCaseSensitivity(InsensitiveField, op);

        Assert.True(decision.IsCaseInsensitive);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.Collation, decision.Mechanism);
    }

    [Theory]
    [InlineData(ComparisonOperator.Contains)]
    [InlineData(ComparisonOperator.NotContains)]
    [InlineData(ComparisonOperator.StartsWith)]
    [InlineData(ComparisonOperator.EndsWith)]
    public void Substring_Family_Resolves_To_PatternMatch_When_Insensitive(ComparisonOperator op)
    {
        var decision = SemanticsResolver.ResolveCaseSensitivity(InsensitiveField, op);

        Assert.True(decision.IsCaseInsensitive);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.PatternMatch, decision.Mechanism);
    }

    [Theory]
    [InlineData(ComparisonOperator.Blank)]
    [InlineData(ComparisonOperator.NotBlank)]
    [InlineData(ComparisonOperator.True)]
    [InlineData(ComparisonOperator.False)]
    [InlineData(ComparisonOperator.InRange)]
    public void Operators_SEM1_Does_Not_Cover_Resolve_To_None_Even_When_Insensitive(ComparisonOperator op)
    {
        var decision = SemanticsResolver.ResolveCaseSensitivity(InsensitiveField, op);

        Assert.True(decision.IsCaseInsensitive);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.None, decision.Mechanism);
    }

    [Fact]
    public void Sensitive_Field_Always_Resolves_To_False_None_Regardless_Of_Operator()
    {
        var decision = SemanticsResolver.ResolveCaseSensitivity(SensitiveField, ComparisonOperator.Eq);

        Assert.False(decision.IsCaseInsensitive);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.None, decision.Mechanism);
    }

    [Fact]
    public void Absent_Text_Options_Default_To_Case_Insensitive()
    {
        var field = new FieldConfig { Type = FieldType.String };

        var decision = SemanticsResolver.ResolveCaseSensitivity(field, ComparisonOperator.Eq);

        Assert.True(decision.IsCaseInsensitive);
    }

    [Fact]
    public void Explicit_Insensitive_Text_Option_Behaves_Identically_To_Absent_Text_Options()
    {
        var decision = SemanticsResolver.ResolveCaseSensitivity(ExplicitlyInsensitiveField, ComparisonOperator.Eq);

        Assert.True(decision.IsCaseInsensitive);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.Collation, decision.Mechanism);
    }

    [Theory]
    [InlineData(FieldType.Int64)]
    [InlineData(FieldType.Decimal)]
    [InlineData(FieldType.Double)]
    [InlineData(FieldType.Boolean)]
    [InlineData(FieldType.Date)]
    [InlineData(FieldType.Datetime)]
    public void NonString_Field_Is_Never_Case_Insensitive_Even_With_No_Text_Options_At_All(FieldType fieldType)
    {
        // SEM-1 only ever applies to a stringValue comparison — a numeric/date/boolean field with no
        // Text block must not fall into "absent Text defaults to insensitive" the way a string field
        // does, or a compiler would be told to emit e.g. "age COLLATE craticula_ci = $1", which is
        // not just wrong but a hard error on a backend that rejects collations on non-text types.
        var field = new FieldConfig { Type = fieldType };

        var decision = SemanticsResolver.ResolveCaseSensitivity(field, ComparisonOperator.Eq);

        Assert.False(decision.IsCaseInsensitive);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.None, decision.Mechanism);
    }

    // -- SEM-1: ResolveSortOrGroupKeyCaseSensitivity ------------------------------------------

    [Fact]
    public void Sort_Or_GroupKey_Mechanism_Is_Always_Collation_When_Insensitive()
    {
        var decision = SemanticsResolver.ResolveSortOrGroupKeyCaseSensitivity(InsensitiveField);

        Assert.True(decision.IsCaseInsensitive);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.Collation, decision.Mechanism);
    }

    [Fact]
    public void Sort_Or_GroupKey_Mechanism_Is_None_When_Sensitive()
    {
        var decision = SemanticsResolver.ResolveSortOrGroupKeyCaseSensitivity(SensitiveField);

        Assert.False(decision.IsCaseInsensitive);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.None, decision.Mechanism);
    }

    [Fact]
    public void Sort_Or_GroupKey_Mechanism_Is_None_For_A_NonString_Field()
    {
        var idField = new FieldConfig { Type = FieldType.Int64 };

        var decision = SemanticsResolver.ResolveSortOrGroupKeyCaseSensitivity(idField);

        Assert.False(decision.IsCaseInsensitive);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.None, decision.Mechanism);
    }

    // -- SEM-2: IsNullInclusive ----------------------------------------------------------------

    [Theory]
    [InlineData(ComparisonOperator.Neq)]
    [InlineData(ComparisonOperator.NotContains)]
    [InlineData(ComparisonOperator.NotIn)]
    public void Negative_Operators_Are_Null_Inclusive(ComparisonOperator op) =>
        Assert.True(SemanticsResolver.IsNullInclusive(op));

    [Theory]
    [InlineData(ComparisonOperator.Eq)]
    [InlineData(ComparisonOperator.Contains)]
    [InlineData(ComparisonOperator.In)]
    [InlineData(ComparisonOperator.Gt)]
    [InlineData(ComparisonOperator.Blank)]
    [InlineData(ComparisonOperator.InRange)]
    public void Every_Other_Operator_Is_Not_Null_Inclusive(ComparisonOperator op) =>
        Assert.False(SemanticsResolver.IsNullInclusive(op));

    // -- SEM-2: ResolveBlankCondition ------------------------------------------------------------

    [Fact]
    public void String_Fields_Resolve_To_NullOrEmptyString() =>
        Assert.Equal(SemanticsResolver.BlankCondition.NullOrEmptyString, SemanticsResolver.ResolveBlankCondition(FieldType.String));

    [Theory]
    [InlineData(FieldType.Int64)]
    [InlineData(FieldType.Decimal)]
    [InlineData(FieldType.Double)]
    [InlineData(FieldType.Boolean)]
    [InlineData(FieldType.Date)]
    [InlineData(FieldType.Datetime)]
    public void NonString_Fields_Resolve_To_NullOnly(FieldType fieldType) =>
        Assert.Equal(SemanticsResolver.BlankCondition.NullOnly, SemanticsResolver.ResolveBlankCondition(fieldType));

    // -- SEM-3: DefaultNullsPosition -------------------------------------------------------------

    [Fact]
    public void Asc_Defaults_To_Nulls_Last() =>
        Assert.Equal(NullsPosition.Last, SemanticsResolver.DefaultNullsPosition(SortDirection.Asc));

    [Fact]
    public void Desc_Defaults_To_Nulls_First() =>
        Assert.Equal(NullsPosition.First, SemanticsResolver.DefaultNullsPosition(SortDirection.Desc));

    // -- SEM-6: AppendStableKeyTiebreaker ---------------------------------------------------------

    private static SortItem Item(string field, SortDirection direction = SortDirection.Asc, NullsPosition nullsPosition = NullsPosition.Last) =>
        new() { Field = field, Direction = direction, NullsPosition = nullsPosition };

    [Fact]
    public void Null_Sort_Appends_A_SingleElement_List_With_The_Tiebreaker()
    {
        var result = SemanticsResolver.AppendStableKeyTiebreaker(null, "id");

        var tiebreaker = Assert.Single(result);
        Assert.Equal("id", tiebreaker.Field);
        Assert.Equal(SortDirection.Asc, tiebreaker.Direction);
        Assert.Equal(NullsPosition.Last, tiebreaker.NullsPosition);
    }

    [Fact]
    public void Sort_Not_Ending_In_StableKey_Gets_The_Tiebreaker_Appended_Last()
    {
        IReadOnlyList<SortItem> sort = [Item("lastName"), Item("salary", SortDirection.Desc, NullsPosition.First)];

        var result = SemanticsResolver.AppendStableKeyTiebreaker(sort, "id");

        Assert.Equal(3, result.Count);
        Assert.Equal("lastName", result[0].Field);
        Assert.Equal("salary", result[1].Field);
        Assert.Equal("id", result[2].Field);
        Assert.Equal(SortDirection.Asc, result[2].Direction);
        Assert.Equal(NullsPosition.Last, result[2].NullsPosition);
    }

    [Fact]
    public void Sort_Already_Ending_In_StableKey_Is_Returned_Unchanged_By_Reference()
    {
        IReadOnlyList<SortItem> sort = [Item("lastName"), Item("id", SortDirection.Asc, NullsPosition.Last)];

        var result = SemanticsResolver.AppendStableKeyTiebreaker(sort, "id");

        Assert.Same(sort, result);
    }

    [Fact]
    public void StableKey_As_The_Only_Existing_Sort_Key_Is_Not_Duplicated()
    {
        IReadOnlyList<SortItem> sort = [Item("id", SortDirection.Desc, NullsPosition.First)];

        var result = SemanticsResolver.AppendStableKeyTiebreaker(sort, "id");

        Assert.Same(sort, result);
        Assert.Single(result);
    }

    [Fact]
    public void Empty_NonNull_Sort_Array_Throws_SortItemValidationException_Via_DefenseInDepth() =>
        Assert.Throws<SortItemValidationException>(() => SemanticsResolver.AppendStableKeyTiebreaker([], "id"));

    [Fact]
    public void Blank_Field_In_Sort_Array_Throws_SortItemValidationException_Via_DefenseInDepth() =>
        Assert.Throws<SortItemValidationException>(() => SemanticsResolver.AppendStableKeyTiebreaker([Item("")], "id"));

    [Fact]
    public void Blank_StableKey_Throws_SortItemValidationException_Rather_Than_Minting_An_Invalid_Tiebreaker() =>
        Assert.Throws<SortItemValidationException>(() => SemanticsResolver.AppendStableKeyTiebreaker(null, ""));

    [Fact]
    public void Overlong_StableKey_Throws_SortItemValidationException_Rather_Than_Minting_An_Invalid_Tiebreaker() =>
        Assert.Throws<SortItemValidationException>(
            () => SemanticsResolver.AppendStableKeyTiebreaker(null, new string('a', 257)));
}
