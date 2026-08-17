using System.Linq.Expressions;
using Craticula.Core.Ir;

namespace Craticula.EntityFrameworkCore;

/// <summary>
/// EXP-3's chunk boundary: "every row that sorts strictly after this one", as a predicate over the
/// query's own <c>ORDER BY</c> tuple. The alternative — <c>OFFSET n</c> — is what EXP-3 forbids for an
/// export, because the engine must re-read and discard all <c>n</c> skipped rows on every chunk, making
/// a full download quadratic in its own length.
///
/// <para><b>The shape, and why it is a tuple rather than "id &gt; last id".</b> A keyset predicate is
/// only correct against the ordering it chunks. An export sorted
/// <c>(orderDate DESC, id ASC)</c> is not sorted by <c>id</c>, so <c>id &gt; lastId</c> would both skip
/// rows and repeat them. The standard row-value comparison is therefore expanded one key at a time:
/// for keys <c>k1..km</c> and the last row's values <c>v1..vm</c>,</para>
/// <code>
/// (k1 after v1)
///   OR (k1 = v1 AND k2 after v2)
///   OR (k1 = v1 AND k2 = v2 AND k3 after v3)
///   OR …
/// </code>
/// <para>where <c>after</c> and <c>=</c> both mean "as this query's <c>ORDER BY</c> compares them" —
/// same direction, same null placement, and <b>same collation</b>. That last one is easy to miss and
/// breaks the walk quietly: if the ordering collates a text key case-insensitively while the boundary
/// compares it bit-for-bit, the two disagree about which rows are "after", and a chunk edge drops or
/// duplicates whatever falls between the two answers. So the collation comes from the same
/// <see cref="EfCoreQueryBuilder.SortKeyPlan"/> the ordering was built from, never re-derived.</para>
///
/// <para><b>SEM-6 is what makes any of this sound, which is worth stating explicitly.</b> A keyset walk
/// needs the ordering to be a TOTAL order: if two rows compare equal on every key, no predicate can
/// separate them, and the walk either loops on them or skips one. SEM-6's appended
/// <see cref="Craticula.Core.Schema.GridSchema.StableKey"/> tiebreaker is exactly that guarantee — the
/// last key is unique and uncollated (<see cref="Craticula.Core.Schema.SemanticsResolver.SortKeyProvenance.AppendedTiebreaker"/>
/// resolves <c>NotApplicable</c> for the reason its own remarks give: a nondeterministic collation on
/// the tiebreaker would leave the very ties it exists to break unbroken).
///
/// <para><b>What happens if that precondition is false is worth being precise about, because the obvious
/// guess is wrong.</b> The predicate below is "strictly after the last row emitted", so the walk is
/// monotone: it can never repeat a row and can never fail to terminate. A non-unique <c>stableKey</c>
/// therefore does not hang an export — it makes the ordering non-total, so two rows tie on the whole
/// tuple and the second is <em>silently skipped</em> at whichever chunk edge it falls on, yielding a short
/// file that looks complete. <see cref="EfCoreExportStream"/> looks for that directly (two adjacent rows
/// sharing the trailing key) rather than for a stalled boundary, which cannot occur.</para>
/// </summary>
internal static class EfCoreKeysetPredicate
{
    /// <summary>
    /// Builds the boundary predicate for the row whose key values are <paramref name="lastKeys"/>
    /// (positionally aligned with <paramref name="keys"/>), or <see langword="null"/> when <b>no row can
    /// sort after it</b> — which is a real answer and not a failure: with every key null and every key's
    /// nulls placed last, the last row is the last row there can be, and the caller stops rather than
    /// issuing a query whose <c>WHERE</c> is constant false.
    /// </summary>
    public static Expression<Func<TRow, bool>>? Build<TRow>(
        IReadOnlyList<EfCoreQueryBuilder.SortKeyPlan> keys,
        IReadOnlyList<object?> lastKeys,
        EfCoreQueryBuilder.RowContext context)
    {
        Expression? disjunction = null;

        for (var index = 0; index < keys.Count; index++)
        {
            if (StrictlyAfter(keys[index], lastKeys[index], context) is not { } after)
            {
                // Nothing sorts after this key's value, so no tuple that ties on the keys before it can
                // contribute — but a LATER key still can only be reached through an equality on this
                // one, so the loop continues rather than stopping.
                continue;
            }

            // The equality prefix: this disjunct describes rows that tie with the last row on every
            // earlier key. Built fresh per disjunct rather than accumulated, because an accumulated
            // Expression would be shared between disjuncts — legal, but it makes the generated SQL's
            // shape depend on node reuse rather than on this loop.
            var disjunct = after;
            for (var earlier = index - 1; earlier >= 0; earlier--)
            {
                disjunct = Expression.AndAlso(Equal(keys[earlier], lastKeys[earlier], context), disjunct);
            }

            disjunction = disjunction is null ? disjunct : Expression.OrElse(disjunction, disjunct);
        }

        return disjunction is null
            ? null
            : Expression.Lambda<Func<TRow, bool>>(disjunction, context.Row);
    }

