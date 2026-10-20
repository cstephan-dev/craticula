using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

/// <summary>
/// Hand-built boundary tests for <see cref="SemanticsResolver"/> (SEM-1/SEM-2/SEM-3/SEM-6/GRP-2):
/// every <see cref="ComparisonOperator"/>'s resolved case-sensitivity mechanism, the ungrouped
/// ordering mechanism always collapsing to
/// <see cref="SemanticsResolver.CaseSensitivityMechanism.Collation"/> while a grouping request folds
/// no case at all,
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

        Assert.Equal(SemanticsResolver.CaseSensitivityMode.Insensitive, decision.Mode);
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

        Assert.Equal(SemanticsResolver.CaseSensitivityMode.Insensitive, decision.Mode);
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

        Assert.Equal(SemanticsResolver.CaseSensitivityMode.Insensitive, decision.Mode);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.None, decision.Mechanism);
    }

    [Fact]
    public void Sensitive_Field_Always_Resolves_To_Sensitive_None_Regardless_Of_Operator()
    {
        var decision = SemanticsResolver.ResolveCaseSensitivity(SensitiveField, ComparisonOperator.Eq);

        // Sensitive, NOT NotApplicable: the field IS text, and a compiler must be able to tell "text
        // the host asked to compare case-sensitively" from "nothing to collate" — the two need
        // opposite renderings on an engine whose default collation folds case.
        Assert.Equal(SemanticsResolver.CaseSensitivityMode.Sensitive, decision.Mode);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.None, decision.Mechanism);
    }

    [Fact]
    public void Absent_Text_Options_Default_To_Case_Insensitive()
    {
        var field = new FieldConfig { Type = FieldType.String };

        var decision = SemanticsResolver.ResolveCaseSensitivity(field, ComparisonOperator.Eq);

        Assert.Equal(SemanticsResolver.CaseSensitivityMode.Insensitive, decision.Mode);
    }

    [Fact]
    public void Explicit_Insensitive_Text_Option_Behaves_Identically_To_Absent_Text_Options()
    {
        var decision = SemanticsResolver.ResolveCaseSensitivity(ExplicitlyInsensitiveField, ComparisonOperator.Eq);

        Assert.Equal(SemanticsResolver.CaseSensitivityMode.Insensitive, decision.Mode);
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

        Assert.Equal(SemanticsResolver.CaseSensitivityMode.NotApplicable, decision.Mode);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.None, decision.Mechanism);
    }

    /// <summary>
    /// The distinction the <see cref="SemanticsResolver.CaseSensitivityMode"/> enum exists for, made
    /// a test rather than a comment: a case-sensitive <c>string</c> field and a non-text field are
    /// both "not case-insensitive", and a <see cref="bool"/> seam gave a compiler no way to tell them
    /// apart. They must be rendered differently — an explicit case-sensitive collation for the first,
    /// nothing at all for the second — so the resolver must not report them identically.
    /// </summary>
    [Fact]
    public void A_Sensitive_String_Field_And_A_NonText_Field_Do_Not_Resolve_To_The_Same_Mode()
    {
        var sensitive = SemanticsResolver.ResolveCaseSensitivity(SensitiveField, ComparisonOperator.Eq);
        var nonText = SemanticsResolver.ResolveCaseSensitivity(new FieldConfig { Type = FieldType.Int64 }, ComparisonOperator.Eq);

        Assert.NotEqual(sensitive.Mode, nonText.Mode);

        // ...and the ungrouped ordering context resolves the same way, because SEM-1's "same mechanism
        // in every context on a field" would otherwise be reachable only for the insensitive direction.
        Assert.NotEqual(
            SemanticsResolver.ResolveOrderingAndGroupKeyCaseSensitivity(SensitiveField, SemanticsResolver.GroupingMode.Ungrouped).Mode,
            SemanticsResolver.ResolveOrderingAndGroupKeyCaseSensitivity(new FieldConfig { Type = FieldType.Int64 }, SemanticsResolver.GroupingMode.Ungrouped).Mode);
    }

    // -- SEM-1/GRP-2: ResolveOrderingAndGroupKeyCaseSensitivity ---------------------------------

    [Fact]
    public void Ungrouped_Ordering_Mechanism_Is_Always_Collation_When_Insensitive()
    {
        var decision = SemanticsResolver.ResolveOrderingAndGroupKeyCaseSensitivity(
            InsensitiveField, SemanticsResolver.GroupingMode.Ungrouped);

        Assert.Equal(SemanticsResolver.CaseSensitivityMode.Insensitive, decision.Mode);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.Collation, decision.Mechanism);
    }

    [Fact]
    public void Ungrouped_Ordering_Mechanism_Is_None_When_Sensitive()
    {
        var decision = SemanticsResolver.ResolveOrderingAndGroupKeyCaseSensitivity(
            SensitiveField, SemanticsResolver.GroupingMode.Ungrouped);

        Assert.Equal(SemanticsResolver.CaseSensitivityMode.Sensitive, decision.Mode);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.None, decision.Mechanism);
    }

    [Fact]
    public void Ungrouped_Ordering_Mechanism_Is_None_For_A_NonString_Field()
    {
        var idField = new FieldConfig { Type = FieldType.Int64 };

        var decision = SemanticsResolver.ResolveOrderingAndGroupKeyCaseSensitivity(
            idField, SemanticsResolver.GroupingMode.Ungrouped);

        Assert.Equal(SemanticsResolver.CaseSensitivityMode.NotApplicable, decision.Mode);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.None, decision.Mechanism);
    }

    /// <summary>
    /// GRP-2: inside the row-group protocol NO structural key folds case, whatever the field says —
    /// the point being that a case-INSENSITIVE string, the one shape that would otherwise collate, is
    /// answered the same way as an int. Bucket identity, the drill-down match and the ordering have to
    /// agree, and the only rendering all three can share is "no mechanism at all": collating the group
    /// key is not merely undesirable, it makes the selected column stop matching the grouped
    /// expression.
    /// </summary>
    [Fact]
    public void Grouped_Structural_Keys_Never_Fold_Case_Whatever_The_Field_Declares()
    {
        FieldConfig[] fields =
        [
            InsensitiveField,
            ExplicitlyInsensitiveField,
            SensitiveField,
            new FieldConfig { Type = FieldType.Int64 }
        ];

        foreach (var field in fields)
        {
            var decision = SemanticsResolver.ResolveOrderingAndGroupKeyCaseSensitivity(
                field, SemanticsResolver.GroupingMode.Grouped);

            Assert.Equal(SemanticsResolver.CaseSensitivityMode.NotApplicable, decision.Mode);
            Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.None, decision.Mechanism);
        }
    }

    /// <summary>
    /// The axis the method's parameter exists for, made a test: the SAME field answers differently in
    /// the two modes. #1634 shipped this decision keyed on "sort key versus filter operator", which is
    /// the wrong axis — <c>spec/fixtures/sort-paging/001</c> collates an ungrouped ORDER BY on exactly
    /// this kind of field while <c>group-aggregation/002</c> does not, and no per-construct rule can
    /// produce both.
    /// </summary>
    [Fact]
    public void The_Same_Field_Resolves_Differently_Grouped_And_Ungrouped()
    {
        Assert.NotEqual(
            SemanticsResolver.ResolveOrderingAndGroupKeyCaseSensitivity(InsensitiveField, SemanticsResolver.GroupingMode.Ungrouped),
            SemanticsResolver.ResolveOrderingAndGroupKeyCaseSensitivity(InsensitiveField, SemanticsResolver.GroupingMode.Grouped));
    }

    /// <summary>
    /// GRP-2 is narrow on purpose: it governs the protocol's OWN keys, never the user's filter. A
    /// pre-aggregation filter selects rows before bucketing (AR-5) and has no say in bucket identity,
    /// so it keeps SEM-1 — and the resolver has no grouping-aware overload for it at all, which is
    /// what makes that unbypassable.
    /// </summary>
    [Fact]
    public void A_User_Filter_Predicate_Keeps_SEM1_Regardless_Of_Grouping()
    {
        var decision = SemanticsResolver.ResolveCaseSensitivity(InsensitiveField, ComparisonOperator.Eq);

        Assert.Equal(SemanticsResolver.CaseSensitivityMode.Insensitive, decision.Mode);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.Collation, decision.Mechanism);
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

    // -- SEM-6 (Bug #2565): the effective sort lists a field once and ends at the stable key -------

    /// <summary>The stable key first: it is unique, so no key after it can break a tie. Dropped, nothing appended (SQL Server error 169 otherwise).</summary>
    [Fact]
    public void Keys_After_The_StableKey_Are_Dropped_And_Nothing_Is_Appended()
    {
        IReadOnlyList<SortItem> sort = [Item("id", SortDirection.Desc, NullsPosition.First), Item("age")];

        var result = SemanticsResolver.AppendStableKeyTiebreaker(sort, "id", out var appended);

        var only = Assert.Single(result);
        Assert.Equal("id", only.Field);
        Assert.Equal(SortDirection.Desc, only.Direction);
        Assert.Equal(NullsPosition.First, only.NullsPosition);
        Assert.False(appended, "the stable key is the user's own key here, not a minted tiebreaker");
    }

    [Fact]
    public void The_StableKey_In_The_Middle_Keeps_The_Keys_Before_It_And_Drops_The_Ones_After()
    {
        IReadOnlyList<SortItem> sort = [Item("age"), Item("id", SortDirection.Desc, NullsPosition.First), Item("lastName")];

        var result = SemanticsResolver.AppendStableKeyTiebreaker(sort, "id", out var appended);

        Assert.Equal(["age", "id"], result.Select(item => item.Field));
        Assert.Equal(SortDirection.Desc, result[1].Direction);
        Assert.False(appended);
    }

    [Fact]
    public void A_Repeated_Field_Keeps_Its_First_Occurrence_And_The_Tiebreaker_Is_Still_Appended()
    {
        IReadOnlyList<SortItem> sort = [Item("age", SortDirection.Desc, NullsPosition.First), Item("age")];

        var result = SemanticsResolver.AppendStableKeyTiebreaker(sort, "id", out var appended);

        Assert.Equal(["age", "id"], result.Select(item => item.Field));
        Assert.Equal(SortDirection.Desc, result[0].Direction);
        Assert.Equal(NullsPosition.First, result[0].NullsPosition);
        Assert.True(appended);
    }

    [Fact]
    public void The_StableKey_Named_Twice_Keeps_The_First_And_Appends_Nothing()
    {
        IReadOnlyList<SortItem> sort = [Item("id"), Item("id", SortDirection.Desc, NullsPosition.First)];

        var result = SemanticsResolver.AppendStableKeyTiebreaker(sort, "id", out var appended);

        var only = Assert.Single(result);
        Assert.Equal(SortDirection.Asc, only.Direction);
        Assert.False(appended);
    }

    [Fact]
    public void A_Repeat_Is_Dropped_Wherever_It_Sits_Among_Other_Keys()
    {
        IReadOnlyList<SortItem> sort = [Item("age"), Item("lastName"), Item("age", SortDirection.Desc, NullsPosition.First), Item("salary"), Item("lastName")];

        var result = SemanticsResolver.AppendStableKeyTiebreaker(sort, "id");

        Assert.Equal(["age", "lastName", "salary", "id"], result.Select(item => item.Field));
        Assert.Equal(SortDirection.Asc, result[0].Direction);
    }

    /// <summary>The reductions look at the field only: a repeat that differs in direction or null placement is still a repeat.</summary>
    [Theory]
    [InlineData(SortDirection.Asc, NullsPosition.Last)]
    [InlineData(SortDirection.Asc, NullsPosition.First)]
    [InlineData(SortDirection.Desc, NullsPosition.Last)]
    [InlineData(SortDirection.Desc, NullsPosition.First)]
    public void A_Repeat_Is_A_Repeat_Whatever_Its_Direction_Or_Null_Placement(SortDirection direction, NullsPosition nulls)
    {
        IReadOnlyList<SortItem> sort = [Item("age"), Item("age", direction, nulls)];

        var result = SemanticsResolver.AppendStableKeyTiebreaker(sort, "id");

        Assert.Equal(["age", "id"], result.Select(item => item.Field));
    }

    /// <summary>A sort with no repeat and no key after the stable key is not touched, by reference: the common case allocates nothing.</summary>
    [Fact]
    public void A_Sort_That_Needs_No_Reduction_Is_Returned_By_Reference_When_It_Reaches_The_StableKey()
    {
        IReadOnlyList<SortItem> sort = [Item("age"), Item("lastName"), Item("id")];

        Assert.Same(sort, SemanticsResolver.AppendStableKeyTiebreaker(sort, "id"));
    }

    // -- SEM-6 (Bug #2565): the sort stops at the stable key only when the user's key on it orders exactly -----

    private static GridSchema SchemaWithStableKey(string stableKey, FieldConfig stableKeyField) => new()
    {
        Source = "t",
        StableKey = stableKey,
        Fields = new Dictionary<string, FieldConfig>
        {
            [stableKey] = stableKeyField,
            ["age"] = new FieldConfig { Type = FieldType.Int64 },
            ["lastName"] = new FieldConfig { Type = FieldType.String }
        }
    };

    /// <summary>A numeric stable key orders exactly, so the sort stops at it, as in the string overload.</summary>
    [Fact]
    public void A_Numeric_StableKey_Stops_The_Sort_In_The_Schema_Overload()
    {
        var schema = SchemaWithStableKey("id", new FieldConfig { Type = FieldType.Int64 });

        var result = SemanticsResolver.AppendStableKeyTiebreaker([Item("id"), Item("age")], schema, groupColumn: null, out var appended);

        Assert.Equal(["id"], result.Select(item => item.Field));
        Assert.False(appended);
    }

    /// <summary>A case-sensitive text stable key orders exactly too.</summary>
    [Fact]
    public void A_CaseSensitive_Text_StableKey_Stops_The_Sort()
    {
        var schema = SchemaWithStableKey("code", SensitiveField);

        var result = SemanticsResolver.AppendStableKeyTiebreaker([Item("code"), Item("age")], schema, groupColumn: null, out var appended);

        Assert.Equal(["code"], result.Select(item => item.Field));
        Assert.False(appended);
    }

    /// <summary>
    /// A case-insensitive text stable key can tie: <c>'aB3'</c> and <c>'Ab3'</c> compare equal under the user's collated key, so the sort is not
    /// total there. Nothing after it is dropped and the exact tiebreaker is still appended, as before this rule existed (Bug #2565 review).
    /// </summary>
    [Fact]
    public void A_CaseInsensitive_Text_StableKey_Keeps_The_Keys_After_It_And_Still_Gets_The_Exact_Tiebreaker()
    {
        var schema = SchemaWithStableKey("code", InsensitiveField);

        var result = SemanticsResolver.AppendStableKeyTiebreaker([Item("code"), Item("age")], schema, groupColumn: null, out var appended);

        Assert.Equal(["code", "age", "code"], result.Select(item => item.Field));
        Assert.True(appended, "the trailing key is the minted, exact tiebreaker");
    }

    [Fact]
    public void A_CaseInsensitive_Text_StableKey_Already_Last_Is_Left_As_It_Always_Was()
    {
        var schema = SchemaWithStableKey("code", InsensitiveField);
        IReadOnlyList<SortItem> sort = [Item("age"), Item("code")];

        var result = SemanticsResolver.AppendStableKeyTiebreaker(sort, schema, groupColumn: null, out var appended);

        Assert.Same(sort, result);
        Assert.False(appended);
    }

    /// <summary>Repeats are dropped whatever the stable key is: a later occurrence under the same collation can never break a tie the first left.</summary>
    [Fact]
    public void A_Repeated_Field_Is_Dropped_Even_When_The_StableKey_Is_Case_Insensitive_Text()
    {
        var schema = SchemaWithStableKey("code", InsensitiveField);

        var result = SemanticsResolver.AppendStableKeyTiebreaker([Item("age"), Item("age", SortDirection.Desc, NullsPosition.First)], schema, null, out _);

        Assert.Equal(["age", "code"], result.Select(item => item.Field));
    }

    /// <summary>A grouped level's group column is exact by construction (GRP-2, GRP-4), whatever the stable key looks like.</summary>
    [Fact]
    public void A_Group_Column_Stops_The_Sort_Whatever_The_StableKey_Is()
    {
        var schema = SchemaWithStableKey("code", InsensitiveField);

        var result = SemanticsResolver.AppendStableKeyTiebreaker([Item("lastName"), Item("age")], schema, groupColumn: "lastName", out var appended);

        Assert.Equal(["lastName"], result.Select(item => item.Field));
        Assert.False(appended);
    }

    [Fact]
    public void StableKeyOrdersExactly_Follows_The_Field_Type_And_Case_Setting()
    {
        Assert.True(SemanticsResolver.StableKeyOrdersExactly(SchemaWithStableKey("id", new FieldConfig { Type = FieldType.Int64 })));
        Assert.True(SemanticsResolver.StableKeyOrdersExactly(SchemaWithStableKey("code", SensitiveField)));
        Assert.False(SemanticsResolver.StableKeyOrdersExactly(SchemaWithStableKey("code", InsensitiveField)));
        Assert.False(SemanticsResolver.StableKeyOrdersExactly(SchemaWithStableKey("code", ExplicitlyInsensitiveField)));
    }

    /// <summary>The group column plays the stable key's role on a grouped level (GRP-4): a key after it is dropped the same way.</summary>
    [Fact]
    public void The_Same_Reductions_Apply_To_A_Group_Column_In_The_StableKeys_Role()
    {
        IReadOnlyList<SortItem> sort = [Item("country"), Item("amount", SortDirection.Desc, NullsPosition.First)];

        var result = SemanticsResolver.AppendStableKeyTiebreaker(sort, "country");

        Assert.Equal(["country"], result.Select(item => item.Field));
    }

    /// <summary>
    /// SEM-6: the tiebreaker this resolver MINTED is not recoverable from the returned list — it is
    /// the same <see cref="SortItem"/> a user key on that field would be — so the one place that
    /// decides it reports it. A caller re-deriving it by comparing the last field to the stable key
    /// would be duplicating the rule, and the flag is load-bearing: it selects
    /// <see cref="SemanticsResolver.SortKeyProvenance.AppendedTiebreaker"/> below.
    /// </summary>
    [Fact]
    public void The_Overload_Reports_Whether_The_Trailing_Key_Was_Minted_Here()
    {
        SemanticsResolver.AppendStableKeyTiebreaker(null, "id", out var appendedToNull);
        Assert.True(appendedToNull);

        SemanticsResolver.AppendStableKeyTiebreaker([Item("lastName")], "id", out var appendedToSort);
        Assert.True(appendedToSort);

        SemanticsResolver.AppendStableKeyTiebreaker([Item("lastName"), Item("id")], "id", out var alreadyEndsInIt);
        Assert.False(alreadyEndsInIt);
    }

    /// <summary>
    /// SEM-6/GRP-2: the APPENDED tiebreaker never folds case, even on the case-insensitive string the
    /// same resolver collates for a user's own ungrouped key. The point is determinism, not tidiness:
    /// the fixture suite's <c>craticula_ci</c> is <c>deterministic = false</c>, so a collated
    /// tiebreaker leaves <c>'aB3'</c> and <c>'Ab3'</c> tied, the sort non-total, and offset paging
    /// free to return a row twice or drop it from both pages — the exact failure SEM-6 exists to
    /// prevent. A <c>stableKey</c> is unique under its OWN storage collation; spelling nothing is what
    /// sorts it under that one.
    /// </summary>
    [Fact]
    public void The_Appended_Tiebreaker_Never_Folds_Case_Where_A_User_Key_On_The_Same_Field_Would()
    {
        var userKey = SemanticsResolver.ResolveOrderingAndGroupKeyCaseSensitivity(
            InsensitiveField, SemanticsResolver.GroupingMode.Ungrouped, SemanticsResolver.SortKeyProvenance.Request);

        var tiebreaker = SemanticsResolver.ResolveOrderingAndGroupKeyCaseSensitivity(
            InsensitiveField, SemanticsResolver.GroupingMode.Ungrouped, SemanticsResolver.SortKeyProvenance.AppendedTiebreaker);

        Assert.Equal(SemanticsResolver.CaseSensitivityMode.Insensitive, userKey.Mode);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.Collation, userKey.Mechanism);

        Assert.Equal(SemanticsResolver.CaseSensitivityMode.NotApplicable, tiebreaker.Mode);
        Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.None, tiebreaker.Mechanism);
    }

    /// <summary>...and a case-SENSITIVE field's tiebreaker is <c>NotApplicable</c> too, not
    /// <c>Sensitive</c>: the appended key is an identity key, so no case decision of any kind is being
    /// made about it. (On a grouped level GRP-2 already answered the same way, so the two rules
    /// compose rather than compete.)</summary>
    [Fact]
    public void The_Appended_Tiebreaker_Is_NotApplicable_In_Every_Mode_And_For_Every_Field()
    {
        FieldConfig[] fields = [InsensitiveField, ExplicitlyInsensitiveField, SensitiveField, new() { Type = FieldType.Int64 }];
        SemanticsResolver.GroupingMode[] modes = [SemanticsResolver.GroupingMode.Ungrouped, SemanticsResolver.GroupingMode.Grouped];

        foreach (var field in fields)
        {
            foreach (var mode in modes)
            {
                var decision = SemanticsResolver.ResolveOrderingAndGroupKeyCaseSensitivity(
                    field, mode, SemanticsResolver.SortKeyProvenance.AppendedTiebreaker);

                Assert.Equal(SemanticsResolver.CaseSensitivityMode.NotApplicable, decision.Mode);
                Assert.Equal(SemanticsResolver.CaseSensitivityMechanism.None, decision.Mechanism);
            }
        }
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
