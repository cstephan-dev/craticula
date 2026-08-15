using Craticula.Core.Ir;

namespace Craticula.Core.Schema;

/// <summary>
/// GRP-6: the two filter-tree operations a BM-3 distinct-values request performs on the filter model
/// it is handed — dropping the target column's own conditions
/// (<see cref="ExcludeOwnField"/>) and turning the set filter's search box into a real predicate
/// (<see cref="SearchPrefixPredicate"/>). Both are named, testable operations rather than inline
/// steps in a compiler, for <see cref="DrillDownFilter"/>'s reason: the Mongo and Search compilers
/// need the identical trees, and two implementations that merely agreed today would be free to drift.
///
/// <para><b>Why this lives beside the schema layer and <see cref="DrillDownFilter"/> does not.</b>
/// The exclusion is exactly the tree pruning <see cref="FieldAllowlistValidator.Strip"/> already
/// implements, down to the edge cases that make it correct — a composite that loses every child
/// disappears rather than becoming an empty node, one that loses down to a single child is unwrapped,
/// and a <see cref="NotNode"/> whose condition vanishes vanishes with it instead of negating nothing.
/// Re-deriving those three in <c>Craticula.Core.Ir</c> to keep this class there would be duplicating
/// the one piece of logic in this codebase whose edge cases are most obviously worth having in one
/// place.</para>
/// </summary>
public static class SetValuesFilter
{
    /// <summary>
    /// GRP-6: removes every condition on <paramref name="colId"/> from <paramref name="filter"/> —
    /// AG Grid's own set-filter convention, and the defining semantic of BM-3.
    ///
    /// <para><b>Why the field's own filter must go.</b> A set filter shows the values the user may
    /// pick from. If the values query honoured the selection already made on that same column, the
    /// list would collapse to what is already selected and the user could never widen it again —
    /// AG Grid's client-side set filter behaves exactly this way, and BM-3 states it as the server
    /// contract. Every <em>other</em> column's filter still applies, so the list stays consistent with
    /// the rows the grid is currently showing.</para>
    ///
    /// <para>A composite mixing the target column with others keeps the others:
    /// <c>(region eq "EMEA" AND segment eq "Enterprise")</c> becomes
    /// <c>segment eq "Enterprise"</c>, not nothing. That follows from pruning conditions rather than
    /// whole branches, and it is the behaviour <c>spec/fixtures/group-aggregation/017</c> pins.</para>
    /// </summary>
    public static FilterNode? ExcludeOwnField(FilterNode? filter, string colId)
    {
        ArgumentException.ThrowIfNullOrEmpty(colId);

        return FieldAllowlistValidator.Strip(filter, [colId]);
    }

    /// <summary>
    /// GRP-6: the set filter's inline search box, as a real IR predicate — a
    /// <see cref="ComparisonOperator.StartsWith"/> comparison on
    /// <see cref="SetValuesSpec.ColId"/> carrying the raw text, or <see langword="null"/> when the box
    /// is empty.
    ///
    /// <para><b>An ordinary predicate on purpose, not a construct of its own.</b> Modelled this way,
    /// SEM-1 picks the field's own case-sensitivity mechanism for it and SEM-7's escaping applies
    /// unchanged, so a literal <c>%</c> typed into the search box cannot widen into a wildcard and no
    /// backend needs a second prefix-matching path. A dedicated "search prefix" rendering would have
    /// had to re-derive both, on every backend, for a predicate that is spelled identically to one a
    /// user could have typed into the column's own text filter.</para>
    ///
    /// <para>Runs <see cref="SetValuesSpecValidator.Validate"/> first and
    /// <see cref="FilterNodeValidator.Validate"/> on its own output afterwards, mirroring
    /// <see cref="DrillDownFilter.Synthesize"/>'s precedent for a method that MINTS new IR from bare
    /// strings.</para>
    /// </summary>
    public static ComparisonNode? SearchPrefixPredicate(SetValuesSpec? setValues)
    {
        SetValuesSpecValidator.Validate(setValues);

        if (setValues?.SearchPrefix is not { } prefix)
        {
            return null;
        }

        var predicate = new ComparisonNode
        {
            Field = setValues.ColId,
            Op = ComparisonOperator.StartsWith,
            Value = new StringValue { Value = prefix }
        };

        FilterNodeValidator.Validate(predicate);

        return predicate;
    }
}
