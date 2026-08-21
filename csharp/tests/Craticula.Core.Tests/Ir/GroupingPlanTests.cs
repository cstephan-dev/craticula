using Craticula.Core.Ir;

namespace Craticula.Core.Tests.Ir;

/// <summary>
/// GRP-1's dispatch, tested where it now lives (#1885). It had no tests of its own while it was a
/// private record copied into each of the three compilers: every assertion about it was made through
/// a compiled artifact, which is exactly the arrangement that let three copies exist without anything
/// noticing. These are the boundary cases of the dispatch itself — the ones a fourth consumer
/// (<c>Craticula.EntityFrameworkCore</c>, #1887) inherits without compiling any text at all.
/// </summary>
public class GroupingPlanTests
{
    private static GroupSpec Spec(IReadOnlyList<string> groupCols, params string[] consumedKeys) =>
        new()
        {
            GroupCols = groupCols,
            GroupKeys = [.. consumedKeys.Select(k => new StringValue { Value = k })]
        };

    private static Aggregation Agg(string field, AggregationFunc func, string? alias = null) =>
        new() { Field = field, Func = func, Alias = alias };

    [Fact]
    public void No_GroupSpec_Is_Not_A_Grouping_Request_At_All() =>
        Assert.Null(GroupingPlan.Resolve(null, [Agg("amount", AggregationFunc.Sum)]));

    /// <summary>
    /// GRP-1's grouped level, top of the protocol: nothing consumed yet, so the level buckets by the
    /// FIRST group column — never by the whole remaining list — and there is nothing to restrict by.
    /// </summary>
    [Fact]
    public void No_Keys_Consumed_Buckets_By_The_First_GroupCol_With_No_DrillDown()
    {
        var plan = GroupingPlan.Resolve(Spec(["region", "segment"]), null);

        Assert.NotNull(plan);
        Assert.Equal("region", plan.GroupColumn);
        Assert.Null(plan.DrillDown);
    }

    /// <summary>
    /// The middle of a multi-level drill-down, and the off-by-one that matters: with one of two keys
    /// consumed the level buckets by <c>groupCols[1]</c>, not by <c>groupCols[0]</c> again and not by
    /// the leaf's <see langword="null"/>.
    /// </summary>
    [Fact]
    public void One_Key_Consumed_Of_Two_Buckets_By_The_Next_GroupCol()
    {
        var plan = GroupingPlan.Resolve(Spec(["region", "segment"], "EMEA"), null);

        Assert.NotNull(plan);
        Assert.Equal("segment", plan.GroupColumn);

        var eq = Assert.IsType<ComparisonNode>(plan.DrillDown);
        Assert.Equal("region", eq.Field);
        Assert.Equal(ComparisonOperator.Eq, eq.Op);
    }

    /// <summary>
    /// GRP-1's drill-down leaf: every dimension consumed, so the answer is raw rows restricted by all
    /// the keys, and <see cref="GroupingPlan.GroupColumn"/> is <see langword="null"/> — the single
    /// value every consumer dispatches on to stop bucketing
    /// (<c>spec/fixtures/group-aggregation/005</c>, <c>006</c>).
    /// </summary>
    [Fact]
    public void All_Keys_Consumed_Is_The_Leaf_And_Keeps_Every_Key_As_A_Restriction()
    {
        var plan = GroupingPlan.Resolve(Spec(["region", "segment"], "EMEA", "Enterprise"), null);

        Assert.NotNull(plan);
        Assert.Null(plan.GroupColumn);

        var and = Assert.IsType<AndNode>(plan.DrillDown);
        Assert.Equal(["region", "segment"], and.Nodes.Cast<ComparisonNode>().Select(n => n.Field));
    }

