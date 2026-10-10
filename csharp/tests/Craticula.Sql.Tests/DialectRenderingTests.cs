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
    [InlineData("oracle", "weird\"name", "\"weird\"\"name\"")]
    public void An_Identifier_Containing_The_Quote_Character_Doubles_It(string dialect, string identifier, string expected) =>
        Assert.Equal(expected, Dialect(dialect).QuoteIdentifier(identifier));

    [Theory]
    [InlineData("mssql", "[plain]")]
    [InlineData("mysql", "`plain`")]
    [InlineData("sqlite", "\"plain\"")]
    // Oracle folds an UNquoted identifier to uppercase, so "conditionally" here would not even
    // be a style question: FROM employees reaches the server as FROM EMPLOYEES.
    [InlineData("oracle", "\"plain\"")]
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
    // Oracle: || concatenation like Postgres/SQLite, but the case-insensitive mechanism is a
    // COLLATE because there is no ILIKE — and the escape clause names ONE backslash, unlike
    // MySQL's doubled one, since a backslash is an ordinary character in an Oracle literal.
    [InlineData("oracle", SqlSubstringAnchor.Start, "\"c\" COLLATE BINARY_CI LIKE :p1 || '%' ESCAPE '\\'")]
    [InlineData("oracle", SqlSubstringAnchor.End, "\"c\" COLLATE BINARY_CI LIKE '%' || :p1 ESCAPE '\\'")]
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
    /// all five dialects, which is the half a byte-comparison of the artifact would not distinguish
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
    /// <c>[</c> opens a character class in T-SQL's <c>LIKE</c> — a wildcard the other four dialects
    /// do not have, Oracle included — so <see cref="SqlPatternEscaping.Escape"/>'s shared three-character set
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
                 { PostgresDialect.Instance, MySqlDialect.Instance, SqliteDialect.Instance, OracleDialect.Instance })
        {
            var sink = new RecordingSink(target);

            target.RenderSubstringMatch(
                target.QuoteIdentifier("c"), SqlSubstringAnchor.Anywhere, negated: false, "A[BC]",
                SemanticsResolver.CaseSensitivityMode.Insensitive, sink);

            // Widening the shared SqlPatternEscaping instead would have put a needless '\[' into
            // four engines' arms of every fixture that mentions a bracket.
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
    /// engine, and <em>each of the five gets there differently</em>. Compiled through the real builder
    /// so the SemanticsResolver decision, not a hand-passed flag, is what selects the rendering.
    ///
    /// <para>Only Postgres's <c>LIKE</c> is case-sensitive on its own. SQLite's is
    /// ASCII-case-insensitive unconditionally (hence <c>GLOB</c>, D3); SQL Server's folds case under
    /// the stock <c>SQL_Latin1_General_CP1_CI_AS</c> server collation (hence the explicit
    /// <c>CS_AS</c> collation); Oracle's depends on the session's <c>NLS_COMP</c>/<c>NLS_SORT</c>
    /// (hence <c>COLLATE BINARY</c>); MySQL's follows the column's declared collation, whose server
    /// default is a <c>_ci</c> family, and is the one case this compiler cannot fix in the statement
    /// — it is a host DDL obligation, recorded rather than guessed.</para>
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
        // Oracle: no ILIKE to avoid and no GLOB to switch to — the same COLLATE operator carries
        // both directions, so the case-SENSITIVE arm is a named collation rather than the absence of
        // one, because its default is whatever the session's NLS_COMP/NLS_SORT say.
        Assert.Contains(
            "\"code\" COLLATE BINARY LIKE '%' || :p1 || '%' ESCAPE '\\'",
            SqlQueryBuilder.Build(request, OracleDialect.Instance).Sql,
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
            target.RenderSetMembership("[c]", negated: false, ["a", "b"], FieldType.String, mode, sink));
        Assert.Equal(
            $"CASE WHEN [c] IS NULL THEN 1 ELSE 0 END ASC, [c] COLLATE {collation} ASC",
            target.RenderSortKey("[c]", SortDirection.Asc, NullsPosition.Last, mode));
        Assert.StartsWith(
            $"[c] COLLATE {collation} LIKE ",
            target.RenderSubstringMatch("[c]", SqlSubstringAnchor.Anywhere, negated: false, "a", mode, sink),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// SEM-1's "same mechanism in every context on a field" on the second engine that has to spell
    /// BOTH directions. Oracle joins SQL Server here for a different reason: its default column
    /// collation is <c>USING_NLS_COMP</c>, which resolves through the <em>session</em>'s
    /// <c>NLS_COMP</c>/<c>NLS_SORT</c>, so a bare comparison folds case on any host that set those
    /// linguistically — SQL-6's condition, reached by session state rather than by a stock server
    /// collation. The suite's artifacts pin only <c>eq</c>, <c>notIn</c> and <c>contains</c>
    /// (<c>dialect-parity/014</c>, <c>/018</c>, <c>/021</c>); the sort key has no case-sensitive
    /// fixture on any dialect, which is what this table covers.
    /// </summary>
    [Theory]
    [InlineData(SemanticsResolver.CaseSensitivityMode.Sensitive, "BINARY")]
    [InlineData(SemanticsResolver.CaseSensitivityMode.Insensitive, "BINARY_CI")]
    public void Oracle_Spells_The_Same_Collation_On_Every_Construct(
        SemanticsResolver.CaseSensitivityMode mode, string collation)
    {
        var target = OracleDialect.Instance;
        var sink = new RecordingSink(target);

        Assert.Equal(
            $"\"c\" COLLATE {collation} = :p1",
            target.RenderScalarComparison("\"c\"", SqlScalarComparison.Equal, "a", mode, sink));
        Assert.Equal(
            $"\"c\" COLLATE {collation} IN (:p2, :p3)",
            target.RenderSetMembership("\"c\"", negated: false, ["a", "b"], FieldType.String, mode, sink));
        Assert.Equal(
            $"\"c\" COLLATE {collation} ASC NULLS LAST",
            target.RenderSortKey("\"c\"", SortDirection.Asc, NullsPosition.Last, mode));
        Assert.StartsWith(
            $"\"c\" COLLATE {collation} LIKE ",
            target.RenderSubstringMatch("\"c\"", SqlSubstringAnchor.Anywhere, negated: false, "a", mode, sink),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The tri-state seam's third and fourth users, together: <c>NotApplicable</c> must spell nothing
    /// on Oracle exactly as on SQL Server, because Oracle's <c>COLLATE</c> operator accepts only a
    /// character operand and rejects a <c>NUMBER</c>/<c>DATE</c> one outright. A boolean seam would
    /// have emitted <c>COLLATE BINARY</c> here, since <c>Sensitive</c> and <c>NotApplicable</c> would
    /// share a branch — the same argument #1639 made for SQL Server, now with a second engine behind
    /// it, so the tri-state is no longer resting on one dialect.
    /// </summary>
    [Fact]
    public void A_NonText_Column_Gets_No_Collation_At_All_On_Oracle()
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
            "SELECT * FROM \"items\" WHERE \"id\" = :p1 ORDER BY \"id\" ASC NULLS LAST",
            SqlQueryBuilder.Build(request, OracleDialect.Instance).Sql);
    }

    /// <summary>
    /// The three dialects that render nothing for <see cref="SemanticsResolver.CaseSensitivityMode.Sensitive"/>,
    /// asserted so that "nothing" stays a deliberate answer rather than an untested one. Postgres and
    /// SQLite need nothing because their default collations already distinguish case; MySQL renders
    /// nothing because the mechanism is the column's declared collation (a host DDL obligation, not a
    /// statement token) — three different reasons, one spelling. The other two (SQL Server, Oracle)
    /// spell it, and are asserted above.
    /// </summary>
    [Theory]
    [InlineData("postgres", "\"c\" = $1")]
    [InlineData("mysql", "`c` = ?")]
    [InlineData("sqlite", "\"c\" = ?")]
    public void The_Three_Dialects_Whose_Default_Distinguishes_Case_Spell_Nothing_For_A_Case_Sensitive_Scalar(string dialect, string expected)
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
    [InlineData("oracle", "\"c\" COLLATE BINARY_CI ASC NULLS LAST")]
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
    [InlineData("oracle", "\"c\" DESC NULLS LAST")]
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
    [InlineData("oracle", "\"c\" COLLATE BINARY_CI IN (:p1, :p2)")]
    public void A_Case_Insensitive_In_List_Collates_The_Column_On_Every_Dialect(string dialect, string expected)
    {
        var target = Dialect(dialect);
        var sink = new RecordingSink(target);

        var sql = target.RenderSetMembership(
            target.QuoteIdentifier("c"), negated: false, ["a", "b"], FieldType.String,
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
    // Column side, like Postgres and SQL Server: Oracle's COLLATE binds tighter than any
    // comparison operator, so the collated operand needs no parentheses.
    [InlineData("oracle", "\"c\" COLLATE BINARY_CI = :p1")]
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
        foreach (var name in new[] { "postgres", "mssql", "mysql", "oracle", "sqlite" })
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
    // Oracle's row-limiting clause spells the offset first, like T-SQL's.
    [InlineData("oracle", "OFFSET :p1 ROWS FETCH NEXT :p2 ROWS ONLY", 30, 70)]
    public void The_Paging_Clause_Binds_Its_Two_Values_In_The_Order_It_Spells_Them(
        string dialect, string expected, int first, int second)
    {
        var target = Dialect(dialect);
        var sink = new RecordingSink(target);

        Assert.Equal(expected, target.RenderPaging(offset: 30, limit: 70, sink));
        Assert.Equal([first, second], sink.Values);
    }

    /// <summary>
    /// GRP-6's row cap, side by side across all five dialects — the axis that made it a seam
    /// member rather than something the builder could spell. Five arms, three clause families: three
    /// of the five are the paging clause minus its offset; SQL Server's <c>FETCH</c> cannot appear
    /// without one, so it writes a literal zero it never binds; and Oracle's <c>FETCH</c> can, so it
    /// writes neither an offset nor a <c>LIMIT</c>.
    ///
    /// <para>The fixtures pin each of these once, inside a whole statement
    /// (<c>group-aggregation/016</c>-<c>019</c>); putting them in one table is what makes the
    /// divergence legible, and pins the parameter count, which is the part a reader is most likely to
    /// assume is uniform.</para>
    /// </summary>
    [Theory]
    [InlineData("postgres", "LIMIT $1")]
    [InlineData("mysql", "LIMIT ?")]
    [InlineData("sqlite", "LIMIT ?")]
    [InlineData("mssql", "OFFSET 0 ROWS FETCH NEXT @p1 ROWS ONLY")]
    // The FIFTH arm and the THIRD clause family, and the reason #1641 predicted this member would fit
    // Oracle unchanged: FETCH is legal here with no OFFSET before it, so no literal zero is needed.
    [InlineData("oracle", "FETCH FIRST :p1 ROWS ONLY")]
    public void The_Row_Cap_Binds_Exactly_One_Value_On_Every_Dialect_However_It_Spells_The_Clause(
        string dialect, string expected)
    {
        var target = Dialect(dialect);
        var sink = new RecordingSink(target);

        Assert.Equal(expected, target.RenderRowLimit(1000, sink));
        Assert.Equal([1000], sink.Values);
    }

    [Fact]
    public void Each_Dialect_Names_Itself_As_Its_Artifacts_Discriminator()
    {
        Assert.Equal("mssql", MssqlDialect.Instance.Name);
        Assert.Equal("mysql", MySqlDialect.Instance.Name);
        Assert.Equal("sqlite", SqliteDialect.Instance.Name);
        Assert.Equal("oracle", OracleDialect.Instance.Name);
    }

    // ---- SQL-7(b): the one input this compiler refuses, and only on this one dialect -------------

    /// <summary>
    /// SQL-7's other half. <c>ir.schema.json</c> lets a <c>stringValue</c> be <c>""</c>, and on Oracle
    /// that binds as NULL — so every comparison against it is UNKNOWN and the statement returns the
    /// wrong rows without erroring. <c>neq ""</c> is the worst of them: it returns <em>only the NULL
    /// rows</em> where the other four return everything-but-empty, i.e. wrong rows rather than no
    /// rows. <see cref="OracleDialect"/> therefore refuses it.
    ///
    /// <para>All three paths a string value can reach are covered here, because getting only the
    /// scalar one is exactly the failure mode SQL-7 itself warns about: a set list with one empty
    /// member makes <c>NOT IN</c> UNKNOWN for every row, and an empty substring value makes the whole
    /// anchored pattern <c>'%' || NULL || '%'</c> NULL, so a <c>contains ""</c> that means "every
    /// non-null row" matches none.</para>
    /// </summary>
    [Theory]
    [InlineData("scalar")]
    [InlineData("set")]
    [InlineData("substring")]
    public void Oracle_Refuses_An_Empty_Bound_String_On_Every_Path_A_String_Value_Can_Reach(string path)
    {
        var sink = new RecordingSink(OracleDialect.Instance);

        var thrown = Assert.Throws<SqlCompilationException>(
            () => RenderWithAnEmptyString(OracleDialect.Instance, path, sink));

        // The message names the engine fact and the operator that does express the question, because
        // a caller hitting this has asked something reasonable that this engine cannot answer.
        Assert.Contains("zero-length string as NULL", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("'blank'/'notBlank'", thrown.Message, StringComparison.Ordinal);

        // Refused before anything was bound, so the sink is never left holding a rejected statement's
        // parameters — which would matter to the set path, where the empty member is not the first.
        Assert.Empty(sink.Values);
    }

    /// <summary>
    /// The other half of the rejection, and the reason it is a dialect decision rather than an IR one:
    /// on every other engine <c>''</c> is an ordinary value, so the same request still compiles and
    /// still binds. Adding a <c>minLength</c> to <c>stringValue</c> would have taken that away from
    /// four correct dialects to fix one, which is why SQL-7(b) is scoped here.
    /// </summary>
    [Theory]
    [InlineData("scalar")]
    [InlineData("set")]
    [InlineData("substring")]
    public void Every_Other_Dialect_Binds_An_Empty_String_Unchanged(string path)
    {
        foreach (var name in new[] { "postgres", "mssql", "mysql", "sqlite" })
        {
            var target = Dialect(name);
            var sink = new RecordingSink(target);

            Assert.NotEmpty(RenderWithAnEmptyString(target, path, sink));
            Assert.NotEmpty(sink.Values);
        }
    }

    /// <summary>The same refusal reached through the real builder, on the operator whose wrong answer
    /// is wrong rows rather than no rows.</summary>
    [Fact]
    public void The_Refusal_Reaches_A_Caller_Compiling_A_Whole_Statement_Not_Just_A_Dialect_Method()
    {
        var request = new SqlQueryRequest
        {
            Schema = CaseSensitiveSchema,
            Filter = new ComparisonNode
            {
                Field = "code",
                Op = ComparisonOperator.Neq,
                Value = new StringValue { Value = "" }
            },
            Page = new NonePage()
        };

        Assert.Throws<SqlCompilationException>(() => SqlQueryBuilder.Build(request, OracleDialect.Instance));

        // ...and the identical request still compiles on Postgres, where '' is a value like any other.
        Assert.Equal(
            "SELECT * FROM \"items\" WHERE (\"code\" <> $1 OR \"code\" IS NULL) ORDER BY \"id\" ASC NULLS LAST",
            SqlQueryBuilder.Build(request, PostgresDialect.Instance).Sql);
    }

    private static string RenderWithAnEmptyString(ISqlDialect target, string path, ISqlParameterSink sink) =>
        path switch
        {
            "scalar" => target.RenderScalarComparison(
                target.QuoteIdentifier("c"), SqlScalarComparison.NotEqual, "",
                SemanticsResolver.CaseSensitivityMode.Insensitive, sink),

            // The empty member is deliberately not first: a guard that checked only values[0], or that
            // bound as it went, would pass the scalar case and still leave this one half-bound.
            "set" => target.RenderSetMembership(
                target.QuoteIdentifier("c"), negated: true, ["x", ""], FieldType.String,
                SemanticsResolver.CaseSensitivityMode.Insensitive, sink),

            "substring" => target.RenderSubstringMatch(
                target.QuoteIdentifier("c"), SqlSubstringAnchor.Anywhere, negated: false, "",
                SemanticsResolver.CaseSensitivityMode.Insensitive, sink),

            _ => throw new ArgumentOutOfRangeException(nameof(path), path, "Unknown value path.")
        };

    private static ISqlDialect Dialect(string name) => name switch
    {
        // Postgres is #1638's and has its own conformance suite; it is reachable here only for the
        // few assertions that are about all FIVE dialects agreeing (the shared comparison-symbol
        // table, "nothing spelled for the case-sensitive direction", and SQL-7(b)'s inverse).
        "postgres" => PostgresDialect.Instance,
        "mssql" => MssqlDialect.Instance,
        "mysql" => MySqlDialect.Instance,
        "oracle" => OracleDialect.Instance,
        "sqlite" => SqliteDialect.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown dialect.")
    };
}
