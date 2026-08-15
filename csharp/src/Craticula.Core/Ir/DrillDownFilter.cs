namespace Craticula.Core.Ir;

/// <summary>
/// GRP-5: turns the already-consumed entries of a <see cref="GroupSpec"/> into the real IR filter
/// they compile to — one <c>eq</c> per consumed key against its group column, wrapped in an
/// <see cref="AndNode"/> when there is more than one.
///
/// <para><b>Why this is in Core rather than in a compiler.</b> The drill-down is IR, not SQL: the
/// Mongo and Search compilers synthesise the identical predicates, and it is also the shape SEC-3
/// has to count. <see cref="Craticula.Core.Schema.GridFilterPipeline"/> needs it to charge the
/// client-supplied <c>groupKeys</c> against <see cref="Craticula.Core.Schema.ResourceLimits"/> —
/// <c>groupSpec</c> has no <c>maxItems</c> of its own, so without this the keys would be the one
/// class of client-controlled leaf predicate that never reaches the limits check — while the SQL
/// builder needs it to render. Both derive it from the same <see cref="GroupSpec"/> through this one
/// function, which is what makes "the limits were checked over what actually gets compiled" true by
/// construction rather than by two implementations agreeing.</para>
///
/// <para>The result is deliberately a pure function of its input: calling it twice yields two equal
/// but distinct trees, and nothing anywhere may depend on the reference identity of what it returns
/// (see <c>SqlQueryBuilder.RenderWhere</c>, which renders the drill-down under its own provenance
/// context precisely so that it does not).</para>
/// </summary>
public static class DrillDownFilter
{
    /// <summary>
    /// Builds the drill-down predicate for <paramref name="group"/>, or <see langword="null"/> when
    /// there is nothing to restrict by — no group state at all, or the top level of the protocol,
    /// where no key has been consumed yet.
    ///
    /// <para>Runs <see cref="GroupSpecValidator.Validate"/> first (the same defense-in-depth every
    /// other public IR entry point performs, since a caller can reach this with a hand-built
    /// <see cref="GroupSpec"/>), and <see cref="FilterNodeValidator.Validate"/> on its own output
    /// afterwards, mirroring <see cref="Craticula.Core.Schema.SemanticsResolver.AppendStableKeyTiebreaker"/>'s
    /// precedent for a method that MINTS new IR from bare strings: a blank <c>groupCol</c> would
    /// otherwise reach a compiler as a quoted empty identifier.</para>
    /// </summary>
    public static FilterNode? Synthesize(GroupSpec? group)
    {
        GroupSpecValidator.Validate(group);

        if (group is null)
        {
            return null;
        }

        // GRP-1 guarantees consumed <= GroupCols.Count; GroupSpecValidator above is what enforces it.
        var consumed = group.GroupKeys.Count;

        var predicates = new List<FilterNode>(consumed);
        for (var i = 0; i < consumed; i++)
        {
            predicates.Add(new ComparisonNode
            {
                Field = group.GroupCols[i],
                Op = ComparisonOperator.Eq,
                Value = group.GroupKeys[i]
            });
        }

        FilterNode? drillDown = predicates.Count switch
        {
            0 => null,
            1 => predicates[0],
            _ => new AndNode { Nodes = predicates }
        };

        FilterNodeValidator.Validate(drillDown);

        return drillDown;
    }
}
