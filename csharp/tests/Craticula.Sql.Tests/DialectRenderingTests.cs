using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Sql.Tests;

/// <summary>
/// The renderings the fixture suite does NOT reach, asserted directly against each dialect rather
/// than through <see cref="SqlQueryBuilder"/>.
///
/// <para>Unlike <see cref="DialectConformanceFixtureTests"/>, these tests DO carry literals — they
/// have to: there is no artifact to compare against, which is precisely why they exist. Each one
/// covers a member of <see cref="ISqlDialect"/> on an input no <c>dialect-parity</c> case produces:
/// an identifier containing the engine's own quote character, the <c>startsWith</c>/<c>endsWith</c>
/// anchors (every fixture uses <c>contains</c>), a case-insensitive ORDER BY key on SQL
/// Server/SQLite (only MySQL's is fixtured, by <c>002</c>), and the <c>DESC</c>+<c>NULLS LAST</c>
/// diagonal the AG Grid adapter's defaults never produce.</para>
///
/// <para>Two paths here <em>are</em> fixtured, and only their edges live in this file. SQLite's
/// <c>GLOB</c> path (D3) is pinned by <c>dialect-parity/021</c>; the artifact fixes the operator, the
/// <c>*</c> anchoring, the absent <c>ESCAPE</c> clause and one escaped metacharacter, leaving the
/// escaping's edge behaviour (<c>?</c>, <c>[</c>, an already-class-shaped value, and the
/// deliberately-unescaped <c>]</c>/<c>^</c>) here. T-SQL's <c>[</c> escaping is pinned by
/// <c>dialect-parity/022</c> for one value, leaving the negated-class, range and
/// escape-character-interaction cases here. Both are places where a wrong answer is valid SQL with a
/// silently different match set.</para>
/// </summary>
public class DialectRenderingTests
{
    /// <summary>A minimal <see cref="ISqlParameterSink"/>: the real
    /// <c>SqlParameterBag</c> is internal to <c>Craticula.Sql</c>, and a test that only exercises one
    /// dialect method has no builder to get one from.</summary>
    private sealed class RecordingSink(ISqlDialect dialect) : ISqlParameterSink
    {
        public List<object?> Values { get; } = [];

        public string Bind(object? value)
        {
            Values.Add(value);
            return dialect.ParameterPlaceholder(Values.Count - 1);
        }
    }

    // ---- SQL-1 quoting: the engine's own quote character, escaped the engine's own way -----------

    [Theory]
    // A ']' inside a bracket-quoted T-SQL identifier is doubled. ProbeDialect's superficially
    // identical '[' + name + ']' omits this, which is the one thing #1639 must not have copied.
    [InlineData("mssql", "weird]name", "[weird]]name]")]
    [InlineData("mysql", "weird`name", "`weird``name`")]
    [InlineData("sqlite", "weird\"name", "\"weird\"\"name\"")]
    public void An_Identifier_Containing_The_Quote_Character_Doubles_It(string dialect, string identifier, string expected) =>
        Assert.Equal(expected, Dialect(dialect).QuoteIdentifier(identifier));

    [Theory]
    [InlineData("mssql", "[plain]")]
    [InlineData("mysql", "`plain`")]
    [InlineData("sqlite", "\"plain\"")]
    public void Every_Identifier_Is_Quoted_Unconditionally(string dialect, string expected) =>
        // SQL-1's "must NOT quote conditionally": no keyword blocklist, no "only mixed-case names".
        Assert.Equal(expected, Dialect(dialect).QuoteIdentifier("plain"));

    // ---- SEM-7 anchors the fixtures never produce (every case uses `contains`) -------------------

