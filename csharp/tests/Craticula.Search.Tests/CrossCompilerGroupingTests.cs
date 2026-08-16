using System.Text.Json.Nodes;
using Craticula.Core.Ir;
using Craticula.Mongo;
using Craticula.Sql;

namespace Craticula.Search.Tests;

/// <summary>
/// The claim no per-backend artifact suite can check: that all three compilers make the <b>same</b>
/// GRP-* decisions. Each conformance suite only ever compares one compiler against its own
/// hand-written arm, which is exactly the assumption that let <c>group-aggregation/017</c>/<c>022</c>
/// carry a bucket-folding collation through three stories unnoticed, and that let all 47
/// <c>operators/</c> Mongo arms sit without SEM-3's mandatory <c>$unset</c> for months.
///
/// <para><b>All three backends are driven from the SAME resolved IR object</b>
/// (<see cref="SearchFixtureCompiler.PrepareRowFetch"/> /
/// <see cref="SearchFixtureCompiler.PrepareSetValues"/> exist for that), so a difference between the
/// outputs is evidence about the compilers rather than about three harnesses that merely look
/// alike. #1643 built the SQL/Mongo pair; this class adds the third backend and, more usefully,
/// turns the DIVERGENCES into assertions: each of the four known ones is asserted as an expected
/// difference naming the fixtures that reach it, so "the divergence disappeared" and "the divergence
/// spread" both fail.</para>
/// </summary>
public class CrossCompilerGroupingTests
{
    private const string Category = "group-aggregation";

    /// <summary>
    /// The one fixture the three compilers cannot be compared on, because one of them <b>refuses</b>
    /// it: <c>020</c> orders a grouped level by an aggregate, which OSE-11(c) has no rendering for.
    /// Excluded here rather than special-cased inside each theory, and asserted by name in
    /// <see cref="OSE11c_Only_Search_Cannot_Order_A_Grouped_Level_By_An_Aggregate"/> so the exclusion
    /// is a stated difference rather than a silent skip.
    /// </summary>
    private const string RefusedBySearch = "020-grouped-query-with-filter-and-sort";

