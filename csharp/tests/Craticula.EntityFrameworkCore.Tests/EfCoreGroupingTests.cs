using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Microsoft.EntityFrameworkCore;

namespace Craticula.EntityFrameworkCore.Tests;

/// <summary>
/// #1887: GRP-1's grouped level and drill-down leaf, compiled through the EF Core path end to end
/// against a real SQLite provider (mirroring <see cref="EfCoreExecutionEqualityTests"/>'s own
/// "compile, seed, execute" shape) — plus the traps a hand-built request reaches that no fixture
/// happens to exercise on this path yet: AGG-1's non-null count, AGG-6's empty-group answer, GRP-2's
/// exact drill-down match, and the shared guards <see cref="GroupingPlan"/>/<see cref="GroupedSortTarget"/>
/// already enforce for the SQL and Mongo compilers.
///
/// <para>Everything the fixture suite DOES pin (the grouped projection shape, the drill-down
/// predicates, the aggregate names) belongs to a fixture-driven equality test once a grouped fixture
/// reaches this path; this file is deliberately the hand-built shapes that let one rule be pinned in
/// isolation, the same division <c>SqlQueryBuilderGroupingTests</c>/<c>MongoPipelineGroupingTests</c>
/// draw for their own compilers.</para>
/// </summary>
public class EfCoreGroupingTests
{
    private static readonly EfCoreProviderSemantics Sqlite = EfCoreProviderSemantics.Sqlite;

    private static FieldConfig Field(FieldType type) => new() { Type = type };

    private static readonly GridSchema Schema = new()
    {
        Source = "sales",
        StableKey = "id",
        FieldOrder = ["id", "region", "segment", "amount", "notes"],
        Fields = new Dictionary<string, FieldConfig>(StringComparer.Ordinal)
        {
            ["id"] = Field(FieldType.Int64),
            ["region"] = Field(FieldType.String),
            ["segment"] = Field(FieldType.String),
            ["amount"] = Field(FieldType.Decimal),
            ["notes"] = Field(FieldType.String)
        }
    };

