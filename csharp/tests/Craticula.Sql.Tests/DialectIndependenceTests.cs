using System.Text.RegularExpressions;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Sql.Tests;

/// <summary>
/// BM-1's Definition of Done, made testable now instead of trusted to #1639: "adding a new dialect
/// requires zero changes to the query builder"
/// (<c>docs/architecture/c4-components-sql-compiler.md</c>). Two independent angles —
/// <see cref="ProbeDialect"/> driving the very same <see cref="SqlQueryBuilder"/> to a completely
/// differently-spelled statement, and a source-level check that no dialect token leaked into the
/// builder in the first place.
/// </summary>
public class DialectIndependenceTests
{
    private static FieldConfig Field(FieldType type, string column) => new()
    {
        Type = type,
        Physical = new PhysicalMapping { Sql = new SqlMapping { Column = column } }
    };

    private static readonly GridSchema Schema = new()
    {
        Source = "employees",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = Field(FieldType.Int64, "id"),
            ["lastName"] = Field(FieldType.String, "last_name"),
            ["department"] = Field(FieldType.String, "department"),
            ["age"] = Field(FieldType.Int64, "age")
        }
    };

    /// <summary>One request touching every axis the dialect interface abstracts: quoting, the
    /// case-insensitive exact-match mechanism, the substring mechanism and its escaping, set
    /// membership, null-inclusive negation, sort keys with null placement, and paging.</summary>
    private static SqlQueryRequest Request => new()
    {
        Schema = Schema,
        Filter = new AndNode
        {
            Nodes =
            [
                new ComparisonNode { Field = "lastName", Op = ComparisonOperator.Eq, Value = new StringValue { Value = "smith" } },
                new ComparisonNode { Field = "lastName", Op = ComparisonOperator.Contains, Value = new StringValue { Value = "50%" } },
                new ComparisonNode
                {
                    Field = "department",
                    Op = ComparisonOperator.NotIn,
                    Values = [new StringValue { Value = "sales" }, new StringValue { Value = "support" }]
                }
            ]
        },
        Sort = [new SortItem { Field = "age", Direction = SortDirection.Desc, NullsPosition = NullsPosition.First }],
        Page = new RangePage { StartRow = 20, EndRow = 40 }
    };

    [Fact]
    public void The_Same_Builder_And_The_Same_Request_Produce_Each_Dialects_Own_Spelling()
    {
        var postgres = SqlQueryBuilder.Build(Request, PostgresDialect.Instance);
        var probe = SqlQueryBuilder.Build(Request, ProbeDialect.Instance);

        Assert.Equal(
            "SELECT * FROM \"employees\" WHERE (" +
            "\"last_name\" COLLATE craticula_ci = $1 AND " +
            "\"last_name\" ILIKE '%' || $2 || '%' ESCAPE '\\' AND " +
            "(\"department\" COLLATE craticula_ci <> ALL($3) OR \"department\" IS NULL)) " +
            "ORDER BY \"age\" DESC NULLS FIRST, \"id\" ASC NULLS LAST LIMIT $4 OFFSET $5",
            postgres.Sql);

        Assert.Equal(
            "SELECT * FROM [employees] WHERE (" +
            "[last_name] = :p1 COLLATE PROBE_CI AND " +
            "[last_name] COLLATE PROBE_CI LIKE CONCAT('%', :p2, '%') ESCAPE '!' AND " +
            "([department] COLLATE PROBE_CI NOT IN (:p3, :p4) OR [department] IS NULL)) " +
            "ORDER BY CASE WHEN [age] IS NULL THEN 1 ELSE 0 END DESC, [age] DESC, " +
            "CASE WHEN [id] IS NULL THEN 1 ELSE 0 END ASC, [id] ASC " +
            "OFFSET :p5 ROWS FETCH NEXT :p6 ROWS ONLY",
            probe.Sql);
    }

    [Fact]
    public void Even_The_Parameter_List_Differs_Because_Its_Shape_Is_A_Dialect_Decision()
    {
        var postgres = SqlQueryBuilder.Build(Request, PostgresDialect.Instance);
        var probe = SqlQueryBuilder.Build(Request, ProbeDialect.Instance);

        // SQL-4: one array parameter vs N scalars, and LIMIT/OFFSET vs OFFSET/FETCH's reversed
        // binding order. If the builder owned either decision, these two lists could not differ.
        Assert.Equal(5, postgres.Parameters.Count);
        Assert.Equal(["smith", "50\\%", new List<object?> { "sales", "support" }, 20, 20], postgres.Parameters);

        Assert.Equal(6, probe.Parameters.Count);
        Assert.Equal(["smith", "50!%", "sales", "support", 20, 20], probe.Parameters);
    }

    /// <summary>
    /// The paging split, pinned: <see cref="SqlQueryBuilder"/> resolves the row window (AG-3's
    /// <em>exclusive</em> <c>endRow</c> makes it <c>endRow - startRow</c> rows wide, and EXP-2's
    /// <c>NonePage</c> makes it no clause at all), and the dialect only spells and orders the two
    /// values it is handed. That subtraction is a semantic reading of the request contract, so a
    /// dialect that re-derived it and bound <c>endRow</c> instead would emit a perfectly plausible
    /// statement returning the wrong rows — silently, and four times over once #1639 lands.
    /// </summary>
    [Fact]
    public void The_Row_Window_Is_The_Builders_So_Every_Dialect_Inherits_AG3s_Exclusive_EndRow()
    {
        // Deliberately asymmetric: the shared request above pages 20..40, where offset and limit
        // coincide at 20 and a dialect binding endRow would still look right.
        var page = new RangePage { StartRow = 30, EndRow = 100 };

        var postgres = SqlQueryBuilder.Build(Request with { Filter = null, Page = page }, PostgresDialect.Instance);
        var probe = SqlQueryBuilder.Build(Request with { Filter = null, Page = page }, ProbeDialect.Instance);

        // The same two numbers in both, each in its own clause's order — spelling varies, the
        // arithmetic cannot.
        Assert.Equal([70, 30], postgres.Parameters);
        Assert.Equal([30, 70], probe.Parameters);
    }

    [Fact]
    public void The_Structural_Rules_Are_Identical_Across_Dialects_Because_They_Belong_To_The_Builder()
    {
        var postgres = SqlQueryBuilder.Build(Request, PostgresDialect.Instance);
        var probe = SqlQueryBuilder.Build(Request, ProbeDialect.Instance);

        // SQL-2's parenthesisation and SEM-2's `OR ... IS NULL` disjunct are the SAME in both, and
        // SEM-6's tiebreaker key appears in both — those are semantics/structure, not spelling, so a
        // dialect must not be able to vary them.
        foreach (var query in new[] { postgres, probe })
        {
            Assert.Contains(" OR ", query.Sql, StringComparison.Ordinal);
            Assert.Contains("IS NULL))", query.Sql, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(query.Sql, @"\bWHERE\b"));
        }

        Assert.Contains("\"id\" ASC", postgres.Sql, StringComparison.Ordinal);
        Assert.Contains("[id] ASC", probe.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// GRP-6's row cap is the one member BM-3 added to the seam, and this is the assertion that it
    /// really is a dialect's own. All three spellings here are trailing clauses over the same
    /// statement structure — <c>LIMIT $1</c>, and a <c>FETCH FIRST</c> form that is neither the real
    /// dialects' <c>LIMIT</c> nor SQL Server's literal-zero <c>OFFSET</c> — so a builder that spelled
    /// the cap itself would fail here rather than only against an artifact.
    ///
    /// <para>The <c>DISTINCT</c> keyword, by contrast, is identical in both: it is ANSI, so it stays
    /// the builder's and no dialect may vary it.</para>
    /// </summary>
    [Fact]
    public void Only_The_Row_Cap_Varies_In_A_Set_Values_Statement_Because_DISTINCT_Is_ANSI()
    {
        var request = new SqlSetValuesRequest { Schema = Schema, ColId = "department", RowLimit = 1000 };

        var postgres = SqlQueryBuilder.BuildSetValues(request, PostgresDialect.Instance);
        var probe = SqlQueryBuilder.BuildSetValues(request, ProbeDialect.Instance);

        Assert.Equal(
            "SELECT DISTINCT \"department\" FROM \"employees\" " +
            "ORDER BY \"department\" ASC NULLS LAST LIMIT $1",
            postgres.Sql);

        Assert.Equal(
            "SELECT DISTINCT [department] FROM [employees] " +
            "ORDER BY CASE WHEN [department] IS NULL THEN 1 ELSE 0 END ASC, [department] ASC " +
            "FETCH FIRST :p1 ROWS ONLY",
            probe.Sql);

        // Same single parameter either way: what the cap resolves to is SEC-3's decision, upstream of
        // every dialect, so only its spelling and position can differ.
        Assert.Equal([1000], postgres.Parameters);
        Assert.Equal([1000], probe.Parameters);
    }

    /// <summary>
    /// SQL-7, and the shape of the seam's third widening: <c>blank</c>'s composition stays in the
    /// builder and only <see cref="ISqlDialect.EmptyStringIsNull"/> varies, so the two spellings are
    /// two branches of ONE method rather than two dialects' opinions.
    ///
    /// <para><b>Why the collapsed form is a correctness fix, not a tidy-up.</b> Oracle documents the
    /// zero-length string as NULL, so <c>"c" = ''</c> is <c>"c" = NULL</c> — UNKNOWN for every row.
    /// <c>blank</c> survives that (<c>TRUE OR UNKNOWN</c> is TRUE and a <c>WHERE</c> keeps only
    /// TRUE), but <c>notBlank</c> becomes <c>NOT (FALSE OR UNKNOWN)</c> = <c>NOT UNKNOWN</c> =
    /// UNKNOWN for a row with a real value, so the uncollapsed statement returns the EMPTY result
    /// set. That is why this assertion pairs the two operators: getting only <c>blank</c> right is
    /// exactly the failure mode.</para>
    /// </summary>
    [Theory]
    // Every dialect whose '' is distinct from NULL keeps SEM-2's two disjuncts...
    [InlineData("postgres", "(\"last_name\" IS NULL OR \"last_name\" = '')")]
    [InlineData("mssql", "([last_name] IS NULL OR [last_name] = '')")]
    [InlineData("mysql", "(`last_name` IS NULL OR `last_name` = '')")]
    [InlineData("sqlite", "(\"last_name\" IS NULL OR \"last_name\" = '')")]
    [InlineData("probe", "([last_name] IS NULL OR [last_name] = '')")]
    // ...and Oracle, alone, collapses to the non-text arm.
    [InlineData("oracle", "\"last_name\" IS NULL")]
    public void Blank_Collapses_To_A_Bare_Null_Test_Only_Where_The_Empty_String_Is_Null(string dialect, string blank)
    {
        var target = DialectNamed(dialect);

        var request = Request with
        {
            Filter = null,
            Sort = null,
            Page = new NonePage()
        };

        var isBlank = SqlQueryBuilder.Build(
            request with { Filter = new ComparisonNode { Field = "lastName", Op = ComparisonOperator.Blank } },
            target).Sql;
        var isNotBlank = SqlQueryBuilder.Build(
            request with { Filter = new ComparisonNode { Field = "lastName", Op = ComparisonOperator.NotBlank } },
            target).Sql;

        Assert.Contains($"WHERE {blank} ", isBlank, StringComparison.Ordinal);

        // SEM-2's "negate the compiled blank condition, never re-derive it": notBlank is that exact
        // text inside a NOT, on every dialect and in both branches of the collapse.
        Assert.Contains($"WHERE NOT ({blank}) ", isNotBlank, StringComparison.Ordinal);
    }

    /// <summary>
    /// The non-text arm is untouched by SQL-7, because there is no <c>= ''</c> disjunct to collapse:
    /// an <c>int64</c> field's <c>blank</c> is <c>IS NULL</c> everywhere, Oracle included.
    ///
    /// <para><b>What this pins, precisely.</b> That SQL-7's collapse is <em>scoped to the text arm</em>
    /// — Oracle answers the non-text case identically to every other dialect, so the engine fact
    /// narrows one branch rather than replacing the field-type question. It does <b>not</b> pin that
    /// the implementation consults <c>SemanticsResolver.ResolveBlankCondition</c> at all: an
    /// <c>EmptyStringIsNull ? "IS NULL" : &lt;field-type switch&gt;</c> written the other way round
    /// passes this too, since both orderings agree on every input. The guard against a dialect-shaped
    /// branch is the source-level one below, not this.</para>
    /// </summary>
    [Theory]
    [InlineData("postgres")]
    [InlineData("oracle")]
    public void A_NonText_Fields_Blank_Is_A_Bare_Null_Test_On_Every_Dialect_Including_Oracle(string dialect)
    {
        var sql = SqlQueryBuilder.Build(
            Request with
            {
                Filter = new ComparisonNode { Field = "age", Op = ComparisonOperator.Blank },
                Sort = null,
                Page = new NonePage()
            },
            DialectNamed(dialect)).Sql;

        Assert.Contains("WHERE \"age\" IS NULL ", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="ISqlDialect"/>'s own doc says a fourth widening "should have to argue that the seam,
    /// and not the candidate dialect, is what is wrong". That was prose with nothing behind it, in a
    /// repo where every artifact count is an exact <c>Assert.Equal</c> — so this is the mechanical
    /// backing: the seam's membership is enumerated, and a tenth-plus member (or a rename) fails here
    /// before it reaches five implementations and every language port.
    ///
    /// <para>Deliberately not a bare count. A count says "ten" and a diff says nothing about which;
    /// naming them makes the failure message the argument itself, and keeps the
    /// two-properties-versus-eight-methods split — nine renderings and one engine fact — visible as
    /// the thing being asserted rather than an accident of arithmetic. Failing this test is not a
    /// reason to update the list: it is the prompt to write down why the seam, and not the new
    /// dialect, is what needed to change.</para>
    /// <para>GRP-6(f) widened the property list to three: SQL Server cannot ORDER a DISTINCT by its
    /// nulls-last CASE key, so <c>DistinctValuesUseGroupBy</c> is the one spelling fact the builder reads.</para>
    /// </summary>
    [Fact]
    public void The_Seam_Is_Exactly_These_Ten_Members_So_A_Fourth_Widening_Cannot_Land_Silently()
    {
        Assert.Equal(
            ["DistinctValuesUseGroupBy", "EmptyStringIsNull", "Name"],
            typeof(ISqlDialect).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        Assert.Equal(
            [
                "ParameterPlaceholder",
                "QuoteIdentifier",
                "RenderPaging",
                "RenderRowLimit",
                "RenderScalarComparison",
                "RenderSetMembership",
                "RenderSortKey",
                "RenderSubstringMatch"
            ],
            typeof(ISqlDialect).GetMethods()
                .Where(method => !method.IsSpecialName) // property accessors are methods too
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    private static ISqlDialect DialectNamed(string name) => name switch
    {
        "postgres" => PostgresDialect.Instance,
        "mssql" => MssqlDialect.Instance,
        "mysql" => MySqlDialect.Instance,
        "oracle" => OracleDialect.Instance,
        "sqlite" => SqliteDialect.Instance,
        "probe" => ProbeDialect.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown dialect.")
    };

    /// <summary>
    /// The other half of the guard: even a builder that happens to produce the right answer today
    /// would violate BM-1 if it did so via a dialect-specific branch, and behavioral tests cannot see
    /// the difference between "delegated correctly" and "guessed correctly for these two dialects".
    /// So this reads the builder's own source and asserts no dialect vocabulary appears in its
    /// executable text at all.
    ///
    /// <para>Comments are stripped first, deliberately: the file explains SEM-2 by quoting
    /// <c>NOT (col ILIKE ...)</c>, and naming a construct in prose is the opposite of hard-coding
    /// it. (The stripper handles <c>//</c> and <c>///</c> lines, which is every comment in the file;
    /// a future block comment would slip through, which is an acceptable limit for a guard whose
    /// real teeth are the assertion above.)</para>
    ///
    /// <para><b>The five dialect NAMES are on the list as of #1642, and that closes a hole this guard
    /// had from the start.</b> Every other token here is a SQL spelling, so the list could only ever
    /// catch a builder that emitted engine-specific text. It was blind to the other shape of the same
    /// violation — branching on <em>which</em> engine — because
    /// <c>if (context.Dialect.Name == "oracle")</c> introduces none of them, passes all 168 non-Postgres
    /// dialect arms,
    /// and is exactly the BM-1 smell. That branch was unreachable in practice until this story: SQL-7
    /// is the first genuinely per-engine <em>behaviour</em> within the builder's reach, and
    /// <see cref="ISqlDialect.EmptyStringIsNull"/> is the shape that keeps it out. Note the ban is on
    /// the name <em>literals</em>, not on <c>.Name</c> — the builder legitimately reads that property
    /// to stamp <c>SqlQuery.Dialect</c>, which is reporting the answer rather than branching on it.</para>
    /// </summary>
    [Fact]
    public void No_Dialect_Specific_Token_Appears_In_The_Builders_Executable_Source()
    {
        var path = Path.Combine(RepoPaths.Root, "csharp", "src", "Craticula.Sql", "SqlQueryBuilder.cs");
        Assert.True(File.Exists(path), $"Expected the builder's source at '{path}'.");

        var code = string.Join(
            '\n',
            File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        string[] forbidden =
        [
            "Postgres", "craticula", // the concrete dialect and its collation name
            "COLLATE", "ILIKE", "LIKE", // SEM-1/SEM-7 mechanism spellings
            "LIMIT", "OFFSET", "FETCH", // paging clauses
            "NULLS", // SEM-3 null-placement syntax
            "ANY(", "ALL(", " IN (", // SQL-4 in-list spellings
            "$1", "@p", ":p", "?)", // parameter placeholder styles
            "\"\\\"\"", "'['", "'`'", // identifier quoting characters

            // ISqlDialect.Name's five values: a builder that branches on WHICH engine answered
            // violates BM-1 just as surely as one that spells the engine's SQL, and introduces none
            // of the tokens above while doing it.
            "\"postgres\"", "\"mssql\"", "\"mysql\"", "\"oracle\"", "\"sqlite\""
        ];

        var leaked = forbidden.Where(token => code.Contains(token, StringComparison.Ordinal)).ToList();

        Assert.True(
            leaked.Count == 0,
            $"SqlQueryBuilder.cs contains dialect-specific token(s) [{string.Join(", ", leaked)}]. " +
            "That is the BM-1 smell docs/architecture/c4-components-sql-compiler.md warns about — " +
            "add a method to ISqlDialect instead of branching in the builder.");
    }
}