    [Theory]
    [InlineData("mssql", SqlSubstringAnchor.Start, "[c] COLLATE Latin1_General_CI_AS LIKE @p1 + '%' ESCAPE '\\'")]
    [InlineData("mssql", SqlSubstringAnchor.End, "[c] COLLATE Latin1_General_CI_AS LIKE '%' + @p1 ESCAPE '\\'")]
    [InlineData("mysql", SqlSubstringAnchor.Start, "`c` LIKE CONCAT(?, '%') ESCAPE '\\\\'")]
    [InlineData("mysql", SqlSubstringAnchor.End, "`c` LIKE CONCAT('%', ?) ESCAPE '\\\\'")]
    [InlineData("sqlite", SqlSubstringAnchor.Start, "\"c\" LIKE ? || '%' ESCAPE '\\'")]
    [InlineData("sqlite", SqlSubstringAnchor.End, "\"c\" LIKE '%' || ? ESCAPE '\\'")]
    public void The_Anchored_Substring_Forms_Concatenate_With_Their_Own_Operator(
        string dialect, SqlSubstringAnchor anchor, string expected)
    {
        var target = Dialect(dialect);
        var sink = new RecordingSink(target);

        var sql = target.RenderSubstringMatch(
            target.QuoteIdentifier("c"), anchor, negated: false, "smith",
            SemanticsResolver.CaseSensitivityMode.Insensitive, sink);

        Assert.Equal(expected, sql);
        Assert.Equal(["smith"], sink.Values);
    }

    /// <summary>
    /// D1, asserted on the token rather than on an artifact: MySQL spells the escape character
    /// <c>'\\'</c> because a backslash is its string-literal escape unless <c>NO_BACKSLASH_ESCAPES</c>
    /// is on (it is off by default), so <c>ESCAPE '\'</c> is an unterminated literal and the
    /// statement does not parse. The escape CHARACTER is unchanged — the bound value is identical on
    /// all four dialects, which is the half a byte-comparison of the artifact would not distinguish
    /// from a genuine escaping change.
    /// </summary>
    [Fact]
    public void MySql_Spells_The_Escape_Character_Doubled_But_Escapes_The_Value_Identically()
    {
        var mysql = new RecordingSink(MySqlDialect.Instance);
        var postgres = new RecordingSink(PostgresDialect.Instance);

        var mysqlSql = MySqlDialect.Instance.RenderSubstringMatch(
            "`c`", SqlSubstringAnchor.Anywhere, negated: false, "50%_off",
            SemanticsResolver.CaseSensitivityMode.Insensitive, mysql);
        PostgresDialect.Instance.RenderSubstringMatch(
            "\"c\"", SqlSubstringAnchor.Anywhere, negated: false, "50%_off",
            SemanticsResolver.CaseSensitivityMode.Insensitive, postgres);

        Assert.EndsWith(@"ESCAPE '\\'", mysqlSql, StringComparison.Ordinal);
        Assert.Equal(mysql.Values, postgres.Values);
        Assert.Equal([@"50\%\_off"], mysql.Values);
    }

    // ---- SEC-4/SEM-7: T-SQL's fourth LIKE metacharacter ------------------------------------------

    /// <summary>
    /// <c>[</c> opens a character class in T-SQL's <c>LIKE</c> — a wildcard the other three v1
    /// dialects do not have — so <see cref="SqlPatternEscaping.Escape"/>'s shared three-character set
    /// is not the whole set on this one dialect, and an unescaped user value would still behave as a
    /// pattern: SEM-7's own Must-Not. The fixture oracle covers the plain case
    /// (<c>dialect-parity/022</c>); these are the edges around it, including the three characters
    /// deliberately left alone.
    /// </summary>
    [Theory]
    // The defect itself: unescaped, `A[BC]` matches "AB" or "AC" rather than the literal string.
    [InlineData("A[BC]", @"A\[BC]")]
    [InlineData("[", @"\[")]
    // A negated class and a range widen the match set further, and both are neutralised by escaping
    // the opener alone.
    [InlineData("[^x]", @"\[^x]")]
    [InlineData("[a-z]", @"\[a-z]")]
    // ']', '^' and '-' are special only INSIDE a class, and after this pass no '[' from the value can
    // open one — so escaping them would change the bound value for no gain.
    [InlineData("]", "]")]
    [InlineData("^-]", "^-]")]
    // SEC-4's shared three still apply, and the escape character is doubled BEFORE '[' is prefixed,
    // so the '\' this pass introduces is never itself escaped.
    [InlineData("50%_off", @"50\%\_off")]
    [InlineData(@"a\[b", @"a\\\[b")]
    [InlineData("plain", "plain")]
    public void A_Bracket_Is_Escaped_On_SqlServer_Because_TSqls_Like_Treats_It_As_A_Wildcard(
        string value, string expectedBound)
    {
        var sink = new RecordingSink(MssqlDialect.Instance);

        MssqlDialect.Instance.RenderSubstringMatch(
            "[c]", SqlSubstringAnchor.Anywhere, negated: false, value,
            SemanticsResolver.CaseSensitivityMode.Insensitive, sink);

        Assert.Equal([expectedBound], sink.Values);
    }

