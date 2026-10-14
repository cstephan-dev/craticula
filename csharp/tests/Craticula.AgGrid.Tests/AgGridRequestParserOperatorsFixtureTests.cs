using Craticula.Core.Ir;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Fixture-driven coverage against the real <c>spec/fixtures/operators/</c> suite (56 cases, AR-6's
/// closed 17-operator set across the AR-7 field-type matrix) — loads each fixture's real
/// <c>agGridRequest.json</c>, runs it through <see cref="AgGridRequestParser.Parse"/>, and asserts
/// the resulting IR shape (field, operator, value) rather than taking the plan's summary as ground
/// truth. Every one of the 56 cases is exercised by name below.
/// </summary>
public class AgGridRequestParserOperatorsFixtureTests
{
    private static string LoadRequest(string fixtureDir) =>
        File.ReadAllText(RepoPaths.Fixture("operators", fixtureDir, "agGridRequest.json"));

    private static ComparisonNode ParseSingleNativeComparison(string fixtureDir) =>
        Assert.IsType<ComparisonNode>(AgGridRequestParser.Parse(LoadRequest(fixtureDir)).NativeFilter);

    /// <summary>Parses with the fixture's own <c>schema.json</c> supplied — needed by the handful of
    /// cases whose IR shape depends on the field's declared type (the boolean Set Filter convention
    /// below; date wire values elsewhere).</summary>
    private static ComparisonNode ParseSingleNativeComparisonWithSchema(string fixtureDir)
    {
        var options = new AgGridParseOptions
        {
            Schema = Craticula.Core.Schema.GridSchema.Parse(
                File.ReadAllText(RepoPaths.Fixture("operators", fixtureDir, "schema.json")))
        };

        return Assert.IsType<ComparisonNode>(AgGridRequestParser.Parse(LoadRequest(fixtureDir), options).NativeFilter);
    }

    // ---- Single bare condition -> single ComparisonNode, value from 'filter'/'dateFrom' -----------

    [Theory]
    [InlineData("003-eq-int64", "age", ComparisonOperator.Eq, "30")]
    [InlineData("009-gt-int64", "age", ComparisonOperator.Gt, "18")]
    [InlineData("010-gt-double", "price", ComparisonOperator.Gt, "9.99")]
    [InlineData("013-gte-int64", "age", ComparisonOperator.Gte, "18")]
    [InlineData("014-gte-double", "price", ComparisonOperator.Gte, "9.99")]
    [InlineData("017-lt-int64", "age", ComparisonOperator.Lt, "65")]
    [InlineData("018-lt-double", "price", ComparisonOperator.Lt, "49.99")]
    [InlineData("021-lte-int64", "age", ComparisonOperator.Lte, "65")]
    [InlineData("022-lte-double", "price", ComparisonOperator.Lte, "49.99")]
    [InlineData("046-eq-decimal", "unitCost", ComparisonOperator.Eq, "19.99")]
    [InlineData("047-gt-decimal", "unitCost", ComparisonOperator.Gt, "49.95")]
    public void Number_Condition_Fixtures_Produce_Expected_ComparisonNode(
        string fixtureDir, string field, ComparisonOperator op, string expectedValue)
    {
        var comparison = ParseSingleNativeComparison(fixtureDir);

        Assert.Equal(field, comparison.Field);
        Assert.Equal(op, comparison.Op);
        Assert.Equal(expectedValue, Assert.IsType<StringValue>(comparison.Value).Value);
    }

    [Theory]
    [InlineData("002-neq-string", "status", ComparisonOperator.Neq, "inactive")]
    [InlineData("025-contains-string", "productName", ComparisonOperator.Contains, "art")]
    [InlineData("026-notcontains-string", "productName", ComparisonOperator.NotContains, "gizmo")]
    [InlineData("027-startswith-string", "productName", ComparisonOperator.StartsWith, "Smart")]
    [InlineData("028-endswith-string", "productName", ComparisonOperator.EndsWith, "Watch")]
    public void Text_Condition_Fixtures_Produce_Expected_ComparisonNode(
        string fixtureDir, string field, ComparisonOperator op, string expectedValue)
    {
        var comparison = ParseSingleNativeComparison(fixtureDir);

        Assert.Equal(field, comparison.Field);
        Assert.Equal(op, comparison.Op);
        Assert.Equal(expectedValue, Assert.IsType<StringValue>(comparison.Value).Value);
    }

    [Theory]
    [InlineData("008-neq-date", "birthDate", ComparisonOperator.Neq, "1990-05-12")]
    [InlineData("011-gt-date", "birthDate", ComparisonOperator.Gt, "1990-01-01")]
    [InlineData("012-gt-datetime", "createdAt", ComparisonOperator.Gt, "2026-01-01 00:00:00")]
    [InlineData("015-gte-date", "birthDate", ComparisonOperator.Gte, "1990-01-01")]
    [InlineData("016-gte-datetime", "createdAt", ComparisonOperator.Gte, "2026-01-01 00:00:00")]
    [InlineData("019-lt-date", "birthDate", ComparisonOperator.Lt, "2000-01-01")]
    [InlineData("020-lt-datetime", "createdAt", ComparisonOperator.Lt, "2026-02-01 00:00:00")]
    [InlineData("023-lte-date", "birthDate", ComparisonOperator.Lte, "2000-01-01")]
    [InlineData("024-lte-datetime", "createdAt", ComparisonOperator.Lte, "2026-02-01 00:00:00")]
    public void Date_Condition_Fixtures_Use_DateFrom_As_StringValue(
        string fixtureDir, string field, ComparisonOperator op, string expectedValue)
    {
        var comparison = ParseSingleNativeComparison(fixtureDir);

        Assert.Equal(field, comparison.Field);
        Assert.Equal(op, comparison.Op);
        Assert.Equal(expectedValue, Assert.IsType<StringValue>(comparison.Value).Value);
    }

    // ---- Unary blank/notBlank -------------------------------------------------------------------

    [Theory]
    [InlineData("029-blank-string", "status", ComparisonOperator.Blank)]
    [InlineData("030-notblank-string", "status", ComparisonOperator.NotBlank)]
    [InlineData("031-blank-int64", "age", ComparisonOperator.Blank)]
    [InlineData("032-notblank-int64", "age", ComparisonOperator.NotBlank)]
    [InlineData("033-blank-date", "birthDate", ComparisonOperator.Blank)]
    [InlineData("034-notblank-date", "birthDate", ComparisonOperator.NotBlank)]
    public void Unary_Condition_Fixtures_Have_No_Value(string fixtureDir, string field, ComparisonOperator op)
    {
        var comparison = ParseSingleNativeComparison(fixtureDir);

        Assert.Equal(field, comparison.Field);
        Assert.Equal(op, comparison.Op);
        Assert.Null(comparison.Value);
    }

    // ---- Legacy condition1/condition2 combined form (AG-1) -----------------------------------------

    [Fact]
    public void Fixture001_Legacy_Or_Form_Combines_Two_Equals_On_Status()
    {
        var orNode = Assert.IsType<OrNode>(AgGridRequestParser.Parse(LoadRequest("001-eq-string")).NativeFilter);

        Assert.Equal(2, orNode.Nodes.Count);
        foreach (var node in orNode.Nodes)
        {
            var comparison = Assert.IsType<ComparisonNode>(node);
            Assert.Equal("status", comparison.Field);
            Assert.Equal(ComparisonOperator.Eq, comparison.Op);
        }

        Assert.Equal("active", Assert.IsType<StringValue>(Assert.IsType<ComparisonNode>(orNode.Nodes[0]).Value).Value);
        Assert.Equal("approved", Assert.IsType<StringValue>(Assert.IsType<ComparisonNode>(orNode.Nodes[1]).Value).Value);
    }

    [Fact]
    public void Fixture004_Legacy_And_Form_Combines_Two_NotEquals_On_Age()
    {
        var andNode = Assert.IsType<AndNode>(AgGridRequestParser.Parse(LoadRequest("004-neq-int64")).NativeFilter);

        Assert.Equal(2, andNode.Nodes.Count);
        Assert.All(andNode.Nodes, node => Assert.Equal(ComparisonOperator.Neq, Assert.IsType<ComparisonNode>(node).Op));
    }

    [Fact]
    public void Fixture007_Legacy_Or_Form_Combines_Two_Equals_On_BirthDate()
    {
        var orNode = Assert.IsType<OrNode>(AgGridRequestParser.Parse(LoadRequest("007-eq-date")).NativeFilter);

        Assert.Equal(2, orNode.Nodes.Count);
        Assert.Equal("1990-05-12", Assert.IsType<StringValue>(Assert.IsType<ComparisonNode>(orNode.Nodes[0]).Value).Value);
        Assert.Equal("1991-07-04", Assert.IsType<StringValue>(Assert.IsType<ComparisonNode>(orNode.Nodes[1]).Value).Value);
    }

    // ---- Set filter (native 'in', boolean modeling) --------------------------------------------

    [Fact]
    public void Fixture035_In_String_Via_Native_Set_Filter()
    {
        var comparison = ParseSingleNativeComparison("035-in-string");

        Assert.Equal("status", comparison.Field);
        Assert.Equal(ComparisonOperator.In, comparison.Op);
        Assert.Equal(["active", "pending"], comparison.Values!.Select(v => Assert.IsType<StringValue>(v).Value));
    }

    [Fact]
    public void Fixture037_In_Int64_Values_Are_StringValue_Not_Coerced_Here()
    {
        var comparison = ParseSingleNativeComparison("037-in-int64");

        Assert.Equal("age", comparison.Field);
        Assert.Equal(ComparisonOperator.In, comparison.Op);
        Assert.Equal(["30", "40"], comparison.Values!.Select(v => Assert.IsType<StringValue>(v).Value));
    }

    [Theory]
    [InlineData("044-true-boolean", ComparisonOperator.True)]
    [InlineData("045-false-boolean", ComparisonOperator.False)]
    public void Boolean_Set_Selection_Becomes_The_Unary_Operator_When_The_Schema_Declares_The_Field_Boolean(
        string fixtureDir, ComparisonOperator expected)
    {
        // spec/fixtures/operators/README.md: "the adapter compiles a boolean column's single-value
        // Set Filter selection to the dedicated unary true/false operators (see 044/045), never to
        // generic eq/neq". #1635 deferred this as "a later semantic stage"; #1638 established there
        // is no later stage that could do it — a compiler sees only `in ["true"]` and cannot tell it
        // apart from a genuine one-element set — and both fixtures' Postgres artifacts pin
        // `"is_active" = $1` with a boolean param, not `= ANY($1)` with a one-element array.
        var comparison = ParseSingleNativeComparisonWithSchema(fixtureDir);

        Assert.Equal("isActive", comparison.Field);
        Assert.Equal(expected, comparison.Op);
        Assert.Null(comparison.Values);
        Assert.Null(comparison.Value);
    }

    [Theory]
    [InlineData("044-true-boolean", "true")]
    [InlineData("045-false-boolean", "false")]
    public void The_Same_Selection_Stays_An_In_When_No_Schema_Says_The_Field_Is_Boolean(string fixtureDir, string wireValue)
    {
        // The rewrite is schema-gated on purpose: `values: ["true"]` against a STRING column is an
        // ordinary set membership on the literal text "true", so without a declared type the parser
        // must not guess. Absent a schema its behavior is unchanged from #1635.
        var comparison = ParseSingleNativeComparison(fixtureDir);

        Assert.Equal(ComparisonOperator.In, comparison.Op);
        Assert.Equal(wireValue, Assert.IsType<StringValue>(comparison.Values![0]).Value);
    }

    // ---- customFilters-only fixtures (no native filterModel path exists for these) -----------------

    [Theory]
    [InlineData("005-eq-boolean")]
    [InlineData("006-neq-boolean")]
    [InlineData("036-notin-string")]
    [InlineData("038-notin-int64")]
    [InlineData("039-inrange-int64-inclusive-inclusive")]
    [InlineData("040-inrange-date-inclusive-exclusive")]
    [InlineData("041-inrange-datetime-exclusive-inclusive")]
    [InlineData("042-inrange-int64-exclusive-exclusive")]
    [InlineData("043-inrange-date-inclusive-inclusive")]
    public void CustomFilters_Only_Fixtures_Have_Null_NativeFilter_And_A_Parsed_CustomFilters_Comparison(string fixtureDir)
    {
        var parsed = AgGridRequestParser.Parse(LoadRequest(fixtureDir));

        Assert.Null(parsed.NativeFilter);
        Assert.IsType<ComparisonNode>(parsed.CustomFilters);
    }

    [Fact]
    public void Fixture005_CustomFilters_Eq_Boolean()
    {
        var comparison = Assert.IsType<ComparisonNode>(AgGridRequestParser.Parse(LoadRequest("005-eq-boolean")).CustomFilters);

        Assert.Equal("isActive", comparison.Field);
        Assert.Equal(ComparisonOperator.Eq, comparison.Op);
        Assert.True(Assert.IsType<BooleanValue>(comparison.Value).Value);
    }

    [Fact]
    public void Fixture039_CustomFilters_InRange_Carries_Explicit_Inclusivity_Flags()
    {
        var comparison = Assert.IsType<ComparisonNode>(
            AgGridRequestParser.Parse(LoadRequest("039-inrange-int64-inclusive-inclusive")).CustomFilters);

        Assert.Equal("age", comparison.Field);
        Assert.Equal(ComparisonOperator.InRange, comparison.Op);
        Assert.Equal(18, Assert.IsType<Int64Value>(comparison.From).Value);
        Assert.Equal(65, Assert.IsType<Int64Value>(comparison.To).Value);
        Assert.True(comparison.FromInclusive);
        Assert.True(comparison.ToInclusive);
    }

    [Fact]
    public void Fixture042_CustomFilters_InRange_Both_Exclusive_Distinguishes_From_Fixture039()
    {
        var comparison = Assert.IsType<ComparisonNode>(
            AgGridRequestParser.Parse(LoadRequest("042-inrange-int64-exclusive-exclusive")).CustomFilters);

        Assert.False(comparison.FromInclusive);
        Assert.False(comparison.ToInclusive);
    }

    // ---- Sanity sweep: every one of the 56 fixtures parses without throwing ------------------------

    public static readonly TheoryData<string> AllOperatorFixtures = new()
    {
        "001-eq-string", "002-neq-string", "003-eq-int64", "004-neq-int64", "005-eq-boolean",
        "006-neq-boolean", "007-eq-date", "008-neq-date", "009-gt-int64", "010-gt-double",
        "011-gt-date", "012-gt-datetime", "013-gte-int64", "014-gte-double", "015-gte-date",
        "016-gte-datetime", "017-lt-int64", "018-lt-double", "019-lt-date", "020-lt-datetime",
        "021-lte-int64", "022-lte-double", "023-lte-date", "024-lte-datetime", "025-contains-string",
        "026-notcontains-string", "027-startswith-string", "028-endswith-string", "029-blank-string",
        "030-notblank-string", "031-blank-int64", "032-notblank-int64", "033-blank-date",
        "034-notblank-date", "035-in-string", "036-notin-string", "037-in-int64", "038-notin-int64",
        "039-inrange-int64-inclusive-inclusive", "040-inrange-date-inclusive-exclusive",
        "041-inrange-datetime-exclusive-inclusive", "042-inrange-int64-exclusive-exclusive",
        "043-inrange-date-inclusive-inclusive", "044-true-boolean", "045-false-boolean",
        "046-eq-decimal", "047-gt-decimal", "048-in-decimal", "049-notin-decimal",
        "050-in-date", "051-notin-date", "052-in-datetime", "053-notin-datetime", "054-in-datetime-zoneless",
        "055-in-datetime-zoneless-zone", "056-in-date-zoneless"
    };

    [Theory]
    [MemberData(nameof(AllOperatorFixtures))]
    public void Every_Operator_Fixture_Parses_Without_Throwing(string fixtureDir)
    {
        AgGridRequestParser.Parse(LoadRequest(fixtureDir));
    }
}
