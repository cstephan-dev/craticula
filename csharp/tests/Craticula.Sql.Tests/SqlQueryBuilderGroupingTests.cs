using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Sql.Tests;

/// <summary>
/// BM-2's grouping paths, hand-built for the shapes <c>spec/fixtures/group-aggregation/</c> does not
/// reach — most importantly a drill-down and a user filter in the SAME request, which no fixture
/// carries (every grouped case has either <c>groupKeys</c> or a <c>filterModel</c>, never both) and
/// which is the one place GRP-2's carve-out and SEM-1 have to coexist inside one statement.
///
/// <para>Everything the fixtures DO pin — the projection, <c>GROUP BY</c>, the aggregate names, the
/// drill-down predicates and the grouped tiebreaker — is asserted against the artifacts by
/// <see cref="PostgresConformanceFixtureTests"/> and <see cref="DialectConformanceFixtureTests"/>,
/// never re-copied here. This file is deliberately the leftovers.</para>
/// </summary>
public class SqlQueryBuilderGroupingTests
{
    private static FieldConfig Field(FieldType type, string column, CaseSensitivity? caseSensitivity = null) => new()
    {
        Type = type,
        Physical = new PhysicalMapping { Sql = new SqlMapping { Column = column } },
        Text = caseSensitivity is null ? null : new TextOptions { CaseSensitivity = caseSensitivity.Value }
    };