    public static TheoryData<string> Fixtures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var directory in Directory
                         .EnumerateDirectories(RepoPaths.Fixture(Category))
                         .Select(path => Path.GetFileName(path)!)
                         .Where(name => name != RefusedBySearch)
                         .OrderBy(name => name, StringComparer.Ordinal))
            {
                data.Add(directory);
            }

            return data;
        }
    }

    /// <summary>What one backend decided, read out of its own output. Never out of the fixture — the
    /// point is to compare compilers, not artifacts.</summary>
    private sealed record Decisions(
        bool Buckets,
        int DrillDownPredicates,
        bool BucketKeyIsExact,
        bool DrillDownIsExact,
        bool SuppressesCaseInsensitivityForBucketIdentity,
        bool NamesItsOutputColumns);

    private static (Decisions Sql, Decisions Mongo, Decisions Search) Compile(string fixture)
    {
        if (SearchFixtureCompiler.IsSetValuesFixture(Category, fixture))
        {
            var request = SearchFixtureCompiler.PrepareSetValues(Category, fixture);

            var sql = SqlQueryBuilder.BuildSetValues(
                new SqlSetValuesRequest
                {
                    Schema = request.Schema,
                    ColId = request.Spec.ColId,
                    Filter = request.Filter,
                    RowLimit = request.RowLimit
                },
                new PostgresDialect());

            var mongo = MongoPipelineBuilder.BuildSetValues(new MongoSetValuesRequest
            {
                Schema = request.Schema,
                ColId = request.Spec.ColId,
                Filter = request.Filter,
                RowLimit = request.RowLimit
            });

            var search = SearchQueryBuilder.BuildSetValues(new SearchSetValuesRequest
            {
                Schema = request.Schema,
                ColId = request.Spec.ColId,
                Filter = request.Filter,
                SearchPrefix = request.Spec.SearchPrefix,
                RowLimit = request.RowLimit
            });

            return (ReadSql(sql, consumed: []), ReadMongo(mongo, consumed: []), ReadSearch(search, consumed: []));
        }

        var row = SearchFixtureCompiler.PrepareRowFetch(Category, fixture);
        var consumed = ConsumedGroupColumns(row);

        var sqlRow = SqlQueryBuilder.Build(
            new SqlQueryRequest
            {
                Schema = row.Schema,
                Filter = row.Filter,
                Sort = row.Parsed.Sort,
                Page = row.Parsed.Page,
                Group = row.Parsed.Group,
                Aggregations = row.Parsed.Aggregations
            },
            new PostgresDialect());

        var mongoRow = MongoPipelineBuilder.Build(new MongoQueryRequest
        {
            Schema = row.Schema,
            Filter = row.Filter,
            Sort = row.Parsed.Sort,
            Page = row.Parsed.Page,
            Group = row.Parsed.Group,
            Aggregations = row.Parsed.Aggregations
        });

        var searchRow = SearchQueryBuilder.Build(new SearchQueryRequest
        {
            Schema = row.Schema,
            Filter = row.Filter,
            Sort = row.Parsed.Sort,
            Page = row.Parsed.Page,
            Group = row.Parsed.Group,
            Aggregations = row.Parsed.Aggregations
        });

        return (ReadSql(sqlRow, consumed), ReadMongo(mongoRow, consumed), ReadSearch(searchRow, consumed));
    }

    /// <summary>
    /// The already-consumed group columns, resolved to each backend's own PHYSICAL name — which is
    /// the harness bug #1643's version caught in itself before it caught anything else: reading the
    /// drill-down by colId silently missed <c>004</c>'s third predicate, because <c>fiscalYear</c>
    /// maps to <c>fiscal_year</c> on SQL and to itself everywhere else.
    /// </summary>
    private static IReadOnlyList<string> ConsumedGroupColumns(SearchFixtureCompiler.RowFetch row) =>
        row.Parsed.Group is not { } group
            ? []
            : [.. group.GroupCols.Take(group.GroupKeys.Count)];

    private static Decisions ReadSql(SqlQuery query, IReadOnlyList<string> consumed)
    {
        var where = Between(query.Sql, "WHERE ", " ORDER BY", " GROUP BY", " LIMIT");

        return new Decisions(
            Buckets: query.Sql.Contains("GROUP BY", StringComparison.Ordinal)
                || query.Sql.Contains("SELECT DISTINCT", StringComparison.Ordinal),
            DrillDownPredicates: consumed.Count(column =>
                where.Contains($"\"{PhysicalSqlColumn(column)}\" = $", StringComparison.Ordinal)),
            BucketKeyIsExact: !GroupKeyText(query.Sql).Contains("COLLATE", StringComparison.Ordinal),
            DrillDownIsExact: consumed.All(column =>
                !where.Contains($"\"{PhysicalSqlColumn(column)}\" COLLATE", StringComparison.Ordinal)),
            SuppressesCaseInsensitivityForBucketIdentity: false,
            NamesItsOutputColumns: !query.Sql.StartsWith("SELECT *", StringComparison.Ordinal));
    }

    private static Decisions ReadMongo(MongoQuery query, IReadOnlyList<string> consumed)
    {
        var match = query.Pipeline.FirstOrDefault(stage => stage.ContainsKey("$match"))?.ToJsonString() ?? "";

        return new Decisions(
            Buckets: query.Pipeline.Any(stage => stage.ContainsKey("$group")),
            DrillDownPredicates: consumed.Count(column => match.Contains($"\"{column}\":{{\"$eq\"", StringComparison.Ordinal)),
            BucketKeyIsExact: query.Collation is null,
            // MongoDB's collation is query-level, so "the pipeline carries none" IS the drill-down's
            // exactness — there is no per-clause state to read separately.
            DrillDownIsExact: query.Collation is null,
            SuppressesCaseInsensitivityForBucketIdentity: query.CollationSuppressedByBucketIdentity,
            NamesItsOutputColumns: query.Pipeline.Any(stage => stage.ContainsKey("$project") || stage.ContainsKey("$group")));
    }

    private static Decisions ReadSearch(SearchQuery query, IReadOnlyList<string> consumed)
    {
        var filter = query.Query?["bool"]?["filter"]?.AsArray() ?? [];
        var aggregation = query.Aggs is null ? null : query.Aggs.First().Value!.AsObject();

        return new Decisions(
            Buckets: aggregation is not null,
            DrillDownPredicates: consumed.Count(column => filter.Any(clause =>
                clause?["term"] is JsonObject term && term.Any(entry => entry.Key.StartsWith(column, StringComparison.Ordinal)))),
            // A composite source and a `terms` aggregation have no `case_insensitive` parameter at
            // all, so the bucket key is exact by construction — the assertion below is a REGRESSION
            // GUARD (nobody could add one) rather than a discriminator, and it is documented as such
            // for the same reason MDB's BucketKeyFoldsCase reader is.
            BucketKeyIsExact: aggregation is null
                || !aggregation.ToJsonString().Contains("case_insensitive", StringComparison.Ordinal),
            // ...whereas THIS one is a real discriminator, and the only place on this backend where
            // GRP-2's exactness is a choice the compiler makes rather than one the DSL makes for it:
            // a drill-down `term` could perfectly well carry `case_insensitive`, and dropping the
            // provenance that stops it is a silent wrong-bucket bug.
            DrillDownIsExact: consumed.All(column => filter
                .Where(clause => clause?["term"] is JsonObject term
                    && term.Any(entry => entry.Key.StartsWith(column, StringComparison.Ordinal)))
                .All(clause => !clause!.ToJsonString().Contains("case_insensitive", StringComparison.Ordinal))),
            SuppressesCaseInsensitivityForBucketIdentity: query.CaseInsensitivitySuppressedByBucketIdentity,
            // OSE-13: there is no projection member in this DSL's artifact at all — see the expected
            // difference asserted at the bottom of this class.
            NamesItsOutputColumns: aggregation is not null);
    }

    // ---- the agreements ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void All_Three_Backends_Agree_On_Whether_This_Request_Buckets(string fixture)
    {
        var (sql, mongo, search) = Compile(fixture);

        Assert.Equal(sql.Buckets, mongo.Buckets);
        Assert.Equal(sql.Buckets, search.Buckets);
    }

    /// <summary>
    /// GRP-1/GRP-5: every consumed <c>groupKeys</c> entry is reproduced as one predicate, on every
    /// backend. This is the reader that caught <c>group-aggregation/004</c>'s third predicate going
    /// missing in #1643's harness, and it is the strongest of the group: the count is derived from
    /// the REQUEST (how many keys were consumed) and then looked for in each compiler's own output.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void All_Three_Backends_Reproduce_Every_Consumed_Group_Key_As_A_Predicate(string fixture)
    {
        var row = SearchFixtureCompiler.IsSetValuesFixture(Category, fixture)
            ? null
            : SearchFixtureCompiler.PrepareRowFetch(Category, fixture);

        var expected = row is null ? 0 : ConsumedGroupColumns(row).Count;
        var (sql, mongo, search) = Compile(fixture);

        Assert.Equal(expected, sql.DrillDownPredicates);
        Assert.Equal(expected, mongo.DrillDownPredicates);
        Assert.Equal(expected, search.DrillDownPredicates);
    }

    /// <summary>
    /// GRP-5's clause <b>ORDER</b>, on all three backends at once: the synthesised drill-down leads
    /// and the user's own filter follows. Asserted by POSITION rather than by presence, which is the
    /// gap #1644's review pass found —
    /// <see cref="All_Three_Backends_Reproduce_Every_Consumed_Group_Key_As_A_Predicate"/> counts the
    /// drill-down's predicates and never asks where they sit, so reversing the two compositions in any
    /// of the three builders passed every test in every suite.
    ///
    /// <para><b>It was unpinned because no fixture reached it</b>: until <c>023</c> was authored, not
    /// one case anywhere in the suite carried both a consumed <c>groupKeys</c> and a
    /// <c>filterModel</c>, so there were never two provenances to order. The columns are read out of
    /// the resolved IR — the consumed group columns from <c>groupSpec</c>, the user's from the filter
    /// tree — and then located in each compiler's own rendered text, so this is a claim about the
    /// compilers rather than about the artifacts.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void GRP5_The_Drill_Down_Leads_The_Users_Own_Filter_On_All_Three_Backends(string fixture)
    {
        if (SearchFixtureCompiler.IsSetValuesFixture(Category, fixture))
        {
            return;
        }

        var row = SearchFixtureCompiler.PrepareRowFetch(Category, fixture);
        var consumed = ConsumedGroupColumns(row);
        var userColumns = ReferencedFields(row.Filter).Except(consumed, StringComparer.Ordinal).ToList();

        if (consumed.Count == 0 || userColumns.Count == 0)
        {
            return;
        }

        var (sqlText, mongoText, searchText) = RenderedConjunctions(row);

        foreach (var (backend, text, physical) in new[]
                 {
                     ("postgres", sqlText, true),
                     ("mongo", mongoText, false),
                     ("search", searchText, false)
                 })
        {
            var lastDrillDown = consumed.Max(column => Offset(text, column, physical));
            var firstUser = userColumns.Min(column => Offset(text, column, physical));

            Assert.True(
                lastDrillDown < firstUser,
                $"GRP-5 on {backend}: the drill-down must precede the user's filter, but the last " +
                $"drill-down column sits at {lastDrillDown} and the first user column at {firstUser} in: {text}");
        }
    }

    /// <summary>
    /// The anti-vacuity guard for the test above, and the reason <c>023</c> exists at all: without a
    /// fixture carrying BOTH provenances, every case above returns early and the theory asserts
    /// nothing. Named rather than counted, so deleting that fixture fails here instead of quietly
    /// emptying the assertion.
    /// </summary>
    [Fact]
    public void GRP5s_Clause_Order_Is_Reached_By_The_Fixture_Authored_For_It()
    {
        var reached = Directory
            .EnumerateDirectories(RepoPaths.Fixture(Category))
            .Select(path => Path.GetFileName(path)!)
            .Where(name => name != RefusedBySearch)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Where(fixture => !SearchFixtureCompiler.IsSetValuesFixture(Category, fixture))
            .Where(fixture =>
            {
                var prepared = SearchFixtureCompiler.PrepareRowFetch(Category, fixture);
                var consumed = ConsumedGroupColumns(prepared);

                return consumed.Count > 0
                    && ReferencedFields(prepared.Filter).Except(consumed, StringComparer.Ordinal).Any();
            })
            .ToList();

        Assert.Equal(["023-grouped-drill-down-with-active-row-filter"], reached);
    }

    /// <summary>
    /// GRP-2/GRP-6(a): <b>both</b> bucket-identity keys spell no case-folding mechanism on any
    /// backend — the aggregation's own key, and the drill-down predicates that reproduce the bucket
    /// being expanded. The two are asserted together because they must AGREE: a folded drill-down
    /// beside an exact <c>GROUP BY</c> selects rows the parent bucket never contained, so a child
    /// level stops summing to its parent's aggregate, silently and on a well-formed query.
    ///
    /// <para>The drill-down half is the discriminating one on this backend — a <c>term</c> could
    /// carry <c>case_insensitive</c> and would if the compiler lost GRP-5's provenance, whereas an
    /// aggregation has no such parameter to lose.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void All_Three_Backends_Keep_Both_Bucket_Identity_Keys_Exact(string fixture)
    {
        var (sql, mongo, search) = Compile(fixture);

        Assert.True(sql.BucketKeyIsExact);
        Assert.True(mongo.BucketKeyIsExact);
        Assert.True(search.BucketKeyIsExact);

        Assert.True(sql.DrillDownIsExact);
        Assert.True(mongo.DrillDownIsExact);
        Assert.True(search.DrillDownIsExact);
    }

    // ---- the divergences, asserted as expected differences -----------------------------------------

    /// <summary>
    /// GRP-2's accepted divergence #3, and the point of asserting it from BOTH sides: MongoDB's
    /// <c>collation</c> is query-level, so a grouping request there cannot honour SEM-1 on a user's
    /// filter and GRP-2 on the protocol's keys at once and the protocol wins. ES/OpenSearch has
    /// per-clause granularity — <c>case_insensitive</c> sits on the individual <c>term</c> that wants
    /// it — so it satisfies both, exactly as the five SQL dialects do.
    ///
    /// <para>The set is pinned to the three fixtures <c>spec/semantics.md</c> names. Asserting only
    /// "Search suppresses nothing" would also pass if this compiler had stopped resolving SEM-1
    /// entirely, which is why the Mongo side is asserted in the same test.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Only_Mongo_Suppresses_Case_Insensitivity_For_Bucket_Identity(string fixture)
    {
        string[] reachesTheDivergence =
        [
            "005-leaf-query-two-group-cols-fully-consumed",
            "017-set-filter-values-excludes-own-field-filter",
            "022-set-filter-values-prefix-with-active-filter"
        ];

        var (sql, mongo, search) = Compile(fixture);

        Assert.Equal(reachesTheDivergence.Contains(fixture), mongo.SuppressesCaseInsensitivityForBucketIdentity);
        Assert.False(sql.SuppressesCaseInsensitivityForBucketIdentity);
        Assert.False(search.SuppressesCaseInsensitivityForBucketIdentity);
    }

    /// <summary>
    /// AGG-5, asserted as a difference rather than left as an absence: Postgres compiles the group
    /// key's <see cref="NullsPosition"/> into a <c>NULLS</c> marker and MongoDB into its bucket
    /// emulation, while the ES/OpenSearch composite source has no <c>missing_order</c> in the pinned
    /// 7.10-era subset and therefore compiles it to nothing at all. Restricted to the grouped levels,
    /// which are the only shape that has a group key to place.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void AGG5_Only_Search_Cannot_Express_Where_The_Null_Group_Bucket_Sorts(string fixture)
    {
        if (SearchFixtureCompiler.IsSetValuesFixture(Category, fixture))
        {
            return;
        }

        var row = SearchFixtureCompiler.PrepareRowFetch(Category, fixture);

        if (row.Parsed.Group is not { } group || group.GroupKeys.Count >= group.GroupCols.Count)
        {
            return;
        }

        var sql = SqlQueryBuilder.Build(
            new SqlQueryRequest
            {
                Schema = row.Schema,
                Filter = row.Filter,
                Sort = row.Parsed.Sort,
                Page = row.Parsed.Page,
                Group = row.Parsed.Group,
                Aggregations = row.Parsed.Aggregations
            },
            new PostgresDialect());

        var search = SearchQueryBuilder.Build(new SearchQueryRequest
        {
            Schema = row.Schema,
            Filter = row.Filter,
            Sort = row.Parsed.Sort,
            Page = row.Parsed.Page,
            Group = row.Parsed.Group,
            Aggregations = row.Parsed.Aggregations
        });

        Assert.Contains("NULLS", sql.Sql, StringComparison.Ordinal);

        var body = search.Aggs!.ToJsonString();
        Assert.Contains("missing_bucket", body, StringComparison.Ordinal);
        Assert.DoesNotContain("missing_order", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_last", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_first", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// GRP-6(c)'s second accepted divergence, and the one this story had to be careful not to
    /// "fix": a set filter's search prefix folds case on Postgres (<c>ILIKE</c>) and MongoDB
    /// (<c>$options: "i"</c>) because SEM-1 assigns it the pattern-match mechanism, while on
    /// ES/OpenSearch it compiles to the <c>terms</c> aggregation's <c>include</c> — a Lucene regex
    /// evaluated at aggregation time with no case-insensitivity option in the pinned subset. Pinned
    /// to the two fixtures that carry a prefix.
    /// </summary>
    [Theory]
    [InlineData("018-set-filter-values-search-prefix")]
    [InlineData("022-set-filter-values-prefix-with-active-filter")]
    public void GRP6c_Only_Search_Matches_The_Set_Filter_Prefix_Case_Sensitively(string fixture)
    {
        var request = SearchFixtureCompiler.PrepareSetValues(Category, fixture);

        Assert.NotNull(request.Spec.SearchPrefix);

        var sql = SqlQueryBuilder.BuildSetValues(
            new SqlSetValuesRequest
            {
                Schema = request.Schema,
                ColId = request.Spec.ColId,
                Filter = request.Filter,
                RowLimit = request.RowLimit
            },
            new PostgresDialect());

        var mongo = MongoPipelineBuilder.BuildSetValues(new MongoSetValuesRequest
        {
            Schema = request.Schema,
            ColId = request.Spec.ColId,
            Filter = request.Filter,
            RowLimit = request.RowLimit
        });

        var search = SearchQueryBuilder.BuildSetValues(new SearchSetValuesRequest
        {
            Schema = request.Schema,
            ColId = request.Spec.ColId,
            Filter = request.Filter,
            SearchPrefix = request.Spec.SearchPrefix,
            RowLimit = request.RowLimit
        });

        Assert.Contains("ILIKE", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("\"$options\":\"i\"", string.Join("", mongo.Pipeline.Select(stage => stage.ToJsonString())));

        var include = search.Aggs!.First().Value!["terms"]!["include"]!.GetValue<string>();
        Assert.Equal($"{request.Spec.SearchPrefix}.*", include);

        // ...and it is the ONLY place the prefix appears: OSE-12 moves it out of the query entirely,
        // so the target column is absent from the query even in `022`, which does carry a filter on
        // another column. Compiling it in both places would apply the folded form as well and quietly
        // undo the divergence this test exists to pin.
        Assert.DoesNotContain(
            request.Spec.ColId, search.Query?.ToJsonString() ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// GRP-3 has no ES/OpenSearch counterpart, and that is a property of the artifact contract rather
    /// than an omission: <c>spec/fixtures/schemas/search-artifact.schema.json</c> declares
    /// <c>additionalProperties: false</c> over a member set with no <c>_source</c> in it. The grid
    /// reads a hit's <c>_source</c> keys, which are the document's own field names — GRP-3's whole
    /// concern is a PHYSICAL column name reaching a grid that asked for a logical one, and this
    /// backend answers it at index time (the mapping) rather than per query.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void OSE13_Search_Names_No_Output_Columns_On_A_Leaf_Where_Both_Others_Do(string fixture)
    {
        var (sql, mongo, search) = Compile(fixture);

        if (sql.Buckets)
        {
            // At a bucketing level all three name their outputs — the aggregation's own keys.
            Assert.True(sql.NamesItsOutputColumns);
            Assert.True(mongo.NamesItsOutputColumns);
            Assert.True(search.NamesItsOutputColumns);
            return;
        }

        // ...and on a drill-down leaf the other two project an explicit, aliased column list while
        // this backend has no member to put one in.
        Assert.True(sql.NamesItsOutputColumns);
        Assert.True(mongo.NamesItsOutputColumns);
        Assert.False(search.NamesItsOutputColumns);
    }

    /// <summary>
    /// OSE-11(c), asserted as an expected difference rather than left as an excluded fixture: ordering
    /// a <b>grouped</b> level by an aggregate compiles on the other two backends and is <b>refused</b>
    /// here. A <c>composite</c> enumerates buckets in composite-key order and returns <c>size</c> of
    /// them, so a <c>bucket_sort</c> would reorder only that page — "the top 100 regions by revenue"
    /// would become "the first 100 regions alphabetically, rearranged". Elasticsearch states the
    /// general rule: a composite aggregation is not compatible with pipeline aggregations.
    ///
    /// <para>This is a real capability gap, not a spelling difference, which is why the fixture's
    /// Search arm was deleted rather than re-authored. Both halves are asserted: that the other two
    /// still express it, and that this one refuses — the pair is what would catch either "the gap
    /// spread" or "the refusal was quietly relaxed back into a wrong answer".</para>
    /// </summary>
    [Fact]
    public void OSE11c_Only_Search_Cannot_Order_A_Grouped_Level_By_An_Aggregate()
    {
        var row = SearchFixtureCompiler.PrepareRowFetch(Category, RefusedBySearch);

        var request = new
        {
            row.Schema,
            row.Filter,
            row.Parsed.Sort,
            row.Parsed.Page,
            row.Parsed.Group,
            row.Parsed.Aggregations
        };

        var sql = SqlQueryBuilder.Build(
            new SqlQueryRequest
            {
                Schema = request.Schema,
                Filter = request.Filter,
                Sort = request.Sort,
                Page = request.Page,
                Group = request.Group,
                Aggregations = request.Aggregations
            },
            new PostgresDialect());

        var mongo = MongoPipelineBuilder.Build(new MongoQueryRequest
        {
            Schema = request.Schema,
            Filter = request.Filter,
            Sort = request.Sort,
            Page = request.Page,
            Group = request.Group,
            Aggregations = request.Aggregations
        });

        // Both express it: Postgres orders by the aggregate expression, Mongo by the $group output.
        Assert.Contains("ORDER BY SUM(\"amount\")", sql.Sql, StringComparison.Ordinal);
        Assert.Contains(
            "amount_sum",
            mongo.Pipeline.First(stage => stage.ContainsKey("$sort")).ToJsonString(),
            StringComparison.Ordinal);

        var exception = Assert.Throws<SearchCompilationException>(() => SearchQueryBuilder.Build(
            new SearchQueryRequest
            {
                Schema = request.Schema,
                Filter = request.Filter,
                Sort = request.Sort,
                Page = request.Page,
                Group = request.Group,
                Aggregations = request.Aggregations
            }));

        Assert.Contains("composite", exception.Message);
        Assert.Contains("pipeline aggregations", exception.Message);
    }

    // ---- readers ----------------------------------------------------------------------------------

    /// <summary>Every colId a filter tree references, in first-appearance order.</summary>
    private static IReadOnlyList<string> ReferencedFields(FilterNode? node)
    {
        var fields = new List<string>();
        Collect(node);
        return fields;

        void Collect(FilterNode? current)
        {
            switch (current)
            {
                case ComparisonNode comparison when !fields.Contains(comparison.Field, StringComparer.Ordinal):
                    fields.Add(comparison.Field);
                    break;
                case CompositeFilterNode composite:
                    foreach (var child in composite.Nodes)
                    {
                        Collect(child);
                    }

                    break;
                case NotNode not:
                    Collect(not.Node);
                    break;
            }
        }
    }

    /// <summary>The three compilers' rendered conjunctions for one request: Postgres's <c>WHERE</c>
    /// text, Mongo's <c>$match</c> body and Search's <c>bool.filter</c> array, each as written.</summary>
    private static (string Sql, string Mongo, string Search) RenderedConjunctions(
        SearchFixtureCompiler.RowFetch row)
    {
        var sql = SqlQueryBuilder.Build(
            new SqlQueryRequest
            {
                Schema = row.Schema,
                Filter = row.Filter,
                Sort = row.Parsed.Sort,
                Page = row.Parsed.Page,
                Group = row.Parsed.Group,
                Aggregations = row.Parsed.Aggregations
            },
            new PostgresDialect());

        var mongo = MongoPipelineBuilder.Build(new MongoQueryRequest
        {
            Schema = row.Schema,
            Filter = row.Filter,
            Sort = row.Parsed.Sort,
            Page = row.Parsed.Page,
            Group = row.Parsed.Group,
            Aggregations = row.Parsed.Aggregations
        });

        var search = SearchQueryBuilder.Build(new SearchQueryRequest
        {
            Schema = row.Schema,
            Filter = row.Filter,
            Sort = row.Parsed.Sort,
            Page = row.Parsed.Page,
            Group = row.Parsed.Group,
            Aggregations = row.Parsed.Aggregations
        });

        return (
            Between(sql.Sql, "WHERE ", " GROUP BY", " ORDER BY", " LIMIT"),
            mongo.Pipeline.First(stage => stage.ContainsKey("$match")).ToJsonString(),
            (search.Query?["bool"]?["filter"] ?? search.Query)?.ToJsonString() ?? "");
    }

    private static int Offset(string text, string colId, bool physical)
    {
        var index = text.IndexOf(physical ? PhysicalSqlColumn(colId) : colId, StringComparison.Ordinal);

        Assert.True(index >= 0, $"Column '{colId}' does not appear in the rendered conjunction: {text}");

        return index;
    }

    private static string GroupKeyText(string sql)
    {
        var group = sql.IndexOf("GROUP BY", StringComparison.Ordinal);
        if (group >= 0)
        {
            return Between(sql[group..], "GROUP BY ", " ORDER BY", " LIMIT");
        }

        var distinct = sql.IndexOf("SELECT DISTINCT", StringComparison.Ordinal);
        return distinct >= 0 ? Between(sql[distinct..], "SELECT DISTINCT ", " FROM") : "";
    }

    private static string Between(string text, string start, params string[] ends)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        if (from < 0)
        {
            return "";
        }

        from += start.Length;

        var to = ends
            .Select(end => text.IndexOf(end, from, StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(text.Length)
            .Min();

        return text[from..to];
    }

    private static string PhysicalSqlColumn(string colId) => colId switch
    {
        "fiscalYear" => "fiscal_year",
        _ => colId
    };
}
