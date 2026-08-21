using System.Text.Json.Nodes;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Mongo.Tests;

/// <summary>
/// BM-2 on MongoDB: GRP-1's level dispatch, GRP-2's exact bucket-identity keys (and the accepted
/// divergence they force here and nowhere else), GRP-3's leaf projection, GRP-4's ordering rules and
/// AGG-1's conditional count.
/// </summary>
public class MongoPipelineGroupingTests
{
    private static FieldConfig Field(FieldType type, params AggregationFunc[] aggregatable) => new()
    {
        Type = type,
        Groupable = true,
        Aggregatable = aggregatable
    };

    private static readonly GridSchema Schema = new()
    {
        Source = "sales_fact",
        StableKey = "id",
        FieldOrder = ["id", "region", "segment", "amount"],
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = Field(FieldType.String),
            ["region"] = Field(FieldType.String),
            ["segment"] = Field(FieldType.String),
            ["amount"] = Field(FieldType.Decimal, AggregationFunc.Sum, AggregationFunc.Count)
        }
    };

    private static MongoQuery Compile(
        GroupSpec group,
        IReadOnlyList<Aggregation>? aggregations = null,
        IReadOnlyList<SortItem>? sort = null,
        FilterNode? filter = null) =>
        MongoPipelineBuilder.Build(new MongoQueryRequest
        {
            Schema = Schema,
            Filter = filter,
            Sort = sort,
            Page = new RangePage { StartRow = 0, EndRow = 100 },
            Group = group,
            Aggregations = aggregations
        });

    private static GroupSpec Group(IReadOnlyList<string> cols, params string[] keys) => new()
    {
        GroupCols = cols,
        GroupKeys = [.. keys.Select(key => (TypedValue)new StringValue { Value = key })]
    };

    private static string Json(JsonNode? node) => MongoFixtureCompiler.CanonicalJson(node);

    private static JsonObject? Stage(MongoQuery query, string name) =>
        query.Pipeline.FirstOrDefault(stage => stage.ContainsKey(name))?[name] as JsonObject;

    /// <summary>The whole <c>$sort</c> stage, located by name — its INDEX moves with whether the
    /// level emits a <c>$match</c> and a <c>$group</c>, which is not what any of these tests is
    /// about.</summary>
    private static string SortStage(MongoQuery query) =>
        Json(query.Pipeline.First(stage => stage.ContainsKey("$sort")));

    // ---- GRP-1: one request, one level ------------------------------------------------------------

    [Fact]
    public void Fewer_Keys_Than_Columns_Buckets_By_The_Next_Unconsumed_Column_And_Only_That_One()
    {
        var query = Compile(
            Group(["region", "segment"], "EMEA"),
            [new Aggregation { Field = "amount", Func = AggregationFunc.Sum }]);

        Assert.Equal("""{"_id":"$segment","amount_sum":{"$sum":"$amount"}}""", Json(Stage(query, "$group")));
    }

    [Fact]
    public void Equal_Counts_Are_The_Drill_Down_Leaf_And_Emit_No_Group_Stage_At_All()
    {
        var query = Compile(Group(["region"], "EMEA"));

        Assert.DoesNotContain(query.Pipeline, stage => stage.ContainsKey("$group"));
    }

    // ---- GRP-5: the drill-down is real IR ---------------------------------------------------------

    [Fact]
    public void A_Single_Consumed_Key_Is_One_Predicate_And_Two_Are_An_And_Composite()
    {
        // GRP-5 rejects #1630's original reading, that a synthesised conjunction is not an IR
        // construct and may render flat. It is ordinary IR merged through the same primitive as
        // customFilters, so any compiler reusing its filter renderer gets the composite for free —
        // which is exactly what happens here.
        Assert.Equal(
            """{"region":{"$eq":"EMEA"}}""",
            Json(Stage(Compile(Group(["region", "segment"], "EMEA")), "$match")));

        Assert.Equal(
            """{"$and":[{"region":{"$eq":"EMEA"}},{"segment":{"$eq":"Enterprise"}}]}""",
            Json(Stage(Compile(Group(["region", "segment", "amount"], "EMEA", "Enterprise")), "$match")));
    }

    [Fact]
    public void The_Drill_Down_Leads_And_The_Users_Own_Filter_Trails()
    {
        var query = Compile(
            Group(["region", "segment"], "EMEA"),
            filter: new ComparisonNode
            {
                Field = "segment",
                Op = ComparisonOperator.Eq,
                Value = new StringValue { Value = "Enterprise" }
            });

        Assert.Equal(
            """{"$and":[{"region":{"$eq":"EMEA"}},{"segment":{"$eq":"Enterprise"}}]}""",
            Json(Stage(query, "$match")));
    }

    // ---- GRP-2: exactness, and the divergence it forces on MongoDB --------------------------------

    [Fact]
    public void A_Grouping_Request_Carries_No_Collation_Even_When_Its_User_Filter_Wants_One()
    {
        // Accepted divergence #3, first shape. MongoDB's collation is query-level: it governs $match,
        // $group and $sort together with no per-stage form, so a request whose user filter wants
        // SEM-1's collation and whose $group forbids it has no pipeline expressing both. Bucket
        // identity wins, because getting it wrong makes a child level stop summing to its parent's
        // aggregate silently, while a case-folded filter is a narrower and visible difference.
        var query = Compile(
            Group(["region", "segment"], "EMEA"),
            filter: new ComparisonNode
            {
                Field = "segment",
                Op = ComparisonOperator.Eq,
                Value = new StringValue { Value = "Enterprise" }
            });

        Assert.Null(query.Collation);

        // ...and the fact that one was WANTED survives, so the divergence is assertable rather than
        // indistinguishable from a pipeline that had no string comparison in it.
        Assert.True(query.CollationSuppressedByBucketIdentity);
    }

    [Fact]
    public void A_Drill_Down_Leaf_Sorting_A_Case_Insensitive_String_Also_Suppresses_The_Collation()
    {
        // Divergence #3, second shape — which spec/fixtures/group-aggregation/005 pins, and /006 does
        // NOT: the spec listed both until #1643's review, but 006 carries no sortModel, so its only
        // key is SEM-6's appended tiebreaker, which resolves exact and never wanted a collation.
        // GRP-2 leaves a LEAF's ORDER BY to SEM-1 (it orders raw documents, not buckets), so a
        // user-chosen key does want it; the leaf's own drill-down $match, which reproduces the bucket
        // being expanded, forbids it.
        var query = Compile(
            Group(["region"], "EMEA"),
            sort: [new SortItem { Field = "segment", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }]);

        Assert.Null(query.Collation);
        Assert.True(query.CollationSuppressedByBucketIdentity);
    }

    [Fact]
    public void The_Drill_Down_Predicate_Is_Exact_Where_The_Same_Comparison_As_A_Filter_Would_Collate()
    {
        // The provenance distinction itself, isolated: the two nodes are IDENTICAL in shape, and the
        // only thing telling them apart is which walk rendered them. On MongoDB the difference is not
        // visible in the $match text (there is no inline collation to spell), so it shows up in the
        // suppression flag instead — an ungrouped request with the same predicate does ask for one.
        var grouped = Compile(Group(["region", "segment"], "EMEA"));

        Assert.False(grouped.CollationSuppressedByBucketIdentity);

        var ungrouped = MongoPipelineBuilder.Build(new MongoQueryRequest
        {
            Schema = Schema,
            Filter = new ComparisonNode
            {
                Field = "region",
                Op = ComparisonOperator.Eq,
                Value = new StringValue { Value = "EMEA" }
            },
            Page = new RangePage { StartRow = 0, EndRow = 100 }
        });

        Assert.NotNull(ungrouped.Collation);
    }

    // ---- GRP-3 / GRP-4: projection and ordering ---------------------------------------------------

    [Fact]
    public void The_Leaf_Projects_Every_Declared_Field_In_Declaration_Order_And_Drops_Id()
    {
        // GRP-3: a grouping request names its output at every level, which returning whole stored
        // documents cannot do. Declaration order comes from GridSchema.FieldOrder, read from the
        // schema document — Fields.Keys has no order .NET specifies.
        var query = Compile(Group(["region"], "EMEA"));

        Assert.Equal(
            """{"$project":{"id":1,"region":1,"segment":1,"amount":1,"_id":0}}""",
            Json(query.Pipeline[^1]));
    }

    [Fact]
    public void A_Grouped_Level_Adds_No_Projection_Stage_Because_Group_Already_Is_One()
    {
        var query = Compile(
            Group(["region", "segment"], "EMEA"),
            [new Aggregation { Field = "amount", Func = AggregationFunc.Sum }]);

        Assert.DoesNotContain(query.Pipeline, stage => stage.ContainsKey("$project"));
    }

    [Fact]
    public void A_Grouped_Level_Breaks_Ties_On_Its_Group_Column_Not_On_The_Stable_Key()
    {
        // GRP-4: the schema's stableKey is not in the $group output at all, so sorting by it would
        // order on a field MongoDB treats as missing on every document — every document tied, which
        // is worse than SQL's outright rejection because nothing reports it.
        var query = Compile(Group(["region", "segment"], "EMEA"));

        Assert.Equal("""{"$sort":{"__groupNullBucket":1,"_id":1}}""", SortStage(query));
    }

    [Fact]
    public void A_Grouped_Level_Orders_An_Aggregate_By_Its_Output_Name()
    {
        // GRP-4's "repeat the expression, never refer back to the output name" is a SQL-only rule and
        // says so: after $group the aggregate IS a real field of the document a later $sort reads.
        var query = Compile(
            Group(["region", "segment"], "EMEA"),
            [new Aggregation { Field = "amount", Func = AggregationFunc.Sum }],
            [new SortItem { Field = "amount", Direction = SortDirection.Desc, NullsPosition = NullsPosition.First }]);

        Assert.Equal(
            """{"$sort":{"__amount_sumNullBucket":-1,"amount_sum":-1,"__groupNullBucket":1,"_id":1}}""",
            SortStage(query));
    }

    [Fact]
    public void A_Grouped_Sort_Key_The_Group_Stage_Does_Not_Produce_Is_Refused()
    {
        var failure = Assert.Throws<MongoCompilationException>(() => Compile(
            Group(["region", "segment"], "EMEA"),
            sort: [new SortItem { Field = "id", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }]));

        Assert.Contains("the $group stage does not produce", failure.Message);
    }

    [Fact]
    public void An_Aggregation_Alias_Names_The_Accumulator_And_Is_What_A_Sort_Key_Matches()
    {
        var query = Compile(
            Group(["region", "segment"], "EMEA"),
            [new Aggregation { Field = "amount", Func = AggregationFunc.Sum, Alias = "totalRevenue" }],
            [new SortItem { Field = "totalRevenue", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }]);

        Assert.Equal("""{"_id":"$segment","totalRevenue":{"$sum":"$amount"}}""", Json(Stage(query, "$group")));
        Assert.Equal(
            """{"$sort":{"__totalRevenueNullBucket":1,"totalRevenue":1,"__groupNullBucket":1,"_id":1}}""",
            SortStage(query));
    }

    // ---- AGG-1 ------------------------------------------------------------------------------------

    [Fact]
    public void Count_Is_A_Conditional_Sum_Over_The_Named_Field_Never_A_Row_Count()
    {
        // AGG-1 names `{"$sum": 1}` as the form a port reaches for first and MUST NOT use: it counts
        // every document in the bucket regardless of the field, silently answering a different
        // question the moment that field can be null. SQL's COUNT(expr) and ES's value_count both
        // already skip nulls; on MongoDB the skipping has to be spelled.
        var query = Compile(
            Group(["region", "segment"], "EMEA"),
            [new Aggregation { Field = "amount", Func = AggregationFunc.Count }]);

        Assert.Equal(
            """{"_id":"$segment","amount_count":{"$sum":{"$cond":[{"$eq":["$amount",null]},0,1]}}}""",
            Json(Stage(query, "$group")));
    }

    // ---- MDB-10: the two client-controlled names that share a document ----------------------------

    [Fact]
    public void An_Aggregate_Named_group_Cannot_Take_The_Reserved_Group_Key_Bucket()
    {
        // MDB-10. AGG-2 derives the output name from `valueCols[].id`, so `id: "group"` makes the
        // aggregate's null bucket `__groupNullBucket` — the exact string a grouped level's own _id
        // key claims. `$addFields`/`$sort`/`$unset` are documents, so the collision is silent: the
        // group key's entry overwrites the aggregate's, `_id` becomes the PRIMARY sort key, a null
        // sorts last where nulls-first was asked for, and `$unset` lists the name twice.
        var failure = Assert.Throws<MongoCompilationException>(() => Compile(
            Group(["region", "segment"], "EMEA"),
            [new Aggregation { Field = "amount", Func = AggregationFunc.Sum, Alias = "group" }],
            [new SortItem { Field = "group", Direction = SortDirection.Desc, NullsPosition = NullsPosition.First }]));

        Assert.Contains("__groupNullBucket", failure.Message);
        Assert.Contains("reserved", failure.Message);
    }

    /// <summary>
    /// The same last-wins hazard one stage earlier: <c>$group</c> is a document too, so the second
    /// accumulator would replace the first and the grid would read one column holding another
    /// column's number, out of a pipeline that is syntactically perfect.
    ///
    /// <para><b>The refusal moved layers in #1886, and the exception type is the evidence — for THIS
    /// shape only.</b> <c>Craticula.Sql</c> had no equivalent guard — it emitted the column twice and
    /// let the driver decide — so two aggregations colliding with each other is now caught one layer
    /// up, in <c>Craticula.Core</c>'s <see cref="AggregationValidator"/>, which
    /// <see cref="MongoPipelineBuilder.Build"/> runs on entry like every other compiler. What this
    /// test pins is that THAT collision is still refused, now on all four backends at once.</para>
    ///
    /// <para><b><c>RenderGroup</c>'s own <c>ContainsKey</c> guard is NOT redundant, and is not merely
    /// kept as a courtesy.</b> Core's check only compares aggregations against each other; it has no
    /// notion of <c>_id</c>, the bucket key this stage seeds into <c>group</c> before the loop below
    /// runs. An aggregation whose output name resolves to <c>_id</c> — reachable from the wire via an
    /// explicit alias, which <c>ir.schema.json</c>'s identifier pattern permits — collides with the
    /// SEED, not with a sibling aggregation, and only this method's own guard sees that. See
    /// <see cref="An_Aggregation_Aliased_To_The_Bucket_Key_Is_Refused_Not_Silently_Overwritten"/>
    /// below for that case, which <see cref="AggregationValidator"/> alone does not catch.</para>
    /// </summary>
    [Fact]
    public void Two_Aggregations_Cannot_Share_One_Output_Name()
    {
        var failure = Assert.Throws<AggregationValidationException>(() => Compile(
            Group(["region", "segment"], "EMEA"),
            [
                new Aggregation { Field = "amount", Func = AggregationFunc.Sum, Alias = "total" },
                new Aggregation { Field = "amount", Func = AggregationFunc.Count, Alias = "total" }
            ]));

        Assert.Contains("output name 'total'", failure.Message);
    }

    /// <summary>
    /// The case <see cref="AggregationValidator"/> structurally cannot cover, because it is not a
    /// collision between two aggregations: an alias of <c>_id</c> passes the shared validator (the
    /// identifier pattern has nothing against it, and there is only one aggregation to compare it
    /// to), then reaches <see cref="MongoPipelineBuilder.RenderGroup"/>, whose own guard is what
    /// catches it — because <c>group</c> is seeded with the <c>_id</c> bucket key BEFORE this
    /// aggregation's output name is ever checked against it. Without that seed-then-check ordering,
    /// or without the guard itself, <c>group["_id"]</c> would be silently overwritten by the
    /// accumulator, and the response would carry no bucket key at all.
    /// </summary>
    [Fact]
    public void An_Aggregation_Aliased_To_The_Bucket_Key_Is_Refused_Not_Silently_Overwritten()
    {
        var failure = Assert.Throws<MongoCompilationException>(() => Compile(
            Group(["region"]),
            [new Aggregation { Field = "amount", Func = AggregationFunc.Sum, Alias = "_id" }]));

        Assert.Contains("output name '_id'", failure.Message);
        Assert.Contains("bucket key", failure.Message);
    }

    [Fact]
    public void More_Group_Keys_Than_Columns_Is_Rejected_By_The_Shared_Validator()
    {
        // GRP-1's arity rule lives in Craticula.Core's GroupSpecValidator, not in a compiler — its
        // failure mode is silent (a different bucket's rows under the label the client asked for), so
        // the compiler that forgot it would return wrong rows rather than an error. This asserts the
        // Mongo builder runs it, not that it implements it.
        Assert.Throws<GroupSpecValidationException>(() => Compile(Group(["region"], "EMEA", "Enterprise")));
    }
}
