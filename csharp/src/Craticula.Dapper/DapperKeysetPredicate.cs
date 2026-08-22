using System.Globalization;
using Craticula.Core.Ir;

namespace Craticula.Dapper;

/// <summary>
/// EXP-3's chunk boundary, expressed as ordinary <see cref="FilterNode"/> IR rather than the C#
/// expression trees <c>Craticula.EntityFrameworkCore.EfCoreKeysetPredicate</c> builds for the identical
/// purpose — this package has no expression tree to compose against, only SQL TEXT
/// <see cref="Craticula.Sql.SqlQueryBuilder.Build"/> can recompile. "Every row that sorts strictly after
/// this one", against the query's own <c>ORDER BY</c> tuple: the standard row-value comparison,
/// expanded one key at a time for keys <c>k1..km</c> and the last emitted row's values <c>v1..vm</c>,
/// <code>
/// (k1 after v1)
///   OR (k1 = v1 AND k2 after v2)
///   OR (k1 = v1 AND k2 = v2 AND k3 after v3)
///   OR …
/// </code>
/// merged AND-wise into the request's own filter via <see cref="FilterTreeMerge.And"/> and recompiled —
/// see <see cref="DapperExportStream"/> for the walk this predicate is one step of.
///
/// <para><b>Why an ordinary <see cref="ComparisonNode"/> needs no explicit collation the way the EF
/// version does.</b> <c>Craticula.Sql.SqlQueryBuilder</c> resolves a filter comparison's case
/// sensitivity from the FIELD alone
/// (<c>Craticula.Core.Schema.SemanticsResolver.ResolveCaseSensitivity</c>), and for every sort key the
/// REQUEST itself chose (as opposed to a minted tiebreaker), that is the identical decision
/// <c>SqlQueryBuilder.RenderOrderBy</c> makes for that same field
/// (<c>ResolveOrderingAndGroupKeyCaseSensitivity</c>'s <c>Request</c> provenance reduces to the same
/// per-field classification for an ungrouped ordering). Recompiling this predicate through the SAME
/// compiler that rendered the <c>ORDER BY</c> is therefore what keeps the two collations in agreement —
/// not a copied decision, but the same decision made twice from the same field.</para>
///
/// <para><b>The one place that guarantee does not reach: the MINTED SEM-6 tiebreaker — refused
/// upfront by <see cref="DapperExportStream"/>, not merely documented.</b>
/// <c>ResolveOrderingAndGroupKeyCaseSensitivity</c> forces <c>NotApplicable</c> (no collation mechanism
/// at all) for an APPENDED tiebreaker regardless of the field's own declared sensitivity, precisely
/// because it establishes row identity rather than compares text — and an ordinary
/// <see cref="ComparisonNode"/> against that same field has no way to ask for that same override; only
/// <c>SqlQueryBuilder</c>'s INTERNAL GRP-2 "group-key match" provenance can, and reaching for that here
/// would be exactly the grouping-dispatch machinery this package's plan says not to port. An earlier
/// version of this remark claimed every fixture in the suite gives its <c>stableKey</c> a non-text type
/// — that was measured wrong (23 of 209 fixture schemas declare a STRING <c>stableKey</c>, every
/// <c>group-aggregation/001</c>-<c>023</c>) and the review that caught it treated "documented, narrow,
/// unobserved" as insufficient for a shape the suite already exercises: <see cref="DapperExportStream"/>
/// now throws <see cref="DapperCompilationException"/> before the walk starts whenever the tiebreaker
/// was minted onto a string field without an explicit <c>"sensitive"</c> declaration, rather than
/// walking with a collation this predicate cannot guarantee agrees with the <c>ORDER BY</c>.</para>
///
/// <para><b>A second, independent hazard on the same "string sort key" surface, also refused
/// upfront:</b> <see cref="ComparisonOperator.Blank"/>/<see cref="ComparisonOperator.NotBlank"/> below render as
/// <c>(col IS NULL OR col = '')</c> for a <see cref="Craticula.Core.Schema.FieldType.String"/> field on
/// every dialect except Oracle, which conflates NULL with an empty string for an ordinary "is this
/// blank" filter condition — SEM-2's own deliberate business semantics — but disagrees with the
/// compiled <c>ORDER BY</c>, which treats the two as distinct sort positions. <see cref="DapperExportStream"/>
/// probes for an actual empty string in the request's own filter scope before the walk starts and
/// refuses the export if one exists, rather than risk a dropped row, a duplicated one, or (a
/// <c>NULLS LAST</c> sort) a walk that never terminates. This is a DIFFERENT mechanism from the
/// collation hazard above — one is about case sensitivity, this one is about null-vs-empty-string —
/// and applies to ANY string sort key, not only a minted tiebreaker.</para>
/// </summary>
internal static class DapperKeysetPredicate
{
    /// <summary>
    /// Builds the boundary predicate for the row whose key values are <paramref name="lastValues"/>
    /// (positionally aligned with <paramref name="keys"/>), or <see langword="null"/> when NO row can
    /// sort after it — every key null and every key's nulls placed last, so the last row emitted is the
    /// last row there can be, and the caller stops rather than compiling a constant-false <c>WHERE</c>.
    /// </summary>
    public static FilterNode? Build(IReadOnlyList<SortItem> keys, IReadOnlyList<object?> lastValues)
    {
        FilterNode? disjunction = null;

        for (var index = 0; index < keys.Count; index++)
        {
            if (StrictlyAfter(keys[index], lastValues[index]) is not { } after)
            {
                // Nothing sorts after this key's value, so no tuple that ties on the keys before it can
                // contribute — but a LATER key is still reachable through an equality on this one, so
                // the loop continues rather than stopping.
                continue;
            }

            // The equality prefix: this disjunct describes rows that tie with the last row on every
            // earlier key.
            var disjunct = after;
            for (var earlier = index - 1; earlier >= 0; earlier--)
            {
                disjunct = new AndNode { Nodes = [Equal(keys[earlier], lastValues[earlier]), disjunct] };
            }

            disjunction = disjunction is null ? disjunct : new OrNode { Nodes = [disjunction, disjunct] };
        }

        return disjunction;
    }

