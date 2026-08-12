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
            "\"\\\"\"", "'['", "'`'" // identifier quoting characters
        ];

        var leaked = forbidden.Where(token => code.Contains(token, StringComparison.Ordinal)).ToList();

        Assert.True(
            leaked.Count == 0,
            $"SqlQueryBuilder.cs contains dialect-specific token(s) [{string.Join(", ", leaked)}]. " +
            "That is the BM-1 smell docs/architecture/c4-components-sql-compiler.md warns about — " +
            "add a method to ISqlDialect instead of branching in the builder.");
    }
}
