using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Search.Tests;

/// <summary>
/// BM-2's grouped level on the paths no fixture reaches: the orderings and the two row windows a
/// <c>composite</c> aggregation cannot express, the name collisions and illegal names its
/// <c>aggs</c> object would otherwise carry, and AGG-5's divergence asserted as an intentional silence
/// rather than left as an absence.
/// </summary>
public class SearchQueryGroupingTests
{
    private static GridSchema Sales { get; } = SearchTestSchema.Of(
        ("id", SearchTestSchema.Id),
        ("region", SearchTestSchema.Field(FieldType.String, "region", keyword: "region.keyword")),
        ("amount", SearchTestSchema.Field(
            FieldType.Decimal, "amount",
            aggregatable: [AggregationFunc.Sum, AggregationFunc.Avg, AggregationFunc.Count])));

    private static SearchQuery Compile(
        IReadOnlyList<Aggregation> aggregations,
        IReadOnlyList<SortItem>? sort = null,
        PageSpec? page = null) =>
        SearchQueryBuilder.Build(new SearchQueryRequest
        {
            Schema = Sales,
            Page = page ?? SearchTestSchema.FirstPage,
            Group = new GroupSpec { GroupCols = ["region"], GroupKeys = [] },
            Sort = sort,
            Aggregations = aggregations
        });

    private static Aggregation Sum { get; } = new() { Field = "amount", Func = AggregationFunc.Sum };

    /// <summary>
    /// AGG-5's accepted divergence, asserted as a deliberate silence. The composite source carries
    /// <c>missing_bucket: true</c> so a null group key is not dropped, and carries <b>nothing at
    /// all</b> about where that bucket sorts — there is no <c>missing_order</c> in the pinned
    /// 7.10-era subset, so the null bucket lands first whatever direction was asked for, while
    /// Postgres honours <c>NULLS FIRST</c>/<c>NULLS LAST</c> and MongoDB emulates it.
    /// A compiler that "fixed" this by reordering client-side would defeat server-side paging, which
    /// is why AGG-5 records it instead.
    /// </summary>
    [Fact]
    public void The_Composite_Source_Keeps_Its_Missing_Bucket_And_Spells_No_Missing_Order()
    {
        var source = Compile([Sum]).Aggs!["region"]!["composite"]!["sources"]![0]!["region"]!["terms"]!.AsObject();

        Assert.True(source["missing_bucket"]!.GetValue<bool>());
        Assert.False(source.ContainsKey("missing_order"));
        Assert.False(source.ContainsKey("missing"));
    }

    /// <summary>
    /// GRP-4/AGG-4: the direction goes on the composite source when the whole ordering IS the group
    /// column, and is spelled only for <c>desc</c> — the source's documented default being ascending
    /// by key, which is also what SEM-6's appended group-column tiebreaker always asks for.
    /// </summary>
    [Fact]
    public void The_Source_Order_Is_Spelled_Only_When_The_Group_Column_Sorts_Descending()
    {
        var ascending = Compile([Sum]).Aggs!["region"]!["composite"]!["sources"]![0]!["region"]!["terms"]!.AsObject();
        Assert.False(ascending.ContainsKey("order"));

        var descending = Compile([Sum], [
            new SortItem { Field = "region", Direction = SortDirection.Desc, NullsPosition = NullsPosition.First }
        ]).Aggs!["region"]!["composite"]!["sources"]![0]!["region"]!["terms"]!.AsObject();

        Assert.Equal("desc", descending["order"]!.GetValue<string>());
    }

    /// <summary>
    /// AGG-1 needs no special construction on this backend: <c>value_count</c> already skips missing
    /// values, so <c>count</c> means "how many documents have a non-null value for this field"
    /// natively — unlike MongoDB, where AGG-1 has to forbid <c>{"$sum": 1}</c> by name.
    /// </summary>
    [Fact]
    public void Count_Compiles_To_Value_Count_Rather_Than_A_Document_Count()
    {
        var aggs = Compile([new Aggregation { Field = "amount", Func = AggregationFunc.Count }])
            .Aggs!["region"]!["aggs"]!.AsObject();

        Assert.Equal(
            """{"amount_count":{"value_count":{"field":"amount"}}}""",
            SearchFixtureCompiler.CanonicalJson(aggs));
    }

    /// <summary>
    /// OSE-11(d): a composite aggregation pages forward with an <c>after</c> key rather than an
    /// offset, so the second page of BUCKETS has no rendering. Emitting <c>size</c> and dropping the
    /// offset would answer every page with the first one — a plausible request answering a different
    /// question, silently. Refused in SQL-7(b)'s and MDB-9's register, and named as #1622's.
    ///
    /// <para>Note the window used here: <c>100..200</c>, AG Grid SSRM's <b>second default block</b>,
    /// not some deep offset. This limitation is reached by every grouped grid one screen of scrolling
    /// in, which is what distinguishes it from PERF-2's <c>search_after</c> gap (10 000 rows deep by
    /// default) that it shares an owner with.</para>
    /// </summary>
    [Fact]
    public void A_Grouped_Level_Asked_For_A_Second_Page_Of_Buckets_Is_Refused()
    {
        var exception = Assert.Throws<SearchCompilationException>(
            () => Compile([Sum], page: new RangePage { StartRow = 100, EndRow = 200 }));

        Assert.Contains("after", exception.Message);
        Assert.Contains("#1622", exception.Message);
    }

