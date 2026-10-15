namespace Craticula.Core.Ir;

/// <summary>
/// GRP-1's dispatch, resolved once for every backend: what a <see cref="GroupSpec"/> means for the
/// one request that carries it — <see langword="null"/> when the request carries no group state at
/// all.
///
/// <para><b>The rule this owns, in one sentence:</b> fewer consumed <see cref="GroupSpec.GroupKeys"/>
/// than declared <see cref="GroupSpec.GroupCols"/> is a <em>grouped level</em> that buckets by
/// <c>groupCols[groupKeys.Count]</c> — the next not-yet-consumed column, and only that one — while
/// equal lengths are the <em>drill-down leaf</em>, which buckets by nothing and emits raw rows. Both
/// are restricted by the already-consumed keys, as real IR (GRP-5).</para>
///
/// <para><b>Why it takes IR pieces rather than a request.</b> Every backend's own compiler reaches
/// this dispatch through its own request type — <c>SqlQueryRequest</c>, <c>MongoQueryRequest</c>,
/// <c>SearchQueryRequest</c>, and, since #1887, <c>EfCoreQueryRequest</c> (via
/// <c>Craticula.EntityFrameworkCore.EfCoreGridQuery.FetchPageAsync</c> — the EF Core adapter
/// <em>refused</em> grouping before that story, which is what made that fourth consumer the reason
/// this extraction happened at all) — and, since #1647, an EXECUTION ADAPTER that does NOT own its
/// own request type at all: <c>Craticula.MongoDB.Driver.MongoGridQuery</c> calls this with the
/// identical <c>MongoQueryRequest</c> its own compiler (<c>MongoPipelineBuilder</c>) already resolved
/// a pipeline from, purely to relabel a grouped level's literal <c>_id</c> output key back to its
/// logical colId — reading back a decision the compiler already made, not making a new one (see
/// <c>Craticula.MongoDB.Driver.MongoGridPage</c>'s own remarks). Do not hardcode a consumer count in
/// this paragraph again — it has already rotted twice (#1885 wrote "four" while EF still refused
/// grouping, #1887 corrected it to match EF, and this note is the fix for the SAME pattern recurring
/// a third time with #1647): every backend's compiler needs this dispatch, and it will keep growing a
/// consumer per new backend/adapter, which is precisely why it lives here instead of in any one of
/// them. No signature naming any one of those types can serve the others — each is the shape of a
/// <em>backend's</em> own input (a dialect, a collation sink, an index name); the dispatch reads none
/// of that, only the two IR members below, both of which are already <c>Craticula.Core.Ir</c> types.
/// Taking them directly is therefore not a widening for convenience: it is the honest parameter list,
/// and it is what keeps this from becoming a fresh copy every time another request shape appears.
/// Passing <c>aggregations</c> separately follows the IR itself, where <c>groupSpec</c>
/// and <c>aggregations</c> are siblings rather than nested (<c>spec/ir.schema.json</c>).</para>
///
/// <para><b>Why it is in Core rather than in each compiler (#1885).</b> Until this ticket the SQL,
/// Mongo and Search builders each held a private record with this body, and their own comments
/// narrate the drift as it happened: one said GRP-5's synthesis was "called by BOTH compilers", the
/// next said "ALL THREE" — the same sentence edited in place as each copy was made. Nothing in a
/// grouped-versus-leaf decision is backend-specific, so three copies could only ever agree by
/// coincidence, and AR-4 requires the EF adapter and the SQL compiler to return the same rows for the
/// same request. The identical argument that moved <see cref="DrillDownFilter"/>,
/// <see cref="IsoInstant"/> and <see cref="FilterNodeFields"/> here applies unchanged.</para>
///
/// <para><b>This type is legitimately for every compiler and adapter that groups, and that is worth
/// stating.</b> #1645's
/// review found that moving <see cref="Craticula.Core.Schema.SqlPatternEscaping"/> into Core made it
/// <em>reachable</em> from <c>Craticula.Mongo</c>, where SEM-7 forbids conflating it with regex
/// escaping — the assembly boundary had silently been the enforcement, and stopped being it. Nothing
/// of that shape applies here: GRP-1 is one rule with one answer, every backend needs exactly this
/// answer, and there is no neighbouring rule a consumer could reach for by mistake. So take the
/// absence of a "do not call this from X" note as deliberate rather than as an omission — this is a
/// shared decision, not a shared implementation detail that happened to become visible.</para>
/// </summary>
/// <param name="GroupColumn">The next not-yet-consumed group column, which this level buckets by;
/// <see langword="null"/> when every group column has been consumed and the request is the drill-down
/// leaf (<c>spec/fixtures/group-aggregation/005</c>, <c>006</c>).</param>
/// <param name="DrillDown">The already-consumed keys as real IR (GRP-5), or <see langword="null"/> at
/// the top group level.</param>
/// <param name="Aggregations">AR-8's <c>valueCols</c>, empty rather than null so a projection loop
/// needs no second null check. Ignored on the leaf, which projects rows, not aggregates.</param>
public sealed record GroupingPlan(
    string? GroupColumn,
    FilterNode? DrillDown,
    IReadOnlyList<Aggregation> Aggregations)
{
    /// <summary>
    /// Resolves GRP-1's dispatch for <paramref name="group"/>, or <see langword="null"/> when the
    /// request is not a grouping request at all.
    ///
    /// <para>GRP-5's drill-down comes from <see cref="DrillDownFilter.Synthesize"/> rather than being
    /// rebuilt here, for the reason that method's own doc gives: it is the same predicate set
    /// <see cref="Craticula.Core.Schema.GridFilterPipeline"/> charges the client's <c>groupKeys</c>
    /// against SEC-3's leaf budget, so deriving it twice from two implementations would let "the
    /// limits were checked over what actually gets compiled" become a coincidence. It is a pure
    /// function, so calling it here as well is safe — but see its note on reference identity, which
    /// nothing may depend on.</para>
    ///
    /// <para>The <see cref="GroupSpecValidator"/> run that <see cref="DrillDownFilter.Synthesize"/>
    /// performs is also what makes the index below safe: GRP-1's arity rule (consumed &lt;=
    /// <see cref="GroupSpec.GroupCols"/>.Count) is enforced there, not re-derived here, and
    /// <c>ir.schema.json</c> cannot express it at all — JSON Schema has no way to compare two arrays'
    /// lengths.</para>
    /// </summary>
    public static GroupingPlan? Resolve(GroupSpec? group, IReadOnlyList<Aggregation>? aggregations)
    {
        // Defense in depth, for DrillDownFilter.Synthesize's reason and mirroring its first line: a
        // caller can reach a public IR entry point with hand-built input. The GroupSpec half is already
        // covered — Synthesize below validates it, which is what makes the GroupCols[consumed] index
        // safe — and this is the other half.
        //
        // It is not decorative. SearchQueryBuilder.RequireOutputName deliberately SKIPS its OSE-11
        // identifier check when an Alias is present, and says why: ir.schema.json holds `alias` to
        // ^[A-Za-z_][A-Za-z0-9_]*$, so the alias is taken as already checked. An aggregation name is
        // read by Elasticsearch through a buckets_path mini-language where '>' navigates and '.'
        // selects, so an unvalidated alias arriving this way would land in query text as syntax. While
        // these three copies were private that coupling sat two lines from each builder's own
        // AggregationValidator call; #1885 made the entry point public, and the assembly boundary
        // stopped being the thing enforcing it — the same shape #1645's review found when
        // SqlPatternEscaping became reachable from Craticula.Mongo.
        //
        // Idempotent for every current caller: Sql/Mongo/Search validate before calling Resolve, so
        // this is their second, harmless pass. EfCoreGridQuery is the opposite order — it calls
        // Resolve (and so this validation) FIRST, before EfCoreQueryBuilder.ValidateAndBuildRowContext
        // validates again for the RowContext it builds — but the guarantee is the same either way: no
        // caller reaches a downstream step with unvalidated aggregations.
        AggregationValidator.Validate(aggregations);

        if (group is null)
        {
            return null;
        }

        var drillDown = DrillDownFilter.Synthesize(group);
        var consumed = group.GroupKeys.Count;
        var groupColumn = consumed < group.GroupCols.Count ? group.GroupCols[consumed] : null;
        var resolvedAggregations = aggregations ?? [];

        if (groupColumn is not null)
        {
            RejectOutputNameCollisionWithGroupColumn(groupColumn, resolvedAggregations);
        }

        return new GroupingPlan(groupColumn, drillDown, resolvedAggregations);
    }

    /// <summary>
    /// GRP-3: a grouped level's flat projection is <c>[groupColumn, ...aggregates]</c> under GRP-3's
    /// own naming rule, so an aggregation whose <see cref="Aggregation.OutputName"/> equals
    /// <paramref name="groupColumn"/> would share one output column with the group key itself — moved
    /// here from <c>Craticula.Sql.SqlQueryBuilder</c> (#1887's review of #1886's fix), because the row
    /// every backend ultimately hands a grid is the identical flat, colId-keyed shape regardless of
    /// which compiler produced it: MongoDB's <c>_id</c> and Elasticsearch's nested <c>aggs</c> keep the
    /// group key and the aggregates apart only INTERNALLY, until the adapter assembling the outward row
    /// object puts them back together. So this is a contract-level hazard reachable from every backend,
    /// not a SQL-rendering quirk — hence one check here rather than a copy per compiler/adapter.
    ///
    /// <para><b>Skipped when <paramref name="groupColumn"/> is <see langword="null"/></b> (a drill-down
    /// leaf) — a leaf has no group column for an aggregate's name to collide with, and its own
    /// <see cref="Aggregations"/> is ignored entirely (it projects rows, not aggregates).</para>
    ///
    /// <para>Collect-all-then-throw, mirroring <see cref="AggregationValidator"/>'s own pattern: every
    /// colliding aggregation is named in one exception rather than reporting only the first.</para>
    /// </summary>
    private static void RejectOutputNameCollisionWithGroupColumn(
        string groupColumn, IReadOnlyList<Aggregation> aggregations)
    {
        var colliding = new List<int>();
        for (var i = 0; i < aggregations.Count; i++)
        {
            if (aggregations[i].OutputName == groupColumn)
            {
                colliding.Add(i);
            }
        }

        if (colliding.Count == 0)
        {
            return;
        }

        throw new AggregationValidationException([
            $"aggregations[{string.Join(", ", colliding)}] resolve to the output name '{groupColumn}', which " +
            "collides with this grouped level's own group column (GRP-3 always projects it under that name). " +
            "A grid row is flat and keyed by colId regardless of backend, so the group column and a same-named " +
            "aggregate output cannot coexist. Give the colliding valueCol(s) a distinct `id` (alias) so their " +
            $"output name is no longer '{groupColumn}'."
        ]);
    }
}