    /// <summary>
    /// GRP-1's arity rule is enforced, not worked around. Consuming only as many keys as fit would
    /// index <c>groupCols</c> in range and return a DIFFERENT bucket's rows under the label the client
    /// asked for — a silent wrong answer, which is why the shared
    /// <see cref="GroupSpecValidator"/> that <see cref="DrillDownFilter.Synthesize"/> runs is what the
    /// index below relies on rather than a local bounds check.
    /// </summary>
    [Fact]
    public void More_Keys_Than_GroupCols_Throws_Rather_Than_Consuming_What_Fits()
    {
        var ex = Assert.Throws<GroupSpecValidationException>(() =>
            GroupingPlan.Resolve(Spec(["region"], "EMEA", "Enterprise"), null));

        Assert.Contains("consumes 2 key(s) but groupCols declares only 1", ex.Errors[0]);
    }

    /// <summary>
    /// The OTHER half of the input is validated too, which is not symmetry for its own sake.
    ///
    /// <para><c>GroupSpec</c> is checked transitively — <see cref="DrillDownFilter.Synthesize"/> runs
    /// <c>GroupSpecValidator</c>, and that is what makes the <c>GroupCols[consumed]</c> index safe (the
    /// test above). The aggregations had no such cover: while the three copies of this dispatch were
    /// <see langword="private"/>, each sat two lines below its own builder's
    /// <c>AggregationValidator.Validate</c> call, so the assembly boundary was the enforcement. #1885
    /// made the entry point public and that boundary stopped existing — the same shape #1645's review
    /// found when <c>SqlPatternEscaping</c> became reachable from <c>Craticula.Mongo</c>.</para>
    ///
    /// <para>What it protects, concretely: <c>SearchQueryBuilder.RequireOutputName</c> deliberately
    /// <b>skips</b> its OSE-11 identifier check whenever an <c>Alias</c> is present, because
    /// <c>ir.schema.json</c> holds <c>alias</c> to <c>^[A-Za-z_][A-Za-z0-9_]*$</c> and the validator is
    /// assumed to have enforced it. Elasticsearch reads an aggregation name through a
    /// <c>buckets_path</c> mini-language where <c>&gt;</c> navigates and <c>.</c> selects, so an alias
    /// that reached a name unchecked would land in query text as syntax.</para>
    /// </summary>
    [Fact]
    public void An_Unvalidated_Alias_Cannot_Reach_A_Consumer_Through_This_Entry_Point()
    {
        var ex = Assert.Throws<AggregationValidationException>(() =>
            GroupingPlan.Resolve(Spec(["region"]), [Agg("amount", AggregationFunc.Sum, "totals>amount")]));

        Assert.NotEmpty(ex.Errors);
    }

    /// <summary>
    /// Absent <c>valueCols</c> resolve to an EMPTY list rather than <see langword="null"/>, which is
    /// the whole reason the plan normalises them: every consumer's projection loop would otherwise
    /// need a second null check, and the one that forgot would throw on the most ordinary grouping
    /// request there is — a level with no aggregations at all.
    /// </summary>
    [Fact]
    public void Null_Aggregations_Normalise_To_Empty()
    {
        var plan = GroupingPlan.Resolve(Spec(["region"]), null);

        Assert.NotNull(plan);
        Assert.Empty(plan.Aggregations);
    }

    /// <summary>Request order is preserved: it is the order a grouped projection emits its aggregates
    /// in, and the artifacts compare that list positionally.</summary>
    [Fact]
    public void Aggregations_Are_Carried_In_Request_Order()
    {
        var plan = GroupingPlan.Resolve(
            Spec(["region"]),
            [Agg("amount", AggregationFunc.Sum), Agg("amount", AggregationFunc.Avg, "amountAvg")]);

        Assert.NotNull(plan);
        Assert.Equal([AggregationFunc.Sum, AggregationFunc.Avg], plan.Aggregations.Select(a => a.Func));
        Assert.Equal([null, "amountAvg"], plan.Aggregations.Select(a => a.Alias));
    }

