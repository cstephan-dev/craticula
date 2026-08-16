using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Search.Tests;

/// <summary>
/// The per-operator rendering paths, with the emphasis deliberately on the ones <b>no fixture
/// reaches</b> — a case-sensitive string ordering comparison, a case-insensitive one on a field that
/// declares no normalizer sub-field, the substring family on a <c>searchMode: analyzed</c> field
/// beyond <c>contains</c>, SC-4's base-field remapping, and the escape sets on values that actually
/// contain metacharacters. The 160-fixture conformance suite covers the rest far better than a
/// hand-written test could.
/// </summary>
public class SearchQueryOperatorTests
{
    private static string Compile(GridSchema schema, FilterNode? filter) =>
        SearchFixtureCompiler.CanonicalJson(SearchQueryBuilder.Build(new SearchQueryRequest
        {
            Schema = schema,
            Filter = filter,
            Page = SearchTestSchema.FirstPage
        }).Query);

    // ---- SEM-1's "one real gap": ordering on a text field ------------------------------------------

    [Fact]
    public void A_Case_Insensitive_String_Range_Targets_The_Declared_Normalizer_Sub_Field()
    {
        var schema = SearchTestSchema.Of(
            ("id", SearchTestSchema.Id),
            ("lastName", SearchTestSchema.Field(
                FieldType.String, "lastName", keyword: "lastName.keyword", normalized: "lastName.normalized")));

        Assert.Equal(
            """{"range":{"lastName.normalized":{"gt":"Smith"}}}""",
            Compile(schema, SearchTestSchema.Compare(
                "lastName", ComparisonOperator.Gt, new StringValue { Value = "Smith" })));
    }

    /// <summary>
    /// The value is bound <b>as typed</b>, and this is the disagreement
    /// <c>spec/fixtures/dialect-parity/015</c> lost. Folding it in the compiler
    /// (<c>"Smith"</c> -&gt; <c>"smith"</c>) is wrong three times over: a term-level query already has
    /// the normalizer applied to its value at search time, so it is redundant; the compiler cannot see
    /// what that normalizer actually does (SEM-1 describes an <b>ICU-folding</b> one, which folds
    /// diacritics and width as well as case, so a pre-folded <c>"ÉCOLE"</c> would miss every document);
    /// and "lowercase" is not one operation across four language ports (.NET's invariant lowering,
    /// Java's locale-sensitive default, Python's full case folding and JavaScript's all disagree on
    /// some input), which is a G4 divergence for a transformation the engine performs correctly
    /// itself. Every other arm of that fixture binds <c>"Smith"</c> untouched and says so.
    /// </summary>
    [Fact]
    public void A_Case_Insensitive_String_Range_Never_Folds_The_Bound_Value_Itself()
    {
        var schema = SearchTestSchema.Of(
            ("id", SearchTestSchema.Id),
            ("lastName", SearchTestSchema.Field(FieldType.String, "lastName", normalized: "lastName.normalized")));

        var compiled = Compile(schema, SearchTestSchema.Compare(
            "lastName", ComparisonOperator.Gte, new StringValue { Value = "ÉCOLE" }));

        var bound = System.Text.Json.Nodes.JsonNode
            .Parse(compiled)!["range"]!["lastName.normalized"]!["gte"]!.GetValue<string>();

        Assert.Equal("ÉCOLE", bound);
    }

    [Fact]
    public void A_Case_Sensitive_String_Range_Targets_The_Exact_Match_Sub_Field_And_Needs_No_Normalizer()
    {
        var schema = SearchTestSchema.Of(
            ("id", SearchTestSchema.Id),
            ("employeeCode", SearchTestSchema.Field(
                FieldType.String, "employeeCode", keyword: "employeeCode.keyword",
                caseSensitivity: CaseSensitivity.Sensitive)));

        Assert.Equal(
            """{"range":{"employeeCode.keyword":{"lt":"AB-100"}}}""",
            Compile(schema, SearchTestSchema.Compare(
                "employeeCode", ComparisonOperator.Lt, new StringValue { Value = "AB-100" })));
    }

