using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.Sql;

namespace Craticula.Mongo.Tests;

/// <summary>
/// The DoD's claim that BM-2's grouping protocol "matches the SQL compiler's semantics exactly",
/// checked directly instead of inferred.
///
/// <para><b>Why the per-backend suites do not already prove it.</b> Each of them compares one
/// compiler against its own hand-written arm of the same fixture. Two compilers can therefore both
/// be green while disagreeing about a GRP-* rule, provided each disagrees in the same direction as
/// the arm it was checked against — which is not hypothetical: <c>group-aggregation/017</c>'s and
/// <c>022</c>'s Mongo arms carried a query-level <c>collation</c> beside a <c>$group</c> on
/// <c>region</c> for three stories, while their SQL arms were right the whole time, and nothing in
/// the suite could contradict it because no Mongo compiler existed.</para>
///
/// <para><b>What is compared.</b> Not output — the two backends emit entirely different shapes — but
/// the <em>decisions</em>: whether the query buckets at all, whether the bucket key folds case,
/// how many drill-down predicates it emits and whether they compose, which key breaks ties, and
/// whether the projection is named. Both compilers are driven from the SAME resolved IR object
/// (<see cref="MongoFixtureCompiler.PrepareRowFetch"/>), so a difference is evidence about the
/// compilers rather than about two harnesses that merely look alike.</para>
///
/// <para><b>Divergence #3 is asserted as an expected difference, not skipped</b> — see
/// <see cref="Wherever_The_Sql_Arm_Collates_The_Mongo_Arm_Suppresses_It_And_Says_So"/>.</para>
/// </summary>
public class CrossCompilerGroupingTests
{
    private const string Category = "group-aggregation";

    private static IReadOnlyList<string> FixtureDirectories =>
    [
        .. Directory
            .EnumerateDirectories(RepoPaths.Fixture(Category))
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
    ];