    private static Dictionary<string, object?> Row(params (string Field, object? Value)[] cells)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in cells)
        {
            row[field] = value;
        }

        return row;
    }

    private static readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> SalesRows =
    [
        Row(("id", 1L), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", 100m), ("notes", "a")),
        Row(("id", 2L), ("region", "EMEA"), ("segment", "SMB"), ("amount", 200m), ("notes", null)),
        Row(("id", 3L), ("region", "APAC"), ("segment", "Enterprise"), ("amount", 50m), ("notes", "b"))
    ];

    private static GroupSpec Group(string[] cols, params string[] keys) => new()
    {
        GroupCols = cols,
        GroupKeys = [.. keys.Select(key => (TypedValue)new StringValue { Value = key })]
    };

    private static Aggregation Agg(string field, AggregationFunc func, string? alias = null) =>
        new() { Field = field, Func = func, Alias = alias };

    private static EfCoreQueryRequest Request(
        GroupSpec? group = null,
        IReadOnlyList<Aggregation>? aggregations = null,
        FilterNode? filter = null,
        IReadOnlyList<SortItem>? sort = null,
        GridSchema? schema = null) => new()
        {
            Schema = schema ?? Schema,
            Filter = filter,
            Sort = sort,
            Page = new RangePage { StartRow = 0, EndRow = 100 },
            Group = group,
            Aggregations = aggregations
        };

    private static async Task<EfCoreGridPage> Fetch(
        GridSchema schema, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, EfCoreQueryRequest request)
    {
        using var database = SqliteFixtureDatabase.Create(schema, rows);
        return await EfCoreGridQuery.FetchPageAsync(database.Rows(), request, Sqlite);
    }

    private static int ColumnIndex(string field) => Schema.FieldOrder.ToList().IndexOf(field);

    // ---- grouped level: end to end -------------------------------------------------------------------

    /// <summary>
    /// GRP-1's grouped level, compiled through <see cref="EfCoreQueryBuilder.ApplyGroupedLevel{TRow}"/>'s
    /// <c>GroupBy</c> + reflection-resolved aggregate selectors: two regions, one aggregate, and GRP-4's
    /// default ordering (ascending on the group column itself, since no explicit sort was asked for).
    /// </summary>
    [Fact]
    public async Task A_Grouped_Level_Buckets_By_The_Group_Column_And_Projects_Its_Aggregates()
    {
        var page = await Fetch(Schema, SalesRows, Request(Group(["region"]), [Agg("amount", AggregationFunc.Sum)]));

        Assert.Equal(["region", "amount_sum"], page.Columns);
        Assert.Equal(2, page.Rows.Count);

        Assert.Equal(["APAC", "EMEA"], page.Rows.Select(row => (string?)row[0]));
        Assert.Equal([50m, 300m], page.Rows.Select(row => (decimal?)row[1]));
    }

    [Fact]
    public async Task A_Group_With_No_Aggregations_Projects_The_Bucket_Column_Alone()
    {
        var page = await Fetch(Schema, SalesRows, Request(Group(["region"])));

        Assert.Equal(["region"], page.Columns);
        Assert.Equal(["APAC", "EMEA"], page.Rows.Select(row => (string?)row[0]));
        Assert.All(page.Rows, row => Assert.Single(row));
    }

    /// <summary>AR-9/GRP-4: an explicit sort on an ALIASED aggregate (matched by
    /// <see cref="Aggregation.ColumnId"/>, which an alias makes equal to <see cref="Aggregation.OutputName"/>)
    /// orders the buckets by it — desc here, so the higher-revenue region leads.</summary>
    [Fact]
    public async Task An_Explicit_Sort_On_An_Aliased_Aggregate_Orders_The_Buckets_By_It()
    {
        var page = await Fetch(
            Schema,
            SalesRows,
            Request(
                Group(["region"]),
                [Agg("amount", AggregationFunc.Sum, "totalAmount")],
                sort: [new SortItem { Field = "totalAmount", Direction = SortDirection.Desc, NullsPosition = NullsPosition.First }]));

        Assert.Equal(["EMEA", "APAC"], page.Rows.Select(row => (string?)row[0]));
    }

    // ---- AGG-1's count trap ---------------------------------------------------------------------------

    /// <summary>
    /// AGG-1: <c>count</c> aggregates non-null occurrences of the NAMED field, never a bare row count.
    /// EMEA's bucket has two rows and one null <c>notes</c>, so the correct answer is 1 — a
    /// <c>g.Count()</c> row count would silently answer 2 instead, on a syntactically identical query.
    /// </summary>
    [Fact]
    public async Task Count_Excludes_Nulls_Rather_Than_Counting_Every_Row_In_The_Bucket()
    {
        var page = await Fetch(Schema, SalesRows, Request(Group(["region"]), [Agg("notes", AggregationFunc.Count)]));

        var emea = page.Rows.Single(row => Equals(row[0], "EMEA"));
        Assert.Equal(1, Convert.ToInt32(emea[1]));

        var apac = page.Rows.Single(row => Equals(row[0], "APAC"));
        Assert.Equal(1, Convert.ToInt32(apac[1]));
    }

    // ---- AGG-6: sum -> 0, avg/min/max -> null on an all-null group -------------------------------------

    /// <summary>
    /// #1886's rule, verified empirically for THIS shape rather than trusted from the ticket's own
    /// "measured" claim: <see cref="EfCoreQueryBuilder.BuildAggregateExpression{TRow}"/>'s own remarks
    /// say EF's translation of the nullable <c>Sum</c>/<c>Average</c>/<c>Min</c>/<c>Max</c> overloads
    /// already answers AGG-6 with no code of this adapter's own — this test is what makes that a
    /// measured fact about SQLite's query translation rather than an assumption carried over from
    /// <c>Craticula.Sql</c>'s <c>COALESCE</c> wrap, which this backend does not need and does not have.
    /// </summary>
    [Fact]
    public async Task An_All_Null_Group_Sums_To_Zero_And_Averages_Mins_And_Maxes_To_Null()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            Row(("id", 1L), ("region", "EMPTY"), ("segment", "x"), ("amount", null), ("notes", null)),
            Row(("id", 2L), ("region", "EMPTY"), ("segment", "y"), ("amount", null), ("notes", null)),
            Row(("id", 3L), ("region", "REAL"), ("segment", "x"), ("amount", 10m), ("notes", null)),
            Row(("id", 4L), ("region", "REAL"), ("segment", "y"), ("amount", 20m), ("notes", null))
        };

        var page = await Fetch(
            Schema,
            rows,
            Request(
                Group(["region"]),
                [
                    Agg("amount", AggregationFunc.Sum, "s"),
                    Agg("amount", AggregationFunc.Avg, "a"),
                    Agg("amount", AggregationFunc.Min, "mn"),
                    Agg("amount", AggregationFunc.Max, "mx")
                ]));

        Assert.Equal(["region", "s", "a", "mn", "mx"], page.Columns);

        var empty = page.Rows.Single(row => Equals(row[0], "EMPTY"));
        Assert.Equal(0m, (decimal?)empty[1]);
        Assert.Null(empty[2]);
        Assert.Null(empty[3]);
        Assert.Null(empty[4]);

        var real = page.Rows.Single(row => Equals(row[0], "REAL"));
        Assert.Equal(30m, (decimal?)real[1]);
        Assert.Equal(15m, (decimal?)real[2]);
        Assert.Equal(10m, (decimal?)real[3]);
        Assert.Equal(20m, (decimal?)real[4]);
    }

    // ---- drill-down leaf -------------------------------------------------------------------------------

    /// <summary>
    /// GRP-1's drill-down leaf (<c>groupKeys.Count == groupCols.Count</c>): raw rows restricted by the
    /// already-consumed key, GRP-3's full-field projection under logical names, and the ordinary
    /// SEM-6/Ungrouped tiebreaker (the leaf has no <c>GROUP BY</c>, so nothing about bucket identity is
    /// at stake in its own ordering — GRP-2's narrowed scope).
    /// </summary>
    [Fact]
    public async Task A_Drill_Down_Leaf_Restricts_To_The_Consumed_Key_And_Projects_Every_Field()
    {
        var page = await Fetch(Schema, SalesRows, Request(Group(["region"], "EMEA")));

        Assert.Equal(Schema.FieldOrder, page.Columns);
        Assert.Equal(2, page.Rows.Count);
        Assert.All(page.Rows, row => Assert.Equal("EMEA", row[ColumnIndex("region")]));

        // No GROUP BY at this level: the appended tiebreaker alone orders it, ascending on the id.
        Assert.Equal([1L, 2L], page.Rows.Select(row => Convert.ToInt64(row[ColumnIndex("id")])));
    }

    /// <summary>GRP-5: the drill-down predicate AND-composed with the request's own filter — here, only
    /// one of the leaf's two EMEA rows also satisfies <c>amount &gt; 150</c>.</summary>
    [Fact]
    public async Task A_Drill_Down_Leaf_Composes_With_The_Users_Own_Filter()
    {
        var page = await Fetch(
            Schema,
            SalesRows,
            Request(
                Group(["region"], "EMEA"),
                filter: new ComparisonNode { Field = "amount", Op = ComparisonOperator.Gt, Value = new DecimalValue { Value = "150" } }));

        var row = Assert.Single(page.Rows);
        Assert.Equal(2L, Convert.ToInt64(row[ColumnIndex("id")]));
    }

    // ---- GRP-2: the drill-down match is exact, never folding case -------------------------------------

    /// <summary>
    /// GRP-2's central claim, on the one adapter left to inherit it: the synthesised drill-down match
    /// resolves through <see cref="SemanticsResolver.ResolveOrderingAndGroupKeyCaseSensitivity"/> rather
    /// than SEM-1, so <c>"region" = 'EMEA'</c> inside the drill-down is byte-exact even though
    /// <c>region</c> is an ordinary case-insensitive string everywhere else. An ordinary <c>eq</c> FILTER
    /// on the identical column, over the identical data, folds case and matches both rows — the same
    /// contrast <c>SqlQueryBuilderGroupingTests</c> draws for the SQL path.
    /// </summary>
    [Fact]
    public async Task The_Drill_Down_Match_Is_Exact_While_An_Ordinary_Filter_On_The_Same_Field_Collates()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            Row(("id", 1L), ("region", "EMEA"), ("segment", "x"), ("amount", 1m), ("notes", null)),
            Row(("id", 2L), ("region", "emea"), ("segment", "y"), ("amount", 2m), ("notes", null))
        };

        var leaf = await Fetch(Schema, rows, Request(Group(["region"], "EMEA")));

        var leafRow = Assert.Single(leaf.Rows);
        Assert.Equal(1L, Convert.ToInt64(leafRow[ColumnIndex("id")]));

        using var database = SqliteFixtureDatabase.Create(Schema, rows);
        var ordinaryFilter = EfCoreQueryBuilder.ApplyFilter(
            database.Rows(),
            Request(filter: new ComparisonNode { Field = "region", Op = ComparisonOperator.Eq, Value = new StringValue { Value = "EMEA" } }),
            Sqlite);

        Assert.Equal(2, await ordinaryFilter.CountAsync());
    }

    // ---- shared guards, reached through the EF path ----------------------------------------------------

    /// <summary>
    /// #1886's fix, reached through a fourth backend: <c>sum(amount)</c> and <c>avg(amount)</c>,
    /// unaliased, resolve to distinct output names (<c>amount_sum</c>/<c>amount_avg</c>) while sharing
    /// one <see cref="Aggregation.ColumnId"/> (<c>amount</c>) — a sort naming that shared id is rejected
    /// by the shared <see cref="GroupedSortTarget"/>, not silently resolved to whichever the request
    /// listed first.
    /// </summary>
    [Fact]
    public async Task Sorting_By_A_Column_Id_Shared_By_Two_Unaliased_Aggregations_Is_Rejected_Not_Guessed()
    {
        var exception = await Assert.ThrowsAsync<EfCoreCompilationException>(() => Fetch(
            Schema,
            SalesRows,
            Request(
                Group(["region"]),
                [Agg("amount", AggregationFunc.Sum), Agg("amount", AggregationFunc.Avg)],
                sort: [new SortItem { Field = "amount", Direction = SortDirection.Desc, NullsPosition = NullsPosition.First }])));

        Assert.Contains("amount", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Sum", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Avg", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>The #1886 review's second finding, reached the identical way: grouping by field X and
    /// aggregating X unaliased makes the aggregation's column id equal X too — reachable on a request
    /// with NO explicit sort at all, since SEM-6's own appended tiebreaker is minted from the group
    /// column and would otherwise silently resolve to the aggregate instead.</summary>
    [Fact]
    public async Task Grouping_By_A_Field_Also_Aggregated_Unaliased_Over_Itself_Is_Rejected_Not_Guessed()
    {
        var exception = await Assert.ThrowsAsync<EfCoreCompilationException>(() => Fetch(
            Schema, SalesRows, Request(Group(["amount"]), [Agg("amount", AggregationFunc.Sum)])));

        Assert.Contains("amount", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Step 7's relocation, confirmed reachable through THIS backend too: the guard now lives
    /// in <c>Craticula.Core.Ir.GroupingPlan.Resolve</c> (see <c>GroupingPlanTests</c> for the primary
    /// coverage), and every compiler/adapter — including this one — inherits it because
    /// <see cref="EfCoreGridQuery.FetchPageAsync{TRow}"/> calls it before building anything.</summary>
    [Fact]
    public async Task An_Aggregation_Aliased_To_Its_Own_Group_Columns_Name_Is_Rejected_Through_The_EF_Path()
    {
        await Assert.ThrowsAsync<AggregationValidationException>(() => Fetch(
            Schema, SalesRows, Request(Group(["region"]), [Agg("amount", AggregationFunc.Sum, "region")])));
    }

    /// <summary>GRP-1's arity rule (<see cref="GroupSpecValidator"/>) is inherited unchanged: the new
    /// grouped orchestration validates through <see cref="EfCoreQueryBuilder.ValidateAndBuildRowContext"/>
    /// exactly as the refused per-clause entry points always did.</summary>
    [Fact]
    public async Task More_Group_Keys_Than_Group_Columns_Is_Rejected_Through_The_EF_Path()
    {
        await Assert.ThrowsAsync<GroupSpecValidationException>(() => Fetch(
            Schema, SalesRows, Request(Group(["region"], "EMEA", "Enterprise"))));
    }

    /// <summary>Mirrors <c>Craticula.Sql.SqlQueryBuilder.Build</c>'s identical guard: EXP-1's
    /// group-export carve-out means the two never arrive together from the adapter, but a hand-built
    /// request can still set both — there is no defined shape for "project these columns" over a
    /// grouped level, and <see cref="EfCoreGridQuery.FetchPageAsync{TRow}"/> must refuse it rather than
    /// silently pick one (the leaf branch would have applied it, the grouped branch would have
    /// discarded it — an AR-4 divergence from the SQL path either way).</summary>
    [Fact]
    public async Task A_Request_Carrying_Both_A_GroupSpec_And_An_Export_Projection_Is_Rejected()
    {
        var request = Request(Group(["region"])) with { Projection = ["region", "amount"] };

        var exception = await Assert.ThrowsAsync<EfCoreCompilationException>(() => Fetch(Schema, SalesRows, request));
        Assert.Contains("GroupSpec", exception.Message, StringComparison.Ordinal);
        Assert.Contains("export projection", exception.Message, StringComparison.Ordinal);
    }
}