    [Fact]
    public void No_Other_Dialect_Escapes_A_Bracket_Because_Their_Like_Has_No_Character_Class()
    {
        foreach (var target in new ISqlDialect[]
                 { PostgresDialect.Instance, MySqlDialect.Instance, SqliteDialect.Instance })
        {
            var sink = new RecordingSink(target);

            target.RenderSubstringMatch(
                target.QuoteIdentifier("c"), SqlSubstringAnchor.Anywhere, negated: false, "A[BC]",
                SemanticsResolver.CaseSensitivityMode.Insensitive, sink);

            // Widening the shared SqlPatternEscaping instead would have put a needless '\[' into
            // three engines' arms of every fixture that mentions a bracket.
            Assert.Equal(["A[BC]"], sink.Values);
        }
    }

    // ---- D3: SQLite's GLOB path, whose escaping edges no fixture reaches --------------------------

    /// <summary>
    /// GLOB's metacharacters are neutralised by wrapping each in a single-member character class,
    /// because SQLite's <c>GLOB</c> accepts no <c>ESCAPE</c> clause at all — so
    /// <see cref="SqlPatternEscaping.Escape"/>'s prefix-with-a-character algorithm is not merely a
    /// different spelling here, it would not work.
    /// </summary>
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("*", "[*]")]
    [InlineData("?", "[?]")]
    [InlineData("[", "[[]")]
    [InlineData("a*b?c[d", "a[*]b[?]c[[]d")]
    [InlineData("**", "[*][*]")]
    // A value that already looks like a character class must match LITERALLY, not act as one: the
    // '[' becomes '[[]', the '*' becomes '[*]', and the trailing ']' stays as itself — so the
    // pattern reads "literal '[', literal '*', literal ']'".
    [InlineData("[*]", "[[][*]]")]
    [InlineData("[?]", "[[][?]]")]
    // ']' is deliberately NOT escaped: outside a class it is an ordinary character, and every class
    // this helper opens is closed immediately, so no ']' from the value can close one.
    [InlineData("]", "]")]
    [InlineData("a]b", "a]b")]
    // '^' only negates a class when it is the FIRST character inside one; the classes emitted here
    // always begin with the escaped character itself, so a '^' in the value stays literal.
    [InlineData("^", "^")]
    [InlineData("[^a]", "[[]^a]")]
    // SEC-4's LIKE metacharacters are NOT GLOB's: '%' and '_' are ordinary characters to GLOB, and
    // escaping them here would change what the user searched for.
    [InlineData("50% off_now", "50% off_now")]
    [InlineData("\\", "\\")]
    [InlineData("", "")]
    public void Escaping_A_Glob_Pattern_Wraps_Each_Metacharacter_In_A_Character_Class(string value, string expected) =>
        Assert.Equal(expected, SqliteDialect.EscapeGlobPattern(value));

    [Theory]
    [InlineData(SqlSubstringAnchor.Anywhere, false, "\"c\" GLOB '*' || ? || '*'")]
    [InlineData(SqlSubstringAnchor.Start, false, "\"c\" GLOB ? || '*'")]
    [InlineData(SqlSubstringAnchor.End, false, "\"c\" GLOB '*' || ?")]
    // SQL-3: the negation goes into the operator, never an outer NOT (...).
    [InlineData(SqlSubstringAnchor.Anywhere, true, "\"c\" NOT GLOB '*' || ? || '*'")]
    public void A_Case_Sensitive_Substring_Match_Uses_Glob_With_Star_Wildcards_And_No_Escape_Clause(
        SqlSubstringAnchor anchor, bool negated, string expected)
    {
        var sink = new RecordingSink(SqliteDialect.Instance);

        var sql = SqliteDialect.Instance.RenderSubstringMatch(
            "\"c\"", anchor, negated, "smith", SemanticsResolver.CaseSensitivityMode.Sensitive, sink);

        Assert.Equal(expected, sql);
        Assert.DoesNotContain("ESCAPE", sql, StringComparison.Ordinal);
        Assert.Equal(["smith"], sink.Values);
    }

    [Fact]
    public void The_Glob_Path_Binds_The_Glob_Escaping_Not_The_Like_Escaping()
    {
        var sink = new RecordingSink(SqliteDialect.Instance);

        SqliteDialect.Instance.RenderSubstringMatch(
            "\"c\"", SqlSubstringAnchor.Anywhere, negated: false, "a*b%c",
            SemanticsResolver.CaseSensitivityMode.Sensitive, sink);

        // '%' stays literal (it is not a GLOB metacharacter) and '*' becomes a class — the exact
        // inverse of what SqlPatternEscaping would have produced.
        Assert.Equal(["a[*]b%c"], sink.Values);
    }

    /// <summary>
    /// The reason D3 exists, end to end, and the reason the seam is three-valued: a field the schema
    /// declares <c>caseSensitivity: "sensitive"</c> must actually compare case-sensitively on every
    /// engine, and <em>each of the four gets there differently</em>. Compiled through the real builder
    /// so the SemanticsResolver decision, not a hand-passed flag, is what selects the rendering.
    ///
    /// <para>Only Postgres's <c>LIKE</c> is case-sensitive on its own. SQLite's is
    /// ASCII-case-insensitive unconditionally (hence <c>GLOB</c>, D3); SQL Server's folds case under
    /// the stock <c>SQL_Latin1_General_CP1_CI_AS</c> server collation (hence the explicit
    /// <c>CS_AS</c> collation); MySQL's follows the column's declared collation, whose server default
    /// is a <c>_ci</c> family, and is the one case this compiler cannot fix in the statement — it is a
    /// host DDL obligation, recorded rather than guessed.</para>
    /// </summary>
    [Fact]
    public void A_Case_Sensitive_Field_Reaches_Case_Sensitivity_Differently_On_Each_Dialect()
    {
        var request = new SqlQueryRequest
        {
            Schema = CaseSensitiveSchema,
            Filter = new ComparisonNode
            {
                Field = "code",
                Op = ComparisonOperator.Contains,
                Value = new StringValue { Value = "AB" }
            },
            Page = new NonePage()
        };

        Assert.Equal(
            "SELECT * FROM \"items\" WHERE \"code\" GLOB '*' || ? || '*' ORDER BY \"id\" ASC NULLS LAST",
            SqlQueryBuilder.Build(request, SqliteDialect.Instance).Sql);

        Assert.Contains(
            "[code] COLLATE Latin1_General_CS_AS LIKE '%' + @p1 + '%' ESCAPE '\\'",
            SqlQueryBuilder.Build(request, MssqlDialect.Instance).Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "`code` LIKE CONCAT('%', ?, '%') ESCAPE '\\\\'",
            SqlQueryBuilder.Build(request, MySqlDialect.Instance).Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"code\" LIKE '%' || $1 || '%' ESCAPE '\\'",
            SqlQueryBuilder.Build(request, PostgresDialect.Instance).Sql,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the tri-state seam, and the reason SQL Server could not simply emit a
    /// case-sensitive collation from the old <c>false</c> branch: the <c>id</c> sort key here is an
    /// <c>int64</c>, and <c>ORDER BY [id] COLLATE Latin1_General_CS_AS</c> is not a stylistic wart but
    /// "COLLATE clause cannot be used on expressions of type int" — a hard T-SQL error. A boolean
    /// seam spells this identically to the case above; a three-valued one cannot.
    /// </summary>
    [Fact]
    public void A_NonText_Column_Gets_No_Collation_At_All_On_SqlServer()
    {
        var request = new SqlQueryRequest
        {
            Schema = CaseSensitiveSchema,
            Filter = new ComparisonNode
            {
                Field = "id",
                Op = ComparisonOperator.Eq,
                Value = new Int64Value { Value = 7 }
            },
            Page = new NonePage()
        };

        Assert.Equal(
            "SELECT * FROM [items] WHERE [id] = @p1 " +
            "ORDER BY CASE WHEN [id] IS NULL THEN 1 ELSE 0 END ASC, [id] ASC",
            SqlQueryBuilder.Build(request, MssqlDialect.Instance).Sql);
    }

    /// <summary>
    /// SEM-1's "same mechanism in every context on a field", now checkable in the case-SENSITIVE
    /// direction too: on SQL Server a declared-sensitive field must carry its collation on the sort
    /// key and the in-list exactly as it does on the comparison, or the grid filters one way and
    /// orders/paginates another — the drill-down half of SEM-1's third Must-Not.
    /// </summary>
    [Theory]
    [InlineData(SemanticsResolver.CaseSensitivityMode.Sensitive, "Latin1_General_CS_AS")]
    [InlineData(SemanticsResolver.CaseSensitivityMode.Insensitive, "Latin1_General_CI_AS")]
    public void SqlServer_Spells_The_Same_Collation_On_Every_Construct(
        SemanticsResolver.CaseSensitivityMode mode, string collation)
    {
        var target = MssqlDialect.Instance;
        var sink = new RecordingSink(target);

        Assert.Equal(
            $"[c] COLLATE {collation} = @p1",
            target.RenderScalarComparison("[c]", SqlScalarComparison.Equal, "a", mode, sink));
        Assert.Equal(
            $"[c] COLLATE {collation} IN (@p2, @p3)",
            target.RenderSetMembership("[c]", negated: false, ["a", "b"], mode, sink));
        Assert.Equal(
            $"CASE WHEN [c] IS NULL THEN 1 ELSE 0 END ASC, [c] COLLATE {collation} ASC",
            target.RenderSortKey("[c]", SortDirection.Asc, NullsPosition.Last, mode));
        Assert.StartsWith(
            $"[c] COLLATE {collation} LIKE ",
            target.RenderSubstringMatch("[c]", SqlSubstringAnchor.Anywhere, negated: false, "a", mode, sink),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The three dialects that render nothing for <see cref="SemanticsResolver.CaseSensitivityMode.Sensitive"/>,
    /// asserted so that "nothing" stays a deliberate answer rather than an untested one. Postgres and
    /// SQLite need nothing because their default collations already distinguish case; MySQL renders
    /// nothing because the mechanism is the column's declared collation (a host DDL obligation, not a
    /// statement token) — three different reasons, one spelling.
    /// </summary>
    [Theory]
    [InlineData("postgres", "\"c\" = $1")]
    [InlineData("mysql", "`c` = ?")]
    [InlineData("sqlite", "\"c\" = ?")]
    public void The_Other_Three_Dialects_Spell_Nothing_For_A_Case_Sensitive_Scalar(string dialect, string expected)
    {
        var target = Dialect(dialect);
        var sink = new RecordingSink(target);

        Assert.Equal(
            expected,
            target.RenderScalarComparison(
                target.QuoteIdentifier("c"), SqlScalarComparison.Equal, "a",
                SemanticsResolver.CaseSensitivityMode.Sensitive, sink));
    }

    private static readonly GridSchema CaseSensitiveSchema = new()
    {
        Source = "items",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = new()
            {
                Type = FieldType.Int64,
                Physical = new PhysicalMapping { Sql = new SqlMapping { Column = "id" } }
            },
            ["code"] = new()
            {
                Type = FieldType.String,
                Text = new TextOptions { CaseSensitivity = CaseSensitivity.Sensitive },
                Physical = new PhysicalMapping { Sql = new SqlMapping { Column = "code" } }
            }
        }
    };

    // ---- SEM-1 on constructs the fixtures leave uncovered ----------------------------------------

    /// <summary>
    /// A case-insensitive ORDER BY key. Only MySQL's is fixtured (<c>dialect-parity/002</c>, where
    /// the answer is "emit nothing"); every other case in the category sorts on <c>id</c>/<c>salary</c>,
    /// which are not strings. SEM-1 requires the SAME mechanism as the exact-match family — hence a
    /// collation on all three, never <c>LIKE</c>/<c>GLOB</c>.
    /// </summary>
    [Theory]
    [InlineData("mssql", "CASE WHEN [c] IS NULL THEN 1 ELSE 0 END ASC, [c] COLLATE Latin1_General_CI_AS ASC")]
    [InlineData("mysql", "ISNULL(`c`) ASC, `c` ASC")]
    [InlineData("sqlite", "\"c\" COLLATE NOCASE ASC NULLS LAST")]
    public void A_Case_Insensitive_Sort_Key_Uses_The_Collation_Mechanism(string dialect, string expected)
    {
        var target = Dialect(dialect);

        Assert.Equal(
            expected,
            target.RenderSortKey(
                target.QuoteIdentifier("c"), SortDirection.Asc, NullsPosition.Last,
                SemanticsResolver.CaseSensitivityMode.Insensitive));
    }

    /// <summary>
    /// SEM-3's bucket direction is independent of the column's own direction — the combination
    /// <c>DESC</c> + <c>NULLS LAST</c> proves it, and no fixture produces it (the AG Grid adapter's
    /// defaults pair <c>asc</c> with <c>last</c> and <c>desc</c> with <c>first</c>, so only the two
    /// diagonal combinations are ever fixtured).
    /// </summary>
    [Theory]
    [InlineData("mssql", "CASE WHEN [c] IS NULL THEN 1 ELSE 0 END ASC, [c] DESC")]
    [InlineData("mysql", "ISNULL(`c`) ASC, `c` DESC")]
    [InlineData("sqlite", "\"c\" DESC NULLS LAST")]
    public void The_Null_Bucket_Direction_Follows_The_Nulls_Position_Not_The_Sort_Direction(string dialect, string expected)
    {
        var target = Dialect(dialect);

        Assert.Equal(
            expected,
            target.RenderSortKey(
                target.QuoteIdentifier("c"), SortDirection.Desc, NullsPosition.Last,
                SemanticsResolver.CaseSensitivityMode.NotApplicable));
    }

    /// <summary>
    /// SQL-4 in-list on a case-INSENSITIVE field, where SQLite's collation has to sit on the COLUMN
    /// even though its scalar comparison collates the value: SQLite resolves <c>x IN (...)</c> from
    /// <c>x</c>'s collating sequence alone, so the value-side form would be silently ignored and the
    /// comparison would run case-sensitively. Fixtured for SQLite by <c>016</c>; the contrast with
    /// <see cref="A_Scalar_Comparison_Collates_The_Side_Its_Engine_Reads"/> is the point here.
    /// </summary>
    [Theory]
    [InlineData("mssql", "[c] COLLATE Latin1_General_CI_AS IN (@p1, @p2)")]
    [InlineData("mysql", "`c` IN (?, ?)")]
    [InlineData("sqlite", "\"c\" COLLATE NOCASE IN (?, ?)")]
    public void A_Case_Insensitive_In_List_Collates_The_Column_On_Every_Dialect(string dialect, string expected)
    {
        var target = Dialect(dialect);
        var sink = new RecordingSink(target);

        var sql = target.RenderSetMembership(
            target.QuoteIdentifier("c"), negated: false, ["a", "b"],
            SemanticsResolver.CaseSensitivityMode.Insensitive, sink);

        Assert.Equal(expected, sql);

        // SQL-4: N scalar parameters on all three, never Postgres's single array.
        Assert.Equal(["a", "b"], sink.Values);
    }

    /// <summary>SQL Server collates the COLUMN (index-friendly, like Postgres), MySQL spells nothing
    /// at all, and SQLite collates the VALUE — the contrast with
    /// <see cref="A_Case_Insensitive_In_List_Collates_The_Column_On_Every_Dialect"/> is the point:
    /// SQLite's collation position is per CONSTRUCT, not per dialect.</summary>
    [Theory]
    [InlineData("mssql", "[c] COLLATE Latin1_General_CI_AS = @p1")]
    [InlineData("mysql", "`c` = ?")]
    [InlineData("sqlite", "\"c\" = ? COLLATE NOCASE")]
    public void A_Scalar_Comparison_Collates_The_Side_Its_Engine_Reads(string dialect, string expected)
    {
        var target = Dialect(dialect);
        var sink = new RecordingSink(target);

        var sql = target.RenderScalarComparison(
            target.QuoteIdentifier("c"), SqlScalarComparison.Equal, "a",
            SemanticsResolver.CaseSensitivityMode.Insensitive, sink);

        Assert.Equal(expected, sql);
    }

    [Theory]
    [InlineData(SqlScalarComparison.NotEqual, "<>")]
    [InlineData(SqlScalarComparison.GreaterThanOrEqual, ">=")]
    [InlineData(SqlScalarComparison.LessThan, "<")]
    [InlineData(SqlScalarComparison.LessThanOrEqual, "<=")]
    public void Every_Dialect_Spells_The_Comparison_Symbols_Identically(SqlScalarComparison comparison, string symbol)
    {
        // These are ANSI and vary in no v1 dialect — asserted so that a dialect "helpfully" emitting
        // T-SQL's `!=` or MySQL's `<=>` shows up here rather than in a port's artifact diff. Since
        // this is one invariant table rather than four decisions, it lives in SqlComparisonSymbols
        // (the same reasoning SqlPatternEscaping records for SEC-4's loop); this test is what pins
        // that a dialect still routes through it.
        foreach (var name in new[] { "postgres", "mssql", "mysql", "sqlite" })
        {
            var target = Dialect(name);
            var sql = target.RenderScalarComparison(
                "c", comparison, 1L, SemanticsResolver.CaseSensitivityMode.NotApplicable, new RecordingSink(target));

            Assert.StartsWith($"c {symbol} ", sql, StringComparison.Ordinal);
        }
    }

    // ---- paging ----------------------------------------------------------------------------------

    /// <summary>
    /// The bind-order axis <see cref="ISqlDialect.RenderPaging"/> exists for, with a deliberately
    /// asymmetric window so a dialect that swapped the two values would still be caught (every
    /// fixture pages 0..100, where offset 0 hides an ordering mistake in the SQL but not in
    /// <c>params</c>).
    /// </summary>
    [Theory]
    [InlineData("mssql", "OFFSET @p1 ROWS FETCH NEXT @p2 ROWS ONLY", 30, 70)]
    [InlineData("mysql", "LIMIT ? OFFSET ?", 70, 30)]
    [InlineData("sqlite", "LIMIT ? OFFSET ?", 70, 30)]
    public void The_Paging_Clause_Binds_Its_Two_Values_In_The_Order_It_Spells_Them(
        string dialect, string expected, int first, int second)
    {
        var target = Dialect(dialect);
        var sink = new RecordingSink(target);

        Assert.Equal(expected, target.RenderPaging(offset: 30, limit: 70, sink));
        Assert.Equal([first, second], sink.Values);
    }

    [Fact]
    public void Each_Dialect_Names_Itself_As_Its_Artifacts_Discriminator()
    {
        Assert.Equal("mssql", MssqlDialect.Instance.Name);
        Assert.Equal("mysql", MySqlDialect.Instance.Name);
        Assert.Equal("sqlite", SqliteDialect.Instance.Name);
    }

    private static ISqlDialect Dialect(string name) => name switch
    {
        // Postgres is #1638's and has its own conformance suite; it is reachable here only for the
        // few assertions that are about all FOUR v1 dialects agreeing (the shared comparison-symbol
        // table, and "nothing spelled for the case-sensitive direction").
        "postgres" => PostgresDialect.Instance,
        "mssql" => MssqlDialect.Instance,
        "mysql" => MySqlDialect.Instance,
        "sqlite" => SqliteDialect.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown dialect.")
    };
}