    /// <summary>
    /// "This key's value equals the last row's." A <see langword="null"/> last value is tested with
    /// <see cref="ComparisonOperator.Blank"/> rather than an <c>eq</c> against a <c>nullValue</c> — SQL's
    /// <c>= NULL</c> is UNKNOWN for every row (AR-7's documented handling; see
    /// <c>Craticula.Sql.SqlQueryBuilder.EncodeValue</c>), so an equality prefix built that way would make
    /// its whole disjunct unreachable and the walk would stop early, silently, for any dataset with a
    /// null in a leading sort key.
    ///
    /// <para><see cref="ComparisonOperator.Blank"/> is exact (<c>IS NULL</c>) for every non-string
    /// <see cref="Craticula.Core.Schema.FieldType"/> — every <c>stableKey</c> and every leading sort key
    /// in the fixture suite. For a nullable STRING key it also matches an empty string
    /// (<c>SemanticsResolver.ResolveBlankCondition</c>'s <c>NullOrEmptyString</c>), which is a narrower,
    /// separately documented gap: the IR has no bare "is null" comparison shape to ask for instead.</para>
    /// </summary>
    private static FilterNode Equal(SortItem key, object? value) => value is null
        ? new ComparisonNode { Field = key.Field, Op = ComparisonOperator.Blank }
        : new ComparisonNode { Field = key.Field, Op = ComparisonOperator.Eq, Value = Encode(value) };