    [Fact]
    public void A_Grouped_Level_Asked_For_An_Unpaginated_Page_Is_Refused()
    {
        var exception = Assert.Throws<SearchCompilationException>(() => Compile([Sum], page: new NonePage()));

        Assert.Contains("unbounded", exception.Message);
    }

    /// <summary>
    /// MDB-10's third collision reached on a different backend by the identical mechanism: an
    /// aggregation container is a JSON object, so a repeated key is last-wins and the grid would read
    /// one column holding another column's number. AGG-2 takes the name from the client-supplied
    /// <c>valueCols[].id</c>, so it is reachable from the wire.
    ///
    /// <para><b>#1886 moved the refusal into <see cref="AggregationValidator"/></b>, which
    /// <see cref="SearchQueryBuilder.Build"/> runs on entry — because <c>Craticula.Sql</c> had no
    /// equivalent guard and the condition is a property of the request, not of this container's
    /// shape. The request is still refused; the exception type says by which layer.
    /// <c>RenderAggregations</c>' own <c>ContainsKey</c> guard is kept but is no longer reachable
    /// from any public entry point of this assembly.</para>
    /// </summary>
    [Fact]
    public void Two_Aggregations_Resolving_To_One_Output_Name_Are_Refused()
    {
        var exception = Assert.Throws<AggregationValidationException>(() => Compile([
            new Aggregation { Field = "amount", Func = AggregationFunc.Sum, Alias = "total" },
            new Aggregation { Field = "amount", Func = AggregationFunc.Avg, Alias = "total" }
        ]));

        Assert.Contains("'total'", exception.Message);
    }

    /// <summary>
    /// OSE-11(e): an aggregate's DEFAULT output name is held to the same
    /// <c>^[A-Za-z_][A-Za-z0-9_]*$</c> pattern <c>ir.schema.json</c> already imposes on an explicit
    /// <c>alias</c> — the gap being that <c>$defs/fieldName</c> has no pattern at all, so a nested
    /// colId composes a default name no validator has ever seen. Here <c>customer.name</c> would
    /// yield <c>customer.name_sum</c>, which both engines read as the aggregation <c>customer</c> and
    /// the metric <c>name_sum</c> when it reaches a <c>buckets_path</c>.
    /// </summary>
    [Fact]
    public void A_Default_Output_Name_That_Would_Be_An_Illegal_Identifier_Is_Refused()
    {
        var nested = SearchTestSchema.Of(
            ("id", SearchTestSchema.Id),
            ("region", SearchTestSchema.Field(FieldType.String, "region", keyword: "region.keyword")),
            ("customer.name", SearchTestSchema.Field(
                FieldType.Decimal, "customer.name", aggregatable: [AggregationFunc.Sum])));

        var exception = Assert.Throws<SearchCompilationException>(() => SearchQueryBuilder.Build(
            new SearchQueryRequest
            {
                Schema = nested,
                Page = SearchTestSchema.FirstPage,
                Group = new GroupSpec { GroupCols = ["region"], GroupKeys = [] },
                Aggregations = [new Aggregation { Field = "customer.name", Func = AggregationFunc.Sum }]
            }));

        Assert.Contains("customer.name_sum", exception.Message);
        Assert.Contains("alias", exception.Message);

        // The same field with an explicit alias compiles: AGG-2 sets one from ColumnVO.id, and
        // AggregationValidator has already held THAT to the pattern.
        var aliased = SearchQueryBuilder.Build(new SearchQueryRequest
        {
            Schema = nested,
            Page = SearchTestSchema.FirstPage,
            Group = new GroupSpec { GroupCols = ["region"], GroupKeys = [] },
            Aggregations =
            [
                new Aggregation { Field = "customer.name", Func = AggregationFunc.Sum, Alias = "customerNameSum" }
            ]
        });

        Assert.True(aliased.Aggs!["region"]!["aggs"]!.AsObject().ContainsKey("customerNameSum"));
    }

    /// <summary>
    /// OSE-11(e), the other half: a BUCKET aggregation is named for a colId, and both engines reject
    /// an aggregation name containing <c>[</c>, <c>]</c> or <c>&gt;</c> — the characters
    /// <c>buckets_path</c> reserves. Held to the engines' own rule rather than to the alias pattern,
    /// so an ordinary dotted colId (a legal container key) still compiles.
    /// </summary>
    [Fact]
    public void A_Group_Column_Whose_ColId_Is_An_Illegal_Aggregation_Name_Is_Refused()
    {
        var hostile = SearchTestSchema.Of(
            ("id", SearchTestSchema.Id),
            ("tags>0", SearchTestSchema.Field(FieldType.String, "tags>0", keyword: "tags>0.keyword")),
            ("nested.path", SearchTestSchema.Field(FieldType.String, "nested.path", keyword: "nested.path.keyword")));

        var exception = Assert.Throws<SearchCompilationException>(() => SearchQueryBuilder.Build(
            new SearchQueryRequest
            {
                Schema = hostile,
                Page = SearchTestSchema.FirstPage,
                Group = new GroupSpec { GroupCols = ["tags>0"], GroupKeys = [] }
            }));

        Assert.Contains("buckets_path", exception.Message);

        var dotted = SearchQueryBuilder.Build(new SearchQueryRequest
        {
            Schema = hostile,
            Page = SearchTestSchema.FirstPage,
            Group = new GroupSpec { GroupCols = ["nested.path"], GroupKeys = [] }
        });

        Assert.True(dotted.Aggs!.ContainsKey("nested.path"));
    }