    /// <summary>
    /// SEM-1 states the normalizer sub-field as a MUST on the schema, and its absence is refused
    /// rather than approximated: <c>.keyword</c> would order <c>"Zebra"</c> before <c>"apple"</c> on a
    /// field the schema declared case-insensitive — wrong rows in the wrong order, on a request both
    /// engines accept, invisible to the artifact tier. SQL-7(b)'s and MDB-9's register.
    /// </summary>
    [Fact]
    public void A_Case_Insensitive_String_Range_On_A_Field_With_No_Normalizer_Sub_Field_Is_Refused()
    {
        var schema = SearchTestSchema.Of(
            ("id", SearchTestSchema.Id),
            ("lastName", SearchTestSchema.Field(FieldType.String, "lastName", keyword: "lastName.keyword")));

        var exception = Assert.Throws<SearchCompilationException>(() => Compile(
            schema, SearchTestSchema.Compare("lastName", ComparisonOperator.Gt, new StringValue { Value = "S" })));

        Assert.Contains("normalizedSubField", exception.Message);
        Assert.Contains("range query", exception.Message);
    }

    // ---- SEM-7's substring family -----------------------------------------------------------------

    [Theory]
    [InlineData(ComparisonOperator.Contains, "*A\\*B*")]
    [InlineData(ComparisonOperator.StartsWith, "A\\*B*")]
    [InlineData(ComparisonOperator.EndsWith, "*A\\*B")]
    public void The_Wildcard_Anchors_Are_Composed_Around_The_Escaped_Value(ComparisonOperator op, string expected)
    {
        var schema = SearchTestSchema.Of(
            ("id", SearchTestSchema.Id),
            ("code", SearchTestSchema.Field(
                FieldType.String, "code", keyword: "code.keyword", caseSensitivity: CaseSensitivity.Sensitive)));

        var pattern = System.Text.Json.Nodes.JsonNode
            .Parse(Compile(schema, Compare(op, "code")))!["wildcard"]!["code.keyword"]!["value"]!.GetValue<string>();

        Assert.Equal(expected, pattern);
    }

    /// <summary>
    /// SEC-4's ES alphabet is <c>*</c> and <c>?</c>, plus the escape character itself — and nothing
    /// else. <c>%</c>, <c>_</c> and <c>[</c> are SQL's (SQL-5) and the regex metacharacters are
    /// MongoDB's (MDB-11); escaping them here would put a literal backslash in the pattern and search
    /// for text the user never typed. <c>dialect-parity/020</c>/<c>022</c> pin the three that pass
    /// through; this pins the three that do not.
    /// </summary>
    [Fact]
    public void The_Wildcard_Escape_Alphabet_Is_Exactly_Backslash_Star_And_Question_Mark()
    {
        var schema = SearchTestSchema.Of(
            ("id", SearchTestSchema.Id),
            ("code", SearchTestSchema.Field(
                FieldType.String, "code", keyword: "code.keyword", caseSensitivity: CaseSensitivity.Sensitive)));

        var compiled = Compile(schema, new ComparisonNode
        {
            Field = "code",
            Op = ComparisonOperator.Contains,
            Value = new StringValue { Value = @"50% a_b [c] (d) \e *f ?g" }
        });

        Assert.Equal(
            """{"wildcard":{"code.keyword":{"value":"*50% a_b [c] (d) \\\\e \\*f \\?g*"}}}""",
            compiled);
    }

    /// <summary>
    /// MDB-11's trap, checked on this backend's own escape: a value outside the BMP must survive the
    /// escape AND the JSON writer intact. The corruption MDB-11 records happens at the writer — a
    /// backslash inserted between the two halves of a surrogate pair leaves two lone surrogates, which
    /// System.Text.Json then emits as U+FFFD — so the assertion is on the round-tripped artifact text,
    /// never on the escape function's return value, which looks fine either way.
    /// </summary>
    [Fact]
    public void A_Non_Ascii_Substring_Value_Survives_The_Escape_And_The_Json_Writer()
    {
        var schema = SearchTestSchema.Of(
            ("id", SearchTestSchema.Id),
            ("code", SearchTestSchema.Field(
                FieldType.String, "code", keyword: "code.keyword", caseSensitivity: CaseSensitivity.Sensitive)));

        var compiled = Compile(schema, new ComparisonNode
        {
            Field = "code",
            Op = ComparisonOperator.Contains,
            Value = new StringValue { Value = "\U0001F600" }
        });

        // Read the pattern back OUT of the serialized artifact, which is the only place the damage
        // shows: a lone surrogate is written as the � escape, which parses back to U+FFFD.
        var pattern = System.Text.Json.Nodes.JsonNode
            .Parse(compiled)!["wildcard"]!["code.keyword"]!["value"]!.GetValue<string>();

        Assert.Equal("*\U0001F600*", pattern);
        Assert.DoesNotContain('�', pattern);
    }