    /// <summary>
    /// "This key's value equals the last row's", under the ordering's own comparison. A null value is
    /// an <c>IS NULL</c> test rather than an equality against a null parameter: SQL's <c>= NULL</c> is
    /// UNKNOWN for every row, so an equality prefix built that way would make its whole disjunct
    /// unreachable and the walk would stop early — silently, and only for datasets that happen to have
    /// a null in a leading sort key.
    /// </summary>
    private static Expression Equal(
        EfCoreQueryBuilder.SortKeyPlan key, object? value, EfCoreQueryBuilder.RowContext context)
    {
        var property = EfCoreQueryBuilder.Property(context, key.Field);

        if (value is null)
        {
            return Expression.Equal(property, Expression.Constant(null, property.Type));
        }

        var collated = EfCoreQueryBuilder.Collated(property, key.CaseSensitivity, context.Semantics);
        return Expression.Equal(collated, EfCoreQueryBuilder.Bind(value, collated.Type));
    }

    /// <summary>
    /// "This key's value sorts strictly after the last row's", or <see langword="null"/> when nothing
    /// does.
    ///
    /// <para>Four cases, and the two null ones are where a keyset walk usually goes wrong. When the last
    /// row's value <b>is</b> null: with <see cref="NullsPosition.Last"/> nothing follows it (null is the
    /// end of this key's order, so the answer is <see langword="null"/> — not "no rows", since a later
    /// key can still discriminate rows that tie on this null, which is what the caller's equality prefix
    /// expresses); with <see cref="NullsPosition.First"/> every non-null value follows it. When the value
    /// is NOT null: the values after it are those on the far side of the comparison the direction
    /// chooses, <em>plus every null</em> if this key places nulls last.</para>
    ///
    /// <para><see cref="FieldType.Boolean"/> is spelled separately because C# defines no relational
    /// operators for <see cref="bool"/> and <see cref="Expression.GreaterThan"/> throws for it. There is
    /// nothing to approximate: a boolean key has exactly two values, so "after <c>false</c>, ascending"
    /// is <c>= true</c> and "after <c>true</c>, ascending" is nothing at all.</para>
    /// </summary>
    private static Expression? StrictlyAfter(
        EfCoreQueryBuilder.SortKeyPlan key, object? value, EfCoreQueryBuilder.RowContext context)
    {
        var property = EfCoreQueryBuilder.Property(context, key.Field);
        var isNull = Expression.Equal(property, Expression.Constant(null, property.Type));

        if (value is null)
        {
            return key.NullsPosition == NullsPosition.First
                ? Expression.Not(isNull)
                : null;
        }

        var ascending = key.Direction == SortDirection.Asc;
        Expression? strictly;

        if (property.Type == typeof(bool?))
        {
            var last = (bool)value;
            var beyond = ascending;
            strictly = last == beyond
                ? null
                : Expression.Equal(property, EfCoreQueryBuilder.Bind(beyond, property.Type));
        }
        else
        {
            var collated = EfCoreQueryBuilder.Collated(property, key.CaseSensitivity, context.Semantics);
            var bound = EfCoreQueryBuilder.Bind(value, collated.Type);

            strictly = ascending
                ? EfCoreQueryBuilder.Compare(collated, bound, Expression.GreaterThan)
                : EfCoreQueryBuilder.Compare(collated, bound, Expression.LessThan);
        }

        // Nulls placed last sort after every value of this key, so they are "after" any non-null one.
        if (key.NullsPosition != NullsPosition.Last)
        {
            return strictly;
        }

        return strictly is null ? isNull : Expression.OrElse(strictly, isNull);
    }
}