    private static readonly GridSchema Schema = new()
    {
        Source = "sales",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = Field(FieldType.Int64, "id"),
            ["region"] = Field(FieldType.String, "region"),
            ["segment"] = Field(FieldType.String, "segment"),
            ["product"] = Field(FieldType.String, "product"),
            ["amount"] = Field(FieldType.Decimal, "amount")
        }
    };

    private static SqlQuery Compile(
        GroupSpec? group,
        IReadOnlyList<Aggregation>? aggregations = null,
        FilterNode? filter = null,
        IReadOnlyList<SortItem>? sort = null,
        ISqlDialect? dialect = null) =>
        SqlQueryBuilder.Build(
            new SqlQueryRequest
            {
                Schema = Schema,
                Filter = filter,
                Sort = sort,
                Page = new RangePage { StartRow = 0, EndRow = 50 },
                Group = group,
                Aggregations = aggregations
            },
            dialect ?? PostgresDialect.Instance);

    private static GroupSpec Group(string[] cols, params string[] keys) => new()
    {
        GroupCols = cols,
        GroupKeys = [.. keys.Select(key => new StringValue { Value = key })]
    };

    private static readonly Aggregation SumAmount = new() { Field = "amount", Func = AggregationFunc.Sum };

    /// <summary>
    /// The uncovered composition: a drill-down key AND a user filter. GRP-5 merges them through
    /// <c>FilterTreeMerge</c>, so SQL-2's wrap applies to the pair exactly as it would to a user's
    /// own <c>and</c>, and the drill-down leads.
    /// </summary>
    [Fact]
    public void A_Drill_Down_And_A_User_Filter_Merge_Into_One_Wrapped_Conjunction_Drill_Down_First()
    {
        var query = Compile(
            Group(["region", "segment"], "EMEA"),
            [SumAmount],
            filter: new ComparisonNode
            {
                Field = "amount",
                Op = ComparisonOperator.Gt,
                Value = new DecimalValue { Value = "1000" }
            });

        Assert.Contains(
            "WHERE (\"region\" = $1 AND \"amount\" > $2) GROUP BY \"segment\"",
            query.Sql,
            StringComparison.Ordinal);
        Assert.Equal(["EMEA", "1000", 50, 0], query.Parameters);
    }

    /// <summary>
    /// The shape nothing else reaches: TWO consumed keys AND a user filter. It is the only one where
    /// the drill-down is a composite that has to keep its own provenance while sitting beside a
    /// user predicate — so a compiler that told the two apart by walking the merged tree (reference
    /// identity, node shape, anything but the walk it started them under) would collate
    /// <c>"region"</c>/<c>"segment"</c> here and silently return a different bucket's rows, with no
    /// exception and no failing artifact. Both group-key matches must stay bare while the user's own
    /// <c>eq</c> on a case-insensitive column still collates.
    /// </summary>
    [Fact]
    public void Two_Consumed_Keys_Beside_A_User_Filter_Stay_Exact_While_The_User_Filter_Collates()
    {
        var query = Compile(
            Group(["region", "segment", "product"], "EMEA", "Enterprise"),
            [SumAmount],
            filter: new ComparisonNode
            {
                Field = "segment",
                Op = ComparisonOperator.Eq,
                Value = new StringValue { Value = "smb" }
            });

        Assert.Contains(
            "WHERE ((\"region\" = $1 AND \"segment\" = $2) AND \"segment\" COLLATE craticula_ci = $3) " +
            "GROUP BY \"product\"",
            query.Sql,
            StringComparison.Ordinal);
        Assert.Equal(["EMEA", "Enterprise", "smb", 50, 0], query.Parameters);
    }

    /// <summary>
    /// GRP-2 is scoped by PROVENANCE, and this is the assertion that says so: the same
    /// case-insensitive <c>string</c> column, in one statement, compared twice — once as a group-key
    /// match (exact, because it must agree with the <c>GROUP BY</c> that labelled the bucket) and
    /// once as the user's own filter (SEM-1's collation, because a pre-aggregation filter selects
    /// rows and has no say in bucket identity). A rule keyed on the FIELD instead would have to give
    /// both the same answer and could not produce this.
    /// </summary>
    [Fact]
    public void The_Group_Key_Match_Is_Exact_While_A_User_Filter_On_The_Same_Column_Still_Collates()
    {
        var query = Compile(
            Group(["region", "segment"], "EMEA"),
            [SumAmount],
            filter: new ComparisonNode
            {
                Field = "region",
                Op = ComparisonOperator.Contains,
                Value = new StringValue { Value = "em" }
            });

        Assert.Contains(
            "WHERE (\"region\" = $1 AND \"region\" ILIKE '%' || $2 || '%' ESCAPE '\\')",
            query.Sql,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The same split on the one dialect that spells BOTH case-sensitivity directions, where getting
    /// it wrong is visible rather than silent: SQL Server's stock collation folds case, so a bare
    /// group-key match there is a real (documented) divergence, while the user's filter still carries
    /// the explicit CI collation SEM-1 asked for.
    /// </summary>
    [Fact]
    public void On_Sql_Server_The_Group_Key_Match_Carries_No_Collation_And_The_User_Filter_Does()
    {
        var query = Compile(
            Group(["region", "segment"], "EMEA"),
            [SumAmount],
            filter: new ComparisonNode { Field = "segment", Op = ComparisonOperator.Eq, Value = new StringValue { Value = "SMB" } },
            dialect: MssqlDialect.Instance);

        Assert.Contains(
            "WHERE ([region] = @p1 AND [segment] COLLATE Latin1_General_CI_AS = @p2)",
            query.Sql,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// GRP-2 reaches the ORDER BY too, and this is the pair that proves the axis is grouped-versus-
    /// ungrouped rather than sort-versus-filter: the identical sort key on the identical field
    /// collates in one query and not in the other.
    /// </summary>
    [Fact]
    public void The_Same_Sort_Key_Collates_Ungrouped_And_Does_Not_Inside_The_Group_Protocol()
    {
        var sort = new[] { new SortItem { Field = "region", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last } };

        var ungrouped = Compile(group: null, sort: sort);
        var grouped = Compile(Group(["region"]), [SumAmount], sort: sort);

        Assert.Contains("ORDER BY \"region\" COLLATE craticula_ci ASC NULLS LAST", ungrouped.Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"region\" ASC NULLS LAST", grouped.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("COLLATE", grouped.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// GRP-2's narrowed scope, at the one shape where getting it wrong is a SEM-1 violation rather
    /// than a style choice: a drill-down LEAF that filters and sorts the SAME case-insensitive
    /// column. The leaf has no <c>GROUP BY</c> and no buckets, so its <c>ORDER BY</c> decides nothing
    /// about bucket identity and SEM-1 governs it — while the drill-down <c>WHERE</c>, which does
    /// reproduce the bucket, stays exact. One statement, three predicates, each with the mechanism
    /// its own role calls for; leaving the <c>ORDER BY</c> bare would spell two mechanisms for
    /// <c>segment</c> in one statement, which SEM-1's surviving Must-NOT forbids outright.
    /// </summary>
    [Fact]
    public void A_Drill_Down_Leaf_That_Filters_And_Sorts_One_Field_Collates_Both_While_The_Group_Key_Stays_Exact()
    {
        var query = Compile(
            Group(["region"], "EMEA"),
            filter: new ComparisonNode
            {
                Field = "segment",
                Op = ComparisonOperator.Eq,
                Value = new StringValue { Value = "smb" }
            },
            sort: [new SortItem { Field = "segment", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }]);

        Assert.Contains(
            "WHERE (\"region\" = $1 AND \"segment\" COLLATE craticula_ci = $2) " +
            "ORDER BY \"segment\" COLLATE craticula_ci ASC NULLS LAST, \"id\" ASC NULLS LAST",
            query.Sql,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// ...and the grouped level immediately above it does the opposite on the identical column: its
    /// <c>ORDER BY</c> orders the buckets its own <c>GROUP BY</c> produced, so GRP-2 keeps it exact.
    /// This pair is the whole of the narrowing — same field, same direction, two levels of one
    /// protocol, two answers.
    /// </summary>
    [Fact]
    public void The_Grouped_Level_Above_That_Leaf_Still_Orders_Its_Buckets_Exactly()
    {
        var query = Compile(
            Group(["segment", "product"]),
            [SumAmount],
            sort: [new SortItem { Field = "segment", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }]);

        Assert.Contains("GROUP BY \"segment\" ORDER BY \"segment\" ASC NULLS LAST", query.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("COLLATE", query.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// GRP-3: the projection is the ONE thing that distinguishes a drill-down leaf from an ordinary
    /// leaf, and the request looks nearly identical otherwise — same rows, same filter shape. A
    /// grouping request names its columns at every level; an ungrouped one keeps #1638's
    /// <c>SELECT *</c>, which the export feature owns.
    /// </summary>
    [Fact]
    public void The_Drill_Down_Leaf_Projects_Named_Columns_While_An_Ungrouped_Query_Selects_Star()
    {
        var leaf = Compile(Group(["region"], "EMEA"), [SumAmount]);
        var ungrouped = Compile(group: null);

        Assert.StartsWith(
            "SELECT \"id\", \"region\", \"segment\", \"product\", \"amount\" FROM \"sales\" WHERE \"region\" = $1 ORDER BY \"id\" ASC NULLS LAST",
            leaf.Sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP BY", leaf.Sql, StringComparison.Ordinal);

        Assert.StartsWith("SELECT * FROM \"sales\"", ungrouped.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// GRP-4: SEM-6's stable key is not in the grouped projection, so ordering by it would be invalid
    /// SQL — the group column takes its place, and does the same job because its values are unique
    /// across the result rows by construction.
    /// </summary>
    [Fact]
    public void A_Grouped_Level_Breaks_Ties_On_Its_Group_Column_Not_On_The_Schemas_Stable_Key()
    {
        var query = Compile(Group(["region"]), [SumAmount]);

        Assert.EndsWith("ORDER BY \"region\" ASC NULLS LAST LIMIT $1 OFFSET $2", query.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\"", query.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// AR-9/GRP-4: a sort naming an aggregated column is ADDRESSED by
    /// <see cref="Aggregation.ColumnId"/> — AGG-2's <c>ColumnVO.id</c> reconstructed, and since #1885
    /// a member of the IR type that owns the rule — so an aggregation the adapter DID alias is found by that
    /// alias and not by its field. What it then ORDERS BY is the aggregate expression, not the alias:
    /// an output name is not usable inside an expression on PostgreSQL or T-SQL, and SEM-3's null
    /// placement is emulated with exactly such an expression on two of the five dialects.
    /// </summary>
    [Fact]
    public void A_Sort_On_An_Aliased_Aggregate_Is_Addressed_By_Its_Alias_And_Orders_By_The_Expression()
    {
        var query = Compile(
            Group(["region"]),
            [new Aggregation { Field = "amount", Func = AggregationFunc.Sum, Alias = "totalRevenue" }],
            sort: [new SortItem { Field = "totalRevenue", Direction = SortDirection.Desc, NullsPosition = NullsPosition.First }]);

        // AGG-6's wrap reaches BOTH occurrences, because AggregateCall composes the expression once.
        Assert.Contains(
            "COALESCE(SUM(\"amount\"), 0) AS \"totalRevenue\" FROM \"sales\" GROUP BY \"region\" " +
            "ORDER BY COALESCE(SUM(\"amount\"), 0) DESC NULLS FIRST, \"region\" ASC NULLS LAST",
            query.Sql,
            StringComparison.Ordinal);

        // The alias survives in the projection (the grid reads the response by it) and appears
        // nowhere in the ORDER BY.
        Assert.DoesNotContain("ORDER BY \"totalRevenue\"", query.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The dialect where repeating the expression is a correctness requirement rather than a
    /// uniformity choice: T-SQL emulates <c>NULLS FIRST</c> with a <c>CASE</c>, and a select-list
    /// alias inside a <c>CASE</c> resolves against the <c>FROM</c> scope — <c>[amount_sum]</c> is not
    /// a column of <c>sales</c>, so the statement would fail with "Invalid column name".
    ///
    /// <para>Since AGG-6 the emulated bucket wraps an expression that can no longer be NULL, so this
    /// key is inert on a <c>sum</c> — kept, and asserted, because SEM-6 licenses no omission of a null
    /// marker on a nullability inference, and because the same emulation is fully live one function
    /// over (see <c>dialect-parity/024</c>, which orders by an <c>AVG</c>).</para>
    /// </summary>
    [Fact]
    public void On_Sql_Server_The_Emulated_Null_Bucket_Wraps_The_Aggregate_Expression_Not_Its_Alias()
    {
        var query = Compile(
            Group(["region"]),
            [SumAmount],
            sort: [new SortItem { Field = "amount", Direction = SortDirection.Desc, NullsPosition = NullsPosition.First }],
            dialect: MssqlDialect.Instance);

        Assert.Contains(
            "ORDER BY CASE WHEN COALESCE(SUM([amount]), 0) IS NULL THEN 1 ELSE 0 END DESC, " +
            "COALESCE(SUM([amount]), 0) DESC",
            query.Sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[amount_sum] IS NULL", query.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// ...and its field is then NOT a legal sort target, because the grouped projection does not
    /// contain the raw column. A statement referencing it would be rejected by the engine, so the
    /// compiler refuses to emit one rather than producing a plausible-looking artifact.
    /// </summary>
    [Fact]
    public void A_Grouped_Sort_On_Something_Outside_The_Projection_Is_Rejected()
    {
        var exception = Assert.Throws<SqlCompilationException>(() => Compile(
            Group(["region"]),
            [SumAmount],
            sort: [new SortItem { Field = "segment", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }]));

        Assert.Contains("segment", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// More keys than levels is not a drill-down at all — it is a malformed request, and consuming
    /// only as many as fit would silently return a DIFFERENT bucket's rows.
    ///
    /// <para>GRP-1 states this of the <em>request</em>, so it is <see cref="GroupSpecValidator"/> that
    /// rejects it, not this compiler — otherwise the Mongo and Search compilers would each have to
    /// re-derive a rule <c>ir.schema.json</c> cannot express (JSON Schema cannot compare two arrays'
    /// lengths). Asserted here anyway, from the SQL entry point, because what must hold is that
    /// <c>Build</c> refuses the request — wherever the refusal is implemented.</para>
    /// </summary>
    [Fact]
    public void More_Group_Keys_Than_Group_Columns_Is_Rejected()
    {
        var exception = Assert.Throws<GroupSpecValidationException>(() =>
            Compile(Group(["region"], "EMEA", "Enterprise"), [SumAmount]));

        Assert.Contains("groupSpec.groupKeys", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A grouped level with no <c>valueCols</c> at all is legal: AG Grid sends one whenever the user
    /// groups without adding an aggregation, and the answer is the bucket list.
    /// </summary>
    [Fact]
    public void A_Group_With_No_Aggregations_Projects_The_Bucket_Column_Alone()
    {
        var query = Compile(Group(["region"]), aggregations: null);

        Assert.StartsWith("SELECT \"region\" AS \"region\" FROM \"sales\" GROUP BY \"region\"", query.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The regression this whole story could most easily cause: grouping is dispatched on
    /// <see cref="SqlQueryRequest.Group"/>, and a request without one must compile exactly as it did
    /// before BM-2 existed — collation and all.
    /// </summary>
    [Fact]
    public void An_Ungrouped_Request_Compiles_Exactly_As_It_Did_Before_Grouping_Existed()
    {
        var query = Compile(
            group: null,
            filter: new ComparisonNode { Field = "region", Op = ComparisonOperator.Eq, Value = new StringValue { Value = "EMEA" } });

        Assert.Equal(
            "SELECT * FROM \"sales\" WHERE \"region\" COLLATE craticula_ci = $1 " +
            "ORDER BY \"id\" ASC NULLS LAST LIMIT $2 OFFSET $3",
            query.Sql);
    }
}