    /// <summary>
    /// GRP-4's last-but-one bullet: a sort key that is neither the group column nor one of this
    /// level's aggregates is rejected rather than rendered. On SQL the engine would refuse the
    /// statement anyway; here it would silently order by a field the buckets do not contain, which is
    /// why the check has to be the compiler's.
    /// </summary>
    [Fact]
    public void A_Grouped_Sort_Key_The_Level_Does_Not_Produce_Is_Refused()
    {
        var exception = Assert.Throws<SearchCompilationException>(() => Compile([Sum], [
            new SortItem { Field = "id", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }
        ]));

        Assert.Contains("'id'", exception.Message);
    }

    /// <summary>
    /// OSE-11(c), and the finding this test exists for: a grouped level ordered by an AGGREGATE has no
    /// rendering on this backend and is refused.
    ///
    /// <para>A <c>composite</c> always enumerates buckets in composite-key order and returns
    /// <c>size</c> of them; a <c>bucket_sort</c> reorders only that page. So "the 100 largest regions
    /// by revenue" would compile to "the first 100 regions alphabetically, rearranged by revenue" —
    /// a different SET of groups, silently, and invisible to the artifact tier. Elasticsearch states
    /// the general rule: a composite aggregation is not compatible with pipeline aggregations. A prior
    /// revision of this compiler emitted the <c>bucket_sort</c> and
    /// <c>spec/fixtures/group-aggregation/020</c> pinned it; the arm was deleted and the answer is a
    /// refusal, in OSE-11(d)'s own register.</para>
    /// </summary>
    [Fact]
    public void A_Grouped_Level_Ordered_By_An_Aggregate_Is_Refused()
    {
        var exception = Assert.Throws<SearchCompilationException>(() => Compile(
            [Sum],
            [new SortItem
            {
                Field = "amount", Direction = SortDirection.Desc, NullsPosition = NullsPosition.First
            }]));

        Assert.Contains("composite", exception.Message);
        Assert.Contains("pipeline aggregations", exception.Message);
        Assert.Contains("#1622", exception.Message);
    }

    /// <summary>
    /// SEM-6 (Bug #2565) on a grouped level: the group column is unique across the level's buckets by construction, so every key AFTER it is
    /// dropped before the checks above look at it. An aggregate after the group column used to be refused although it could never have changed
    /// the order of one-bucket-per-value rows; it is now served, ordered by the group column alone. The same holds for a key the level does not
    /// produce. A key BEFORE the group column is still refused (the two tests above).
    /// </summary>
    [Theory]
    [InlineData("amount")]
    [InlineData("id")]
    public void A_Key_After_The_Group_Column_Is_Dropped_Rather_Than_Refused(string afterGroupColumn)
    {
        var query = Compile([Sum], [
            new SortItem { Field = "region", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last },
            new SortItem { Field = afterGroupColumn, Direction = SortDirection.Desc, NullsPosition = NullsPosition.First }
        ]);

        var orderBy = Compile([Sum], [
            new SortItem { Field = "region", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }
        ]);

        Assert.Equal(orderBy.ToArtifact().ToJsonString(), query.ToArtifact().ToJsonString());
    }

    /// <summary>
    /// The same refusal on the fixture that used to pin the <c>bucket_sort</c>, asserted BY NAME so
    /// the deleted arm reads as a recorded capability gap rather than an oversight — the Search
    /// conformance runner discovers by artifact presence, so a missing arm is otherwise invisible to
    /// it. Fails the day someone re-adds the file, which is the point.
    /// </summary>
    [Fact]
    public void GroupAggregation020_Has_No_Search_Arm_Because_This_Compiler_Refuses_It()
    {
        const string category = "group-aggregation";
        const string fixture = "020-grouped-query-with-filter-and-sort";

        Assert.False(File.Exists(RepoPaths.Fixture(category, fixture, "expected", "search.artifact.json")));

        var row = SearchFixtureCompiler.PrepareRowFetch(category, fixture);

        var exception = Assert.Throws<SearchCompilationException>(() => SearchQueryBuilder.Build(
            new SearchQueryRequest
            {
                Schema = row.Schema,
                Filter = row.Filter,
                Sort = row.Parsed.Sort,
                Page = row.Parsed.Page,
                Group = row.Parsed.Group,
                Aggregations = row.Parsed.Aggregations
            }));

        Assert.Contains("amount", exception.Message);
        Assert.Contains("composite", exception.Message);
    }
}
