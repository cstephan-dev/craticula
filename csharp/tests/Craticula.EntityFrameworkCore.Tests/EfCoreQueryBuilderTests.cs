using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.EntityFrameworkCore.Tests;

/// <summary>
/// Unit tests for the translation itself — the half <see cref="EfCoreExecutionEqualityTests"/> cannot
/// reach.
///
/// <para>That suite proves the two paths agree about ROWS, which is AR-4's requirement, but rows are
/// silent about three things: the shapes this adapter <b>refuses</b> (nothing is returned, so there is
/// nothing to compare); properties of the emitted SQL that no dataset can expose (a value inlined as a
/// literal returns exactly the same rows as a bound parameter); and rules whose only witness would be a
/// dataset nobody has (SEC-4's escaping needs a filter value containing <c>%</c>, and no fixture in the
/// suite carries one).</para>
///
/// <para>Assertions on generated SQL are on the presence or absence of ONE token, never on the whole
/// string. <c>ToQueryString</c> is EF's debug rendering and moves between versions; #1644's lesson about
/// a moving target applies to it more than to anything this repo authors.</para>
/// </summary>
public class EfCoreQueryBuilderTests
{
    private static readonly EfCoreProviderSemantics Sqlite = EfCoreProviderSemantics.Sqlite;

    /// <summary>A minimal in-code schema, so a unit test can name a shape no fixture happens to have.
    /// The fixtures remain the oracle for everything they DO cover — this is for the four columns below,
    /// which exist to be filtered wrongly.</summary>
    private static GridSchema Schema() => new()
    {
        Source = "items",
        StableKey = "id",
        FieldOrder = ["id", "name", "code", "qty", "createdAt"],
        Fields = new Dictionary<string, FieldConfig>(StringComparer.Ordinal)
        {
            ["id"] = new() { Type = FieldType.Int64 },
            ["name"] = new() { Type = FieldType.String },
            ["code"] = new() { Type = FieldType.String, Text = new TextOptions { CaseSensitivity = CaseSensitivity.Sensitive } },
            ["qty"] = new() { Type = FieldType.Int64 },
            ["createdAt"] = new() { Type = FieldType.Datetime }
        }
    };

    private static EfCoreQueryRequest Request(FilterNode? filter, PageSpec? page = null) => new()
    {
        Schema = Schema(),
        Filter = filter,
        Page = page ?? new RangePage { StartRow = 0, EndRow = 100 }
    };

    private static ComparisonNode Comparison(string field, ComparisonOperator op, TypedValue? value = null) =>
        new() { Field = field, Op = op, Value = value };

    private static string Sql(EfCoreQueryRequest request) =>
        SqliteFixtureDatabase.GeneratedSql(
            EfCoreQueryBuilder.ApplyFilter(EmptyDatabase(request.Schema).Rows(), request, Sqlite));

    /// <summary>A database with the table but no rows: these tests are about the STATEMENT, and seeding
    /// would invite an assertion about rows that <see cref="EfCoreExecutionEqualityTests"/> already makes
    /// against a dataset designed for it.</summary>
    private static SqliteFixtureDatabase EmptyDatabase(GridSchema schema) =>
        SqliteFixtureDatabase.Create(schema, []);

    // ---- the four shapes the brief names ------------------------------------------------------------

    /// <summary>
    /// SEM-2: <c>blank</c> on a NON-TEXT column is <c>IS NULL</c> and nothing else. The empty-string arm
    /// is a property of the field's declared TYPE, not of the operator — a numeric column has no empty
    /// string, and comparing one to <c>''</c> on a typed engine is either a coercion or an error.
    /// </summary>
    [Fact]
    public void Blank_On_A_Non_Text_Column_Tests_Only_Null()
    {
        var sql = Sql(Request(Comparison("qty", ComparisonOperator.Blank)));

        var statement = sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..];