    /// <summary>
    /// SEM-7's <c>searchMode: analyzed</c> opt-in, and its <b>scope</b>: it changes
    /// <c>contains</c>/<c>notContains</c>, which is what SEM-7 names, and leaves
    /// <c>startsWith</c>/<c>endsWith</c> on the wildcard form. An anchored affix has no tokenized
    /// meaning — <c>match</c> would match a document whose first token merely equals the prefix — and
    /// SEM-7 describes the opt-in as an escape hatch for "relevance-style full-text matching", which
    /// an affix never is. Only <c>dialect-parity/013</c>'s <c>contains</c> is pinned by a fixture.
    /// </summary>
    [Fact]
    public void SearchMode_Analyzed_Changes_Contains_And_NotContains_And_Leaves_The_Affixes_Alone()
    {
        var schema = SearchTestSchema.Of(
            ("id", SearchTestSchema.Id),
            ("jobTitle", SearchTestSchema.Field(FieldType.String, "jobTitle", searchMode: SearchMode.Analyzed)));

        Assert.Equal(
            """{"match":{"jobTitle":{"operator":"and","query":"engineer"}}}""",
            Compile(schema, Compare(ComparisonOperator.Contains, "jobTitle", "engineer")));

        // ...and notContains negates that same clause, with SEM-2's null-inclusive arm around it.
        Assert.Equal(
            """{"bool":{"minimum_should_match":1,"should":[{"bool":{"must_not":{"match":{"jobTitle":{"operator":"and","query":"engineer"}}}}},{"bool":{"must_not":{"exists":{"field":"jobTitle"}}}}]}}""",
            Compile(schema, Compare(ComparisonOperator.NotContains, "jobTitle", "engineer")));

        // The field declares no keywordSubField, so the wildcard falls back to the base field —
        // SEM-7's last Must-NOT calls that a documented PERF-4 trade-off, not a compiler error.
        Assert.Equal(
            """{"wildcard":{"jobTitle":{"case_insensitive":true,"value":"Senior*"}}}""",
            Compile(schema, Compare(ComparisonOperator.StartsWith, "jobTitle", "Senior")));
    }

    /// <summary>
    /// OSE-6: <c>searchMode: analyzed</c> on a case-<b>sensitive</b> field is <b>refused</b>, and this
    /// is the one path in this class that no artifact could ever pin — <c>dialect-parity/013</c> is
    /// the suite's only <c>analyzed</c> field and it is case-insensitive.
    ///
    /// <para>A <c>match</c> has no <c>case_insensitive</c> parameter and no case control at all: it is
    /// resolved by the field's own analyzer, and every stock text analyzer in both engines lowercases.
    /// So compiling the combination would fold case on a field the schema declared sensitive — SEM-1's
    /// second Must-NOT, and the same failure <c>OrderingTarget</c> refuses in the other direction.
    /// There is no third rendering to fall back to, so the two declarations contradict each other and
    /// the schema is what has to change. The affixes are unaffected: they keep the wildcard form,
    /// which honours the case correctly.</para>
    /// </summary>
    [Fact]
    public void SearchMode_Analyzed_On_A_Case_Sensitive_Field_Is_Refused()
    {
        var schema = SearchTestSchema.Of(
            ("id", SearchTestSchema.Id),
            ("jobTitle", SearchTestSchema.Field(
                FieldType.String, "jobTitle",
                caseSensitivity: CaseSensitivity.Sensitive, searchMode: SearchMode.Analyzed)));

        foreach (var op in new[] { ComparisonOperator.Contains, ComparisonOperator.NotContains })
        {
            var exception = Assert.Throws<SearchCompilationException>(
                () => Compile(schema, Compare(op, "jobTitle", "Engineer")));

            Assert.Contains("analyzed", exception.Message);
            Assert.Contains("sensitive", exception.Message);
        }

        // startsWith/endsWith never take the `match` form, so they compile — case-sensitively, which
        // is exactly what the schema asked for.
        Assert.Equal(
            """{"wildcard":{"jobTitle":{"value":"Senior*"}}}""",
            Compile(schema, Compare(ComparisonOperator.StartsWith, "jobTitle", "Senior")));
    }

    // ---- SC-4 -------------------------------------------------------------------------------------