    /// <summary>
    /// "This key's value sorts strictly after the last row's", or <see langword="null"/> when nothing
    /// does — the same four cases <c>EfCoreKeysetPredicate.StrictlyAfter</c> reasons through, restated
    /// here as IR. When the last row's value IS null: <see cref="NullsPosition.Last"/> means nothing
    /// follows it on THIS key alone (a later key can still discriminate via the equality prefix above,
    /// which is why the caller's loop continues rather than stopping); <see cref="NullsPosition.First"/>
    /// means every non-null value follows it. When the value is NOT null: the values after it are on the
    /// comparison's own direction side, plus every null when this key places nulls LAST.
    ///
    /// <para>Unlike the EF version, no <see cref="bool"/> special case is needed: C# expression trees
    /// have no relational operators for <see cref="bool"/>, but SQL text does (<c>&gt;</c>/<c>&lt;</c>
    /// over a boolean column is ordinary, portable SQL on every dialect this compiler targets), so an
    /// ordinary <see cref="ComparisonOperator.Gt"/>/<see cref="ComparisonOperator.Lt"/> node handles a
    /// boolean sort key exactly like any other.</para>
    /// </summary>
    private static FilterNode? StrictlyAfter(SortItem key, object? value)
    {
        if (value is null)
        {
            if (key.NullsPosition != NullsPosition.First)
            {
                return null;
            }

            // ComparisonOperator.NotBlank, deliberately NOT a NotNode wrapping Blank:
            // SqlQueryBuilder.RenderNot applies SEM-2's tree-level NOT fan-out to every NotNode
            // unconditionally — appending an "OR field IS NULL" disjunct for every field the negated
            // subtree references, which turns "NOT (age IS NULL)" into the tautology
            // "NOT (age IS NULL) OR age IS NULL" (always true). That fan-out exists so a negated FILTER
            // does not silently drop NULL rows from its match set (SEM-2); it is not optional and cannot
            // be told this NotNode means something else. NotBlank is an ordinary comparison LEAF
            // (RenderComparison's own `NOT ({RenderBlank(...)})` string, never routed through RenderNot),
            // so it renders exactly "age IS NOT NULL" with no fan-out — measured after this exact
            // tautology let an already-emitted row (tied on a NULL-valued earlier key) match its own
            // "strictly after" boundary and re-emit as a duplicate.
            return new ComparisonNode { Field = key.Field, Op = ComparisonOperator.NotBlank };
        }

        var ascending = key.Direction == SortDirection.Asc;
        FilterNode strictly = new ComparisonNode
        {
            Field = key.Field,
            Op = ascending ? ComparisonOperator.Gt : ComparisonOperator.Lt,
            Value = Encode(value)
        };

        // Nulls placed last sort after every value of this key, so they are "after" any non-null one.
        return key.NullsPosition == NullsPosition.Last
            ? new OrNode { Nodes = [strictly, new ComparisonNode { Field = key.Field, Op = ComparisonOperator.Blank }] }
            : strictly;
    }

    /// <summary>
    /// Wraps a native CLR value — read off the previous chunk's last row by
    /// <see cref="DapperValueReader"/> — back into AR-7's typed IR so it can be embedded in a fresh
    /// <see cref="ComparisonNode"/> for the NEXT chunk's boundary. The inverse of
    /// <see cref="DapperValueReader.Read"/>, and the two must agree on every
    /// <see cref="Craticula.Core.Schema.FieldType"/> or a round-tripped value silently changes shape
    /// between chunks.
    /// </summary>
    private static TypedValue Encode(object value) => value switch
    {
        string s => new StringValue { Value = s },
        long l => new Int64Value { Value = l },
        // AR-7's numeric string: SqlQueryBuilder binds a decimal as text to avoid the round-tripping
        // loss a floating type would reintroduce, and this boundary value must be bound the same way.
        decimal d => new DecimalValue { Value = d.ToString(CultureInfo.InvariantCulture) },
        double d => new DoubleValue { Value = d },
        bool b => new BooleanValue { Value = b },
        DateOnly date => new DateValue { Value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
        DateTime dt => new DatetimeValue
        {
            Value = DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            Zone = "UTC"
        },
        _ => throw new DapperCompilationException(
            $"Cannot encode a keyset boundary value of CLR type '{value.GetType().Name}' back into typed IR.")
    };
}