    /// <summary>
    /// The synthesised drill-down is a fresh tree on every call, as
    /// <see cref="DrillDownFilter.Synthesize"/>'s own doc requires. Pinned here because this is the
    /// call site a compiler reaches it through: <c>SqlQueryBuilder.RenderWhere</c> renders the
    /// drill-down under its own GRP-2 provenance context precisely so that nothing has to tell a
    /// group-key match from a user predicate by reference identity, and a plan that memoised the tree
    /// would make that non-local invariant quietly load-bearing.
    /// </summary>
    [Fact]
    public void Each_Resolve_Synthesizes_Its_Own_DrillDown_Tree()
    {
        var group = Spec(["region", "segment"], "EMEA");

        Assert.NotSame(
            GroupingPlan.Resolve(group, null)!.DrillDown,
            GroupingPlan.Resolve(group, null)!.DrillDown);
    }

    // ---- GRP-3's output-name/group-column collision guard (#1887) --------------------------------

    /// <summary>
    /// #1886 found and fixed this shape inside <c>Craticula.Sql.SqlQueryBuilder.RenderProjection</c>
    /// alone; #1887's review of that fix moved the check itself here, because a flat, colId-keyed grid
    /// row makes the collision reachable from every backend, not only SQL's <c>SELECT</c> list — an
    /// aggregation aliased back onto its own level's group column is rejected before any compiler or
    /// adapter ever sees it. This is the shape SQL's own now-unreachable local guard used to catch:
    /// <c>SELECT "region" AS "region", COALESCE(SUM("amount"), 0) AS "region"</c>.
    /// </summary>
    [Fact]
    public void An_Aggregation_Output_Name_Colliding_With_The_Group_Column_Is_Rejected()
    {
        var ex = Assert.Throws<AggregationValidationException>(() => GroupingPlan.Resolve(
            Spec(["region"]),
            [Agg("amount", AggregationFunc.Sum, "region")]));

        Assert.Single(ex.Errors);
        Assert.Contains("'region'", ex.Errors[0]);
        Assert.Contains("group column", ex.Errors[0]);
    }

    /// <summary>Collect-all-then-throw applied to this guard too, mirroring
    /// <see cref="AggregationValidator"/>'s own pattern: two aggregations colliding with the same
    /// group column are named together in ONE error, not the first one found.</summary>
    [Fact]
    public void Every_Colliding_Aggregation_Is_Named_In_One_Error()
    {
        var ex = Assert.Throws<AggregationValidationException>(() => GroupingPlan.Resolve(
            Spec(["region"]),
            [Agg("amount", AggregationFunc.Sum, "region"), Agg("quantity", AggregationFunc.Count, "region")]));

        Assert.Single(ex.Errors);
        Assert.Contains("aggregations[0, 1]", ex.Errors[0]);
    }

    /// <summary>
    /// A leaf has no group column for an aggregate's output name to collide with — its
    /// <see cref="GroupingPlan.Aggregations"/> is ignored entirely (a leaf projects rows, not
    /// aggregates) — so the SAME alias that would be rejected one level up must NOT be rejected here.
    /// </summary>
    [Fact]
    public void A_Drill_Down_Leaf_Has_No_Group_Column_To_Collide_With()
    {
        var plan = GroupingPlan.Resolve(
            Spec(["region"], "EMEA"),
            [Agg("amount", AggregationFunc.Sum, "region")]);

        Assert.NotNull(plan);
        Assert.Null(plan.GroupColumn);
    }

    /// <summary>The ordinary, non-colliding shape must keep working: an aggregation over a DIFFERENT
    /// field from the group column, unaliased, is exactly every other test in this file's shape.</summary>
    [Fact]
    public void A_Non_Colliding_Aggregation_Does_Not_Throw()
    {
        var plan = GroupingPlan.Resolve(Spec(["region"]), [Agg("amount", AggregationFunc.Sum)]);

        Assert.NotNull(plan);
        Assert.Equal("region", plan.GroupColumn);
    }
}