    /// <summary>
    /// SC-4's base-field remapping, which <b>no in-scope fixture exercises</b> (see
    /// <c>SearchConformanceFixtureTests.Records_That_No_In_Scope_Fixture_Maps_A_Search_Field_Away_From_Its_ColId</c>).
    /// The three names are independent: the <c>exists</c> clause names the base field even when the
    /// comparison names a sub-field, because a <c>.keyword</c> sub-field's own presence is an indexing
    /// detail (<c>ignore_above</c> can drop a long value from it while the base field still holds
    /// one), and "this document has no value for this column" is a question only the base field
    /// answers.
    /// </summary>
    [Fact]
    public void The_Base_Field_The_Keyword_Sub_Field_And_The_Normalizer_Are_Three_Independent_Names()
    {
        var schema = SearchTestSchema.Of(
            ("id", SearchTestSchema.Id),
            ("customerName", SearchTestSchema.Field(
                FieldType.String, "customer_name", keyword: "customer_name.exact", normalized: "customer_name.folded")));

        Assert.Equal(
            """{"term":{"customer_name.exact":{"case_insensitive":true,"value":"Acme"}}}""",
            Compile(schema, SearchTestSchema.Compare(
                "customerName", ComparisonOperator.Eq, new StringValue { Value = "Acme" })));

        Assert.Equal(
            """{"range":{"customer_name.folded":{"gt":"Acme"}}}""",
            Compile(schema, SearchTestSchema.Compare(
                "customerName", ComparisonOperator.Gt, new StringValue { Value = "Acme" })));

        Assert.Equal(
            """{"bool":{"minimum_should_match":1,"should":[{"bool":{"must_not":{"exists":{"field":"customer_name"}}}},{"term":{"customer_name.exact":{"value":""}}}]}}""",
            Compile(schema, SearchTestSchema.Compare("customerName", ComparisonOperator.Blank)));
    }

    [Fact]
    public void A_Field_The_Schema_Does_Not_Declare_Is_A_Caller_Precondition_Failure()
    {
        var schema = SearchTestSchema.Of(("id", SearchTestSchema.Id));

        var exception = Assert.Throws<SearchCompilationException>(() => Compile(
            schema, SearchTestSchema.Compare("salary", ComparisonOperator.Gt, new Int64Value { Value = 1 })));

        Assert.Contains("SC-1/SC-2", exception.Message);
    }

    // ---- AR-7 -------------------------------------------------------------------------------------

    /// <summary>
    /// AR-7: a <c>decimal</c> keeps its exact decimal text. This DSL's request body has no
    /// arbitrary-precision numeric literal the way Extended JSON's <c>$numberDecimal</c> does, so the
    /// only correct answer is to write the IR's own token through unchanged — never to round-trip it
    /// through a binary double, which is what makes <c>decimal</c> and <c>double</c> different types
    /// in the first place. The value here has no <c>double</c> representation at all.
    /// </summary>
    [Fact]
    public void A_Decimal_Bound_Keeps_Its_Exact_Text_And_Is_Never_Round_Tripped_Through_A_Double()
    {
        var schema = SearchTestSchema.Of(
            ("id", SearchTestSchema.Id),
            ("amount", SearchTestSchema.Field(FieldType.Decimal, "amount")));

        Assert.Equal(
            """{"range":{"amount":{"gt":0.1000000000000000055511151231257827}}}""",
            Compile(schema, SearchTestSchema.Compare(
                "amount", ComparisonOperator.Gt,
                new DecimalValue { Value = "0.1000000000000000055511151231257827" })));
    }

    /// <summary>
    /// SEM-4: a <c>datetimeValue</c> is normalised to UTC so the same instant written <c>+02:00</c>
    /// and written <c>Z</c> produces one artifact, and its <c>zone</c> is provenance that never
    /// reaches the query — SEM-4's second Must-NOT names ES's <c>time_zone</c> parameter explicitly.
    /// </summary>
    [Fact]
    public void A_Datetime_Bound_Is_Normalised_To_Utc_And_Carries_No_Time_Zone_Parameter()
    {
        var schema = SearchTestSchema.Of(
            ("id", SearchTestSchema.Id),
            ("createdAt", SearchTestSchema.Field(FieldType.Datetime, "createdAt")));

        var compiled = Compile(schema, SearchTestSchema.Compare(
            "createdAt", ComparisonOperator.Gte,
            new DatetimeValue { Value = "2026-01-01T02:00:00+02:00", Zone = "Europe/Paris" }));

        Assert.Equal(
            """{"range":{"createdAt":{"format":"strict_date_optional_time","gte":"2026-01-01T00:00:00Z"}}}""",
            compiled);
        Assert.DoesNotContain("time_zone", compiled);
        Assert.DoesNotContain("Paris", compiled);
    }

    private static ComparisonNode Compare(ComparisonOperator op, string field) =>
        new() { Field = field, Op = op, Value = new StringValue { Value = "A*B" } };

    private static ComparisonNode Compare(ComparisonOperator op, string field, string value) =>
        new() { Field = field, Op = op, Value = new StringValue { Value = value } };
}