    public static TheoryData<string> Fixtures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var directory in FixtureDirectories)
            {
                data.Add(directory);
            }

            return data;
        }
    }

    /// <summary>
    /// One query's GRP-* decisions, in a form both backends can be read for. Every member is a rule
    /// from <c>spec/semantics.md</c>'s grouping addendum rather than a property of either output
    /// format.
    /// </summary>
    /// <param name="ComputesBuckets">GRP-1/GRP-6: does this query group values into buckets at all
    /// (<c>GROUP BY</c> or <c>SELECT DISTINCT</c>; <c>$group</c>), as opposed to returning
    /// rows.</param>
    /// <param name="BucketKeyFoldsCase">GRP-2/GRP-6(a): whether any case-folding mechanism is spelled
    /// on the key that decides bucket identity. Must be <see langword="false"/> on both, always.
    /// <b>The two halves are not equally strong and the difference is worth stating.</b> The SQL half
    /// reads a token out of the statement, so it discriminates: an arm that collated its
    /// <c>GROUP BY</c> would report <see langword="true"/>. The Mongo half cannot, because MongoDB has
    /// no inline collation to read — the only mechanism is the query-level option, so this reduces to
    /// "a <c>$group</c> and a <c>collation</c> do not co-occur", which today's builder makes
    /// unreachable by suppressing one whenever it emits the other. It is a regression guard rather
    /// than a discriminator: deleting that suppression makes <c>017</c> and <c>022</c> fail here.</param>
    /// <param name="DrillDownPredicates">GRP-5: how many of the consumed <c>groupKeys</c> reached the
    /// compiled predicate. A compiler that dropped one returns a different bucket's rows under the
    /// label the client asked for.</param>
    /// <param name="DrillDownComposes">GRP-5: two or more consumed keys render as ONE composite
    /// construct (SQL-2's wrap; a <c>$and</c>), never as loose siblings — the reading GRP-5 rejects
    /// by name.</param>
    /// <param name="TiebreakerIsStableKey">SEM-6 vs GRP-4: a grouped level breaks ties on its group
    /// column because the schema's <c>stableKey</c> is not in its projection, while a drill-down leaf
    /// keeps the stable key. BM-3 appends nothing at all (GRP-6(e)), which is
    /// <see langword="null"/>.</param>
    /// <param name="ProjectionIsNamed">GRP-3: a grouping request names its output columns at every
    /// level; an ordinary row query does not. <b>Inside this category it is <c>true</c> on all 23
    /// fixtures and on both backends</b>, so the equality above cannot discriminate on it — every
    /// case here is a grouped level, a drill-down leaf or a BM-3 query, and GRP-3 names the output of
    /// all three. Its other half is pinned by
    /// <see cref="An_Ordinary_Row_Query_Names_No_Columns_On_Either_Backend"/>, which drives the same
    /// two readers over a fixture from a category that has one.</param>
    private readonly record struct GroupingDecisions(
        bool ComputesBuckets,
        bool BucketKeyFoldsCase,
        int DrillDownPredicates,
        bool DrillDownComposes,
        bool? TiebreakerIsStableKey,
        bool ProjectionIsNamed);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Both_Compilers_Make_The_Same_Grp_Decisions(string fixtureDir)
    {
        var (sql, mongo, context) = Compile(fixtureDir);

        Assert.Equal(ReadSql(sql, context), ReadMongo(mongo, context));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void No_Bucket_Identity_Key_Folds_Case_On_Either_Backend(string fixtureDir)
    {
        // GRP-2/GRP-6(a) stated positively rather than as an equality, so the pair cannot pass by
        // being wrong together.
        var (sql, mongo, context) = Compile(fixtureDir);

        Assert.False(ReadSql(sql, context).BucketKeyFoldsCase);
        Assert.False(ReadMongo(mongo, context).BucketKeyFoldsCase);
    }

    /// <summary>
    /// GRP-2's accepted divergence #3, asserted as an <b>expected difference</b>.
    ///
    /// <para>MongoDB's <c>collation</c> is query-level: it governs <c>$match</c>, <c>$group</c> and
    /// <c>$sort</c> together with no per-stage form, so a pipeline that both computes buckets and
    /// carries a construct SEM-1 wants collated has no way to express both. The protocol's keys win.
    /// The five SQL dialects have no such conflict — <c>COLLATE</c> attaches per construct — so they
    /// collate the user's filter or the leaf's <c>ORDER BY</c> and leave the bucket key bare.</para>
    ///
    /// <para>The assertion is the equality: the Mongo arm <b>suppressed</b> a collation exactly where
    /// the SQL arm <b>spelled</b> one. That distinguishes the divergence from the far commoner case
    /// of a pipeline that simply had no text comparison in it, and it is stronger than asserting
    /// "Mongo has no collation" alone, which would also pass if the Mongo compiler had silently
    /// stopped resolving SEM-1 at all.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Wherever_The_Sql_Arm_Collates_The_Mongo_Arm_Suppresses_It_And_Says_So(string fixtureDir)
    {
        var (sql, mongo, _) = Compile(fixtureDir);

        Assert.Null(mongo.Collation);
        Assert.Equal(sql.Sql.Contains("COLLATE", StringComparison.Ordinal), mongo.CollationSuppressedByBucketIdentity);
    }

    /// <summary>
    /// The three shapes divergence #3 reaches, named so a reader can tell "no case is affected" from
    /// "the affected cases are these". Shape 1 (a grouped request carrying a user <c>eq</c>/<c>in</c>
    /// on a case-insensitive string) has no fixture in the suite at all — <c>spec/semantics.md</c>
    /// says so explicitly — and is covered by
    /// <c>MongoPipelineGroupingTests.A_Grouping_Request_Carries_No_Collation_Even_When_Its_User_Filter_Wants_One</c>.
    /// </summary>
    [Fact]
    public void The_Divergence_Is_Reached_By_Exactly_The_Three_Fixtures_The_Spec_Names()
    {
        var affected = FixtureDirectories
            .Where(fixtureDir => Compile(fixtureDir).Mongo.CollationSuppressedByBucketIdentity)
            .ToList();

        Assert.Equal(
            [
                // Shape 2: a drill-down LEAF sorting a case-insensitive string. Its ORDER BY is
                // SEM-1's (a leaf orders raw rows, not buckets) while its drill-down $match is
                // GRP-2's.
                "005-leaf-query-two-group-cols-fully-consumed",

                // Shape 3, twice: a BM-3 distinct-values request filtering a DIFFERENT
                // case-insensitive string. Easy to miss precisely because the conflicting pair sits
                // on two fields — the collation is wanted for `segment` and forbidden for `region`.
                "017-set-filter-values-excludes-own-field-filter",
                "022-set-filter-values-prefix-with-active-filter"
            ],
            affected);
    }

    /// <summary>
    /// GRP-3's other half, which <c>group-aggregation/</c> cannot supply: an <b>ordinary row query</b>
    /// names no columns, on either backend. Without it <see cref="GroupingDecisions.ProjectionIsNamed"/>
    /// is <see langword="true"/> for all 23 fixtures on both sides, so the equality assertion above
    /// would still hold if both compilers had lost the distinction entirely.
    ///
    /// <para>The fixture is borrowed from another category deliberately: reading it through the same
    /// two readers is what makes this evidence about the readers as well as about the compilers.</para>
    /// </summary>
    [Fact]
    public void An_Ordinary_Row_Query_Names_No_Columns_On_Either_Backend()
    {
        var row = MongoFixtureCompiler.PrepareRowFetch("operators", "001-eq-string");

        var request = new SqlQueryRequest
        {
            Schema = row.Schema,
            Filter = row.Filter,
            Sort = row.Parsed.Sort,
            Page = row.Parsed.Page
        };

        var sql = SqlQueryBuilder.Build(request, PostgresDialect.Instance);
        var mongo = MongoPipelineBuilder.Build(new MongoQueryRequest
        {
            Schema = row.Schema,
            Filter = row.Filter,
            Sort = row.Parsed.Sort,
            Page = row.Parsed.Page
        });

        var context = Context(row.Schema, [], isDistinctValuesQuery: false);

        Assert.False(ReadSql(sql, context).ProjectionIsNamed);
        Assert.False(ReadMongo(mongo, context).ProjectionIsNamed);

        // ...and both agree there is nothing to bucket and nothing to drill into, so the reading
        // above is not an artefact of one compiler having taken a different path entirely.
        Assert.False(ReadSql(sql, context).ComputesBuckets);
        Assert.False(ReadMongo(mongo, context).ComputesBuckets);
        Assert.Equal(0, ReadSql(sql, context).DrillDownPredicates);
        Assert.Equal(0, ReadMongo(mongo, context).DrillDownPredicates);

        // The whole record is deliberately NOT compared here: `DrillDownComposes` is undefined with
        // zero drill-down predicates. Both readers answer it with a proxy for "is the top of the
        // filter a composite" — a leading `(` on SQL, an `$and` key on Mongo — and those two
        // legitimately disagree about a single wrapped leaf, which is what this fixture is. Inside
        // `group-aggregation/` every case either has a drill-down or has no filter at all, so the
        // proxies never meet that shape there.
    }

    // ---- reading each backend's decisions ----------------------------------------------------------

    /// <summary>
    /// What the fixture's own request and schema say about this level, which is shared INPUT rather
    /// than either compiler's decision — so reading it once and handing it to both readers cannot
    /// hide a disagreement.
    ///
    /// <para>The consumed columns are carried as each backend's own PHYSICAL name (SC-4), not as the
    /// colId: <c>group-aggregation</c>'s <c>fiscalYear</c> lives in <c>fiscal_year</c> on SQL and
    /// <c>fiscalYear</c> on MongoDB, and a reader that grepped the colId would silently miss the SQL
    /// arm's third drill-down predicate on <c>004</c> — which is exactly what it did before this
    /// test caught it.</para>
    /// </summary>
    /// <param name="IsDistinctValuesQuery">Which of the two entry points the fixture's own request
    /// document selected — read from the request, never inferred from either output. Inferring it
    /// (say, from a <c>$group</c> with no accumulator) would make the reader guess about a
    /// zero-aggregation grouped level, and a guess is the one thing a cross-compiler oracle may not
    /// contain.</param>
    private readonly record struct FixtureContext(
        IReadOnlyList<string> ConsumedSqlColumns,
        IReadOnlyList<string> ConsumedMongoPaths,
        string StableKey,
        string StableKeySqlColumn,
        bool IsDistinctValuesQuery);

    private (SqlQuery Sql, MongoQuery Mongo, FixtureContext Context) Compile(string fixtureDir)
    {
        if (MongoFixtureCompiler.IsSetValuesFixture(Category, fixtureDir))
        {
            var setValues = MongoFixtureCompiler.PrepareSetValues(Category, fixtureDir);

            return (
                SqlQueryBuilder.BuildSetValues(
                    new SqlSetValuesRequest
                    {
                        Schema = setValues.Schema,
                        ColId = setValues.Spec.ColId,
                        Filter = setValues.Filter,
                        RowLimit = setValues.RowLimit
                    },
                    PostgresDialect.Instance),
                MongoPipelineBuilder.BuildSetValues(new MongoSetValuesRequest
                {
                    Schema = setValues.Schema,
                    ColId = setValues.Spec.ColId,
                    Filter = setValues.Filter,
                    RowLimit = setValues.RowLimit
                }),
                Context(setValues.Schema, [], isDistinctValuesQuery: true));
        }

        var row = MongoFixtureCompiler.PrepareRowFetch(Category, fixtureDir);
        var group = row.Parsed.Group;
        IReadOnlyList<string> consumed = group is null ? [] : [.. group.GroupCols.Take(group.GroupKeys.Count)];

        return (
            SqlQueryBuilder.Build(
                new SqlQueryRequest
                {
                    Schema = row.Schema,
                    Filter = row.Filter,
                    Sort = row.Parsed.Sort,
                    Page = row.Parsed.Page,
                    Group = group,
                    Aggregations = row.Parsed.Aggregations
                },
                PostgresDialect.Instance),
            MongoPipelineBuilder.Build(new MongoQueryRequest
            {
                Schema = row.Schema,
                Filter = row.Filter,
                Sort = row.Parsed.Sort,
                Page = row.Parsed.Page,
                Group = group,
                Aggregations = row.Parsed.Aggregations
            }),
            Context(row.Schema, consumed, isDistinctValuesQuery: false));
    }

    private static FixtureContext Context(
        GridSchema schema, IReadOnlyList<string> consumed, bool isDistinctValuesQuery) => new(
        [.. consumed.Select(colId => schema.Fields[colId].Physical?.Sql?.Column ?? colId)],
        [.. consumed.Select(colId => schema.Fields[colId].Physical?.Mongo?.Path ?? colId)],
        schema.StableKey,
        schema.Fields[schema.StableKey].Physical?.Sql?.Column ?? schema.StableKey,
        isDistinctValuesQuery);

    private static GroupingDecisions ReadSql(SqlQuery query, FixtureContext context)
    {
        var sql = query.Sql;
        var groupBy = Section(sql, " GROUP BY ");
        var orderBy = Section(sql, " ORDER BY ");
        var where = Section(sql, " WHERE ");

        var isDistinct = context.IsDistinctValuesQuery;
        var bucketKey = isDistinct ? Section(sql, "SELECT DISTINCT ") : groupBy;

        // ...and the entry point the request selected really did produce the statement shape it
        // implies, so the shared context cannot paper over a compiler that took the wrong path.
        Assert.Equal(isDistinct, sql.StartsWith("SELECT DISTINCT ", StringComparison.Ordinal));

        return new GroupingDecisions(
            ComputesBuckets: groupBy is not null || isDistinct,
            BucketKeyFoldsCase: bucketKey?.Contains("COLLATE", StringComparison.Ordinal) == true
                || (bucketKey is not null && orderBy?.Split(',')[0].Contains("COLLATE", StringComparison.Ordinal) == true),
            DrillDownPredicates: context.ConsumedSqlColumns.Count(column =>
                where?.Contains($"\"{column}\" = $") == true || where?.Contains($"\"{column}\" IS NULL") == true), // GRP-7: a null key is the null test
            DrillDownComposes: where?.StartsWith('(') == true,
            TiebreakerIsStableKey: isDistinct
                ? null
                : orderBy?.Split(',')[^1].Contains($"\"{context.StableKeySqlColumn}\"", StringComparison.Ordinal),
            ProjectionIsNamed: !sql.StartsWith("SELECT * ", StringComparison.Ordinal));
    }

    private static GroupingDecisions ReadMongo(MongoQuery query, FixtureContext context)
    {
        var group = Stage(query, "$group");
        var match = Stage(query, "$match");
        var sort = Stage(query, "$sort");
        var isDistinct = context.IsDistinctValuesQuery;

        // The one place the drill-down's own predicates are separable from the user filter's: the
        // consumed columns are known from the request, so a predicate naming one of them at the top
        // of the $match is a group-key match. group-aggregation carries no case where a user filter
        // also names a consumed column, which is the same reason the SQL reader can count "col" = $.
        var matchJson = MongoFixtureCompiler.CanonicalJson(match);

        if (isDistinct)
        {
            Assert.DoesNotContain(query.Pipeline, stage => stage.ContainsKey("$skip"));
        }

        return new GroupingDecisions(
            ComputesBuckets: group is not null,

            // On MongoDB there is no inline collation to look for: the ONLY way a bucket key could
            // fold case is the query-level option, which reaches $group and $sort alike.
            BucketKeyFoldsCase: group is not null && query.Collation is not null,

            // The `{` is load-bearing and couples this reader to MDB-1's EXPLICIT $eq spelling
            // (`{"region":{"$eq":"EMEA"}}`): under the implicit form the drill-down would read
            // `"region":"EMEA"` and this count would drop to zero. That is a loud failure, not a
            // silent one — the SQL reader still counts its own predicates and the equality breaks —
            // so the coupling is recorded rather than removed. If MDB-1 is ever re-settled the other
            // way, this line is one of the places that moves.
            DrillDownPredicates: context.ConsumedMongoPaths.Count(path => matchJson.Contains($"\"{path}\":{{")),
            DrillDownComposes: match?.ContainsKey("$and") == true,
            // GRP-6(e): a distinct-values query appends nothing, so there is no tiebreaker to
            // classify — and, by the same rule (the cap is a ceiling, not a window), no $skip either.
            TiebreakerIsStableKey: isDistinct ? null : sort?.Last().Key == context.StableKey,
            ProjectionIsNamed: query.Pipeline.Any(stage => stage.ContainsKey("$project") || stage.ContainsKey("$group")));
    }

    private static JsonObject? Stage(MongoQuery query, string name) =>
        query.Pipeline.FirstOrDefault(stage => stage.ContainsKey(name))?[name] as JsonObject;

    /// <summary>The clause following <paramref name="keyword"/>, up to the next top-level clause
    /// keyword — enough to read one decision out of a statement without parsing SQL.</summary>
    private static string? Section(string sql, string keyword)
    {
        var start = sql.IndexOf(keyword, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var rest = sql[(start + keyword.Length)..];
        var end = ClauseBoundary.Match(rest);
        return (end.Success ? rest[..end.Index] : rest).Trim();
    }

    private static readonly Regex ClauseBoundary = new(
        @" (FROM|WHERE|GROUP BY|ORDER BY|LIMIT|OFFSET|FETCH) ", RegexOptions.Compiled);
}