        Assert.Contains("IS NULL", statement, StringComparison.Ordinal);
        Assert.DoesNotContain(" OR ", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("''", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the text arm for contrast, so the pair pins that the difference is the declared TYPE rather
    /// than an accident of one rendering.
    ///
    /// <para>The empty string arrives as a bound parameter rather than as a <c>''</c> literal in the
    /// statement — visible in <c>ToQueryString</c>'s parameter preamble. That follows the choice
    /// <c>SqlQueryBuilder</c> already made for AR-6's minted <c>true</c>/<c>false</c>: a value the
    /// compiler mints is still a compared value, and SEC-2 wants every compared value bound.</para>
    /// </summary>
    [Fact]
    public void Blank_On_A_Text_Column_Tests_Null_Or_The_Empty_String()
    {
        var sql = Sql(Request(Comparison("name", ComparisonOperator.Blank)));
        var statement = sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..];

        Assert.Contains("IS NULL", statement, StringComparison.Ordinal);
        Assert.Contains(" OR ", statement, StringComparison.Ordinal);

        // The second arm's bound value, in the preamble: `.param set @Value ''`.
        Assert.Contains("''", sql[..sql.IndexOf("SELECT", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    /// <summary>
    /// SEM-8: an empty <c>in</c> list is a constant false and an empty <c>notIn</c> a constant true; EF never
    /// sees an empty <c>Contains</c>, and no NULL disjunct is added next to the constants.
    /// </summary>
    [Theory]
    [InlineData(ComparisonOperator.In, true)]
    [InlineData(ComparisonOperator.NotIn, false)]
    public void An_Empty_Set_Compiles_To_A_Constant(ComparisonOperator op, bool hasWhere)
    {
        var sql = Sql(Request(new ComparisonNode { Field = "name", Op = op, Values = [] }));

        // false: EF keeps a constant `WHERE 0`; true: EF drops the always-true predicate altogether.
        Assert.Equal(hasWhere, sql.Contains("WHERE 0", StringComparison.Ordinal));
        Assert.DoesNotContain("IS NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" IN ", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// An operator/type mismatch is refused rather than translated. The DECLARED type decides, checked
    /// separately from the value's variant: a caller that skipped SEM-5 coercion can hand a
    /// <see cref="StringValue"/> to an <c>int64</c> field, and since
    /// <see cref="SemanticsResolver"/> resolves the pattern-match mechanism only for a <c>string</c>
    /// field, translating it would emit a case-SENSITIVE substring match against a numeric column — a
    /// query that runs.
    /// </summary>
    [Fact]
    public void A_Substring_Match_Against_A_Non_Text_Column_Is_Refused()
    {
        var node = Comparison("qty", ComparisonOperator.Contains, new StringValue { Value = "7" });

        var exception = Assert.Throws<EfCoreCompilationException>(() => Sql(Request(node)));
        Assert.Contains("substring match", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Int64", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>The mirror: the right operator, the wrong value variant. EF would NOT have caught this —
    /// it resolves <c>EF.Property</c> by name and ignores the generic argument — so the check is this
    /// adapter's or nobody's.</summary>
    [Fact]
    public void A_Value_Whose_Variant_Disagrees_With_The_Declared_Type_Is_Refused()
    {
        var node = Comparison("qty", ComparisonOperator.Eq, new StringValue { Value = "7" });

        var exception = Assert.Throws<EfCoreCompilationException>(() => Sql(Request(node)));
        Assert.Contains("TypeCoercionValidator", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>SC-1/SC-2: a colId the schema does not declare never reaches EF. Reported as a caller
    /// precondition failure, because a compiler is reached only after
    /// <see cref="FieldAllowlistValidator"/> — a client-supplied unknown field is stripped or rejected
    /// there, in the layer that can tell Strict from Lenient.</summary>
    [Fact]
    public void A_Field_The_Schema_Does_Not_Declare_Is_Refused()
    {
        var node = Comparison("secretSalary", ComparisonOperator.Eq, new StringValue { Value = "x" });

        var exception = Assert.Throws<EfCoreCompilationException>(() => Sql(Request(node)));
        Assert.Contains("not declared in the GridSchema", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FieldAllowlistValidator", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a SORT field the schema does not declare, which is a different code path from a filter field
    /// and — as <see cref="SemanticsResolver.AppendStableKeyTiebreaker(IReadOnlyList{SortItem}?, string)"/>'s
    /// own remarks record — the only stage that refuses one: there is still no orchestrator that
    /// allowlists sort colIds the way <see cref="GridFilterPipeline"/> allowlists filter fields, so a
    /// bogus sort colId reaches a compiler (or this adapter) and is refused there or nowhere.
    /// </summary>
    [Fact]
    public void A_Sort_Field_The_Schema_Does_Not_Declare_Is_Refused()
    {
        var request = Request(null) with
        {
            Sort = [new SortItem { Field = "ag-Grid-AutoColumn", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }]
        };

        var exception = Assert.Throws<EfCoreCompilationException>(() => SqliteFixtureDatabase.GeneratedSql(
            EfCoreQueryBuilder.ApplyOrdering(EmptyDatabase(request.Schema).Rows(), request, Sqlite)));

        Assert.Contains("not declared in the GridSchema", exception.Message, StringComparison.Ordinal);
    }

    // ---- the refusals that keep the two paths from diverging silently -------------------------------

    [Fact]
    public void A_Request_Carrying_Row_Group_State_Is_Refused_Naming_The_Story_That_Owns_It()
    {
        var request = Request(null) with
        {
            Group = new GroupSpec { GroupCols = ["name"], GroupKeys = [] }
        };

        var exception = Assert.Throws<EfCoreCompilationException>(() => Sql(request));
        Assert.Contains("#1622", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GroupSpec", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Refused INDEPENDENTLY of <see cref="EfCoreQueryRequest.Group"/>, because AG Grid sends
    /// <c>valueCols</c> without <c>rowGroupCols</c> (pivot mode, or a user who added an aggregation
    /// before grouping) and returning leaf rows where the caller asked for totals is a wrong answer that
    /// looks healthy.</summary>
    [Fact]
    public void Aggregations_Without_A_GroupSpec_Are_Refused_Too()
    {
        var request = Request(null) with
        {
            Aggregations = [new Aggregation { Field = "qty", Func = AggregationFunc.Sum }]
        };

        var exception = Assert.Throws<EfCoreCompilationException>(() => Sql(request));
        Assert.Contains("#1622", exception.Message, StringComparison.Ordinal);
        Assert.Contains("valueCols", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one documented divergence this adapter turns into an exception. <c>SqlQueryBuilder</c> binds a
    /// <c>nullValue</c> as a NULL parameter, rendering <c>"col" = $1</c> — which matches no row under
    /// three-valued logic — while EF collapses a comparison against a null parameter to <c>IS NULL</c>,
    /// which matches exactly the null rows. Both are defensible; silently picking one would make the two
    /// paths answer the same request differently, on a shape no fixture covers.
    /// </summary>
    [Fact]
    public void A_NullValue_Comparison_Is_Refused_Rather_Than_Diverging_From_The_Sql_Path()
    {
        var node = Comparison("name", ComparisonOperator.Eq, new NullValue());

        var exception = Assert.Throws<EfCoreCompilationException>(() => Sql(Request(node)));
        Assert.Contains("IS NULL", exception.Message, StringComparison.Ordinal);
        Assert.Contains("blank", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// SC-3 + SEM-7 on a provider whose <c>LIKE</c> folds case: refused, because no
    /// provider-independent EF translation honours the declaration. The alternative is SEM-1's second
    /// Must-Not — returning rows the schema excluded, on well-formed SQL, invisibly.
    /// </summary>
    [Fact]
    public void A_Case_Sensitive_Substring_Match_Is_Refused_Where_Like_Folds_Case()
    {
        var node = Comparison("code", ComparisonOperator.Contains, new StringValue { Value = "AB" });

        var exception = Assert.Throws<EfCoreCompilationException>(() => Sql(Request(node)));
        Assert.Contains("PatternMatchFoldsCase", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GLOB", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>...and accepted on a provider whose <c>LIKE</c> does not, with the case-INsensitive
    /// direction switching to <c>lower()</c> there. The pair is what shows the refusal above is about the
    /// engine rather than about the operator.</summary>
    [Fact]
    public void The_Same_Match_Compiles_Where_Like_Does_Not_Fold_Case()
    {
        var postgresLike = Sqlite with { PatternMatchFoldsCase = false, CaseInsensitiveCollation = "craticula_ci" };

        var sensitive = SqliteFixtureDatabase.GeneratedSql(EfCoreQueryBuilder.ApplyFilter(
            EmptyDatabase(Schema()).Rows(),
            Request(Comparison("code", ComparisonOperator.Contains, new StringValue { Value = "AB" })),
            postgresLike));

        var insensitive = SqliteFixtureDatabase.GeneratedSql(EfCoreQueryBuilder.ApplyFilter(
            EmptyDatabase(Schema()).Rows(),
            Request(Comparison("name", ComparisonOperator.Contains, new StringValue { Value = "AB" })),
            postgresLike));

        Assert.Contains("LIKE", sensitive, StringComparison.Ordinal);
        Assert.DoesNotContain("lower(", sensitive, StringComparison.Ordinal);

        Assert.Contains("lower(", insensitive, StringComparison.Ordinal);
        Assert.Contains("'%ab%'", insensitive, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Qualified_Column_Or_Expression_Mapping_Is_Refused_With_Its_Reason()
    {
        foreach (var (mapping, expected) in new (SqlMapping, string)[]
        {
            (new SqlMapping { QualifiedColumn = "t.name" }, "navigation"),
            (new SqlMapping { Expression = "UPPER(name)" }, "no LINQ expression-tree equivalent")
        })
        {
            var schema = Schema();
            var fields = new Dictionary<string, FieldConfig>(schema.Fields, StringComparer.Ordinal)
            {
                ["name"] = new() { Type = FieldType.String, Physical = new PhysicalMapping { Sql = mapping } }
            };

            var request = new EfCoreQueryRequest
            {
                Schema = schema with { Fields = fields },
                Filter = Comparison("name", ComparisonOperator.Eq, new StringValue { Value = "x" }),
                Page = new NonePage()
            };

            // The model itself refuses first, before any query is built — which is the point: the schema
            // is what is wrong, so the error names the schema rather than a query.
            var exception = Assert.Throws<EfCoreCompilationException>(() => EmptyDatabase(request.Schema).Rows());
            Assert.Contains("physical.sql", exception.Message, StringComparison.Ordinal);
        }
    }

    // ---- properties of the emitted statement no dataset can expose ---------------------------------

    /// <summary>
    /// SEC-2: every compared value is a bound PARAMETER, never a literal spliced into the statement. No
    /// dataset can see the difference — the same rows come back either way — but a literal makes every
    /// distinct value a user types a new statement for the engine to parse and plan, and defeats EF's
    /// compiled-query cache. It is also what AR-4's own first sentence asks for.
    /// </summary>
    [Fact]
    public void Compared_Values_Are_Bound_As_Parameters_Not_Inlined_As_Literals()
    {
        var sql = Sql(Request(Comparison("name", ComparisonOperator.Eq, new StringValue { Value = "acme" })));

        // The statement names a parameter...
        Assert.Contains("@", sql, StringComparison.Ordinal);

        // ...and the value appears only in ToQueryString's parameter PREAMBLE (".param set @Value 'acme'"),
        // never inside the statement itself.
        var statement = sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..];
        Assert.DoesNotContain("'acme'", statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// SEM-1's collation mechanism reaches the emitted statement, on the COLUMN side. The rows in
    /// <see cref="EfCoreExecutionEqualityTests"/> prove it has the right EFFECT; this proves it is the
    /// mechanism <c>SqliteDialect</c> names rather than some other way of folding case that happens to
    /// agree on ASCII data.
    /// </summary>
    [Fact]
    public void A_Case_Insensitive_Comparison_Spells_The_Providers_Collation()
    {
        var sql = Sql(Request(Comparison("name", ComparisonOperator.Eq, new StringValue { Value = "acme" })));

        Assert.Contains("COLLATE NOCASE", sql, StringComparison.Ordinal);
    }

    /// <summary>SC-3: and a case-SENSITIVE field spells nothing on SQLite, whose default collation
    /// already distinguishes case. The two together are why
    /// <see cref="SemanticsResolver.CaseSensitivityMode"/> is three-valued.</summary>
    [Fact]
    public void A_Case_Sensitive_Comparison_Spells_No_Collation_On_Sqlite()
    {
        var sql = Sql(Request(Comparison("code", ComparisonOperator.Eq, new StringValue { Value = "AB-100" })));

        Assert.DoesNotContain("COLLATE", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// SEC-4: a filter value's own <c>%</c>, <c>_</c> and <c>\</c> are escaped so they match literally,
    /// and the <c>ESCAPE</c> clause that gives the escape character meaning is emitted with them (SEM-7's
    /// "never a pattern without a matching ESCAPE"). No fixture in the suite carries such a value, so this
    /// is the only place the rule is exercised — through the same
    /// <see cref="SqlPatternEscaping.Escape"/> the five SQL dialects call.
    /// </summary>
    [Fact]
    public void A_Pattern_Value_Has_Its_Wildcards_Escaped_With_A_Matching_Escape_Clause()
    {
        var sql = Sql(Request(Comparison("name", ComparisonOperator.Contains, new StringValue { Value = @"50%_x\y" })));

        Assert.Contains(@"'%50\%\_x\\y%'", sql, StringComparison.Ordinal);
        Assert.Contains(@"ESCAPE '\'", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// SQL-5's extra T-SQL metacharacter, on the branch no shipped preset turns on:
    /// <see cref="EfCoreProviderSemantics.PatternMatchTreatsLeftBracketAsMetacharacter"/>. Only
    /// <see cref="EfCoreProviderSemantics.Sqlite"/> exists today and it sets the flag
    /// <see langword="false"/>, so without this test the <see langword="true"/> branch ships unexercised
    /// — and its failure mode is the quiet one: an unescaped <c>[</c> opens a character class and
    /// silently <b>widens</b> the match set, returning wrong rows rather than throwing.
    ///
    /// <para><b>The expected value is not read off this adapter.</b> It is what
    /// <c>dialect-parity/022</c> pins for the SQL path on SQL Server — that fixture sends
    /// <c>A[BC]</c> and its <c>mssql.artifact.json</c> binds <c>A\[BC]</c>, with the <c>]</c>
    /// deliberately left alone. Asserting against the fixture's own oracle is what makes this a
    /// statement about SQL-5 rather than a restatement of the code, and it is the same claim AR-4
    /// makes about the two paths: they must not disagree about what a value means.</para>
    /// </summary>
    [Fact]
    public void A_Left_Bracket_Is_Escaped_Where_The_Engine_Treats_It_As_A_Metacharacter()
    {
        const string asPinnedByDialectParity022 = @"A\[BC]";

        Assert.Equal(asPinnedByDialectParity022, SqlPatternEscaping.EscapeIncludingLeftBracket("A[BC]", '\\'));

        // And the flag actually selects it: the default (SQLite) preset must leave the bracket alone.
        Assert.Equal("A[BC]", SqlPatternEscaping.Escape("A[BC]", '\\'));
        Assert.False(EfCoreProviderSemantics.Sqlite.PatternMatchTreatsLeftBracketAsMetacharacter);
    }

    /// <summary>The composition order the two escaping passes require, which is the part that is easy to
    /// get wrong: the escape character introduced by the first pass must not be doubled by the second,
    /// and a literal escape character already doubled carries no <c>[</c> for the second to see.</summary>
    [Fact]
    public void The_Bracket_Pass_Does_Not_Re_Escape_What_The_First_Pass_Introduced()
    {
        // '\' doubles to '\\'; '%' becomes '\%'; only the bare '[' gains a prefix.
        Assert.Equal(@"a\\b\%c\[d", SqlPatternEscaping.EscapeIncludingLeftBracket(@"a\b%c[d", '\\'));
    }

    /// <summary>
    /// AG-3: <c>startRow: 0</c> emits a limit and NO offset. Not a micro-optimisation — it is what keeps
    /// the first grid page's statement the same shape as an export chunk's, so that
    /// <c>EfCoreExportStreamTests</c>' "no OFFSET anywhere" assertion is about the export path rather
    /// than about which page happened to be requested.
    /// </summary>
    [Fact]
    public void A_Page_Starting_At_Row_Zero_Emits_No_Offset()
    {
        var request = Request(null, new RangePage { StartRow = 0, EndRow = 25 });
        var sql = SqliteFixtureDatabase.GeneratedSql(
            EfCoreQueryBuilder.ApplyRowWindow(EmptyDatabase(request.Schema).Rows(), request.Page));

        Assert.Contains("LIMIT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET", sql, StringComparison.Ordinal);
    }

    /// <summary>AG-3: <see cref="RangePage.EndRow"/> is EXCLUSIVE, so the window is
    /// <c>endRow - startRow</c> rows wide. Asserted on the bound values because an off-by-one here
    /// returns a perfectly plausible page.</summary>
    [Fact]
    public void A_Row_Window_Takes_EndRow_Minus_StartRow_Rows()
    {
        var request = Request(null, new RangePage { StartRow = 30, EndRow = 100 });
        var sql = SqliteFixtureDatabase.GeneratedSql(
            EfCoreQueryBuilder.ApplyRowWindow(EmptyDatabase(request.Schema).Rows(), request.Page));

        // The asymmetric window is deliberate: the common 20..40 page has offset == limit and would hide
        // a builder that bound EndRow, which is the mistake DialectIndependenceTests calls out on the SQL
        // side for the same reason.
        Assert.Contains("70", sql, StringComparison.Ordinal);
        Assert.Contains("30", sql, StringComparison.Ordinal);
        Assert.Contains("OFFSET", sql, StringComparison.Ordinal);
    }

    /// <summary>EXP-2: an unpaginated request emits no row window at all.</summary>
    [Fact]
    public void A_None_Page_Emits_Neither_Limit_Nor_Offset()
    {
        var sql = SqliteFixtureDatabase.GeneratedSql(
            EfCoreQueryBuilder.ApplyRowWindow(EmptyDatabase(Schema()).Rows(), new NonePage()));

        Assert.DoesNotContain("LIMIT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// SEM-3: the null bucket is emitted for EVERY key, and its direction is the null POSITION's rather
    /// than the sort direction's. LINQ has no <c>NULLS FIRST</c>/<c>LAST</c>, so this emulation is the
    /// whole of SEM-3 on this path — and the bucket's direction is the half that is easy to get backwards
    /// (it is independent of the value key's).
    /// </summary>
    [Fact]
    public void Every_Sort_Key_Emits_A_Null_Bucket_Whose_Direction_Follows_The_Nulls_Position()
    {
        var descNullsFirst = Request(null) with
        {
            Sort = [new SortItem { Field = "name", Direction = SortDirection.Desc, NullsPosition = NullsPosition.First }]
        };

        var sql = SqliteFixtureDatabase.GeneratedSql(
            EfCoreQueryBuilder.ApplyOrdering(EmptyDatabase(descNullsFirst.Schema).Rows(), descNullsFirst, Sqlite));

        // nulls FIRST -> the bucket descends (true before false); the value key descends on its own account.
        Assert.Contains("IS NULL DESC", sql, StringComparison.Ordinal);

        // ...and SEM-6's appended tiebreaker is there, ascending, with its own bucket.
        Assert.EndsWith("\"id\"", sql.TrimEnd(), StringComparison.Ordinal);
    }

    /// <summary>SEM-6: the tiebreaker is appended when the sort does not already end in the stable key,
    /// and NOT appended when it does — the decision made once, in
    /// <see cref="SemanticsResolver.AppendStableKeyTiebreaker(IReadOnlyList{SortItem}?, string, out bool)"/>,
    /// and only reported here.</summary>
    [Fact]
    public void The_Stable_Key_Tiebreaker_Is_Appended_Once_And_Not_Duplicated()
    {
        var alreadyEndsInIt = Request(null) with
        {
            Sort =
            [
                new SortItem { Field = "name", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last },
                new SortItem { Field = "id", Direction = SortDirection.Desc, NullsPosition = NullsPosition.First }
            ]
        };

        var sql = SqliteFixtureDatabase.GeneratedSql(
            EfCoreQueryBuilder.ApplyOrdering(EmptyDatabase(alreadyEndsInIt.Schema).Rows(), alreadyEndsInIt, Sqlite));

        // The user's own trailing `id desc` stands and is still LAST. An appended tiebreaker would be
        // ascending and would come after it, which both contradicts SEM-6 and makes the user's own last
        // key unreachable — so the direction of the final key is the discriminating assertion, and the
        // occurrence count is not (both arrangements mention "id" twice, once per emulated null bucket).
        Assert.EndsWith("\"id\" DESC", sql.TrimEnd(), StringComparison.Ordinal);

        var appended = Request(null) with
        {
            Sort = [new SortItem { Field = "name", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }]
        };

        Assert.EndsWith(
            "\"id\"",
            SqliteFixtureDatabase.GeneratedSql(
                EfCoreQueryBuilder.ApplyOrdering(EmptyDatabase(appended.Schema).Rows(), appended, Sqlite)).TrimEnd(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// SEM-4: a <c>datetime</c> reaches the provider as the absolute instant in UTC, with the request's
    /// zone treated as provenance and never as an instruction — the same reading
    /// <c>SqlQueryBuilder</c> gets, because both go through
    /// <see cref="IsoInstant.TryParseUtc"/>. The two spellings below name the same moment and must
    /// therefore bind the same value.
    /// </summary>
    [Fact]
    public void Two_Spellings_Of_One_Instant_Bind_The_Same_Value()
    {
        var offset = Sql(Request(Comparison("createdAt", ComparisonOperator.Gte,
            new DatetimeValue { Value = "2024-02-01T12:00:00+02:00", Zone = "Europe/Paris" })));

        var utc = Sql(Request(Comparison("createdAt", ComparisonOperator.Gte,
            new DatetimeValue { Value = "2024-02-01T10:00:00Z", Zone = "UTC" })));

        Assert.Equal(utc, offset);
        Assert.DoesNotContain("Europe/Paris", offset, StringComparison.Ordinal);
    }
}
