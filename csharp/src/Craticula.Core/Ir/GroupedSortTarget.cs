namespace Craticula.Core.Ir;

/// <summary>
/// GRP-4's sort-target lookup: which aggregation(s) a grouped level's sort key addresses, matched by
/// <see cref="Aggregation.ColumnId"/> — AGG-2's <c>ColumnVO.id</c> reconstructed. The grid sends the
/// <em>column's</em> id in <c>sortModel</c>, never <see cref="Aggregation.OutputName"/>; the two
/// coincide only when an alias is set (see those members' own docs for why they differ otherwise).
///
/// <para><b>Why this is shared rather than left to each compiler.</b> The SQL and MongoDB compilers
/// held byte-identical resolution logic — a <c>foreach</c> returning the FIRST aggregation whose
/// <see cref="Aggregation.ColumnId"/> matched — and both independently missed the same defect:
/// #1886's output-name uniqueness check does not imply column-id uniqueness. <c>Alias ?? Field</c>
/// and <c>Alias ?? "&lt;field&gt;_&lt;func&gt;"</c> are different rules, so two unaliased functions
/// over one field (<c>sum</c> and <c>avg</c> on <c>amount</c>, say — legal, and exactly
/// <c>page-export/011</c>'s shape) resolve to distinct, legal output names while sharing one column
/// id. A first-match lookup then silently orders by whichever aggregation the client happened to
/// list first — a plausible-looking wrong answer, which is worse than a compiler error naming the
/// reason (the same argument <c>ResolveGroupedSortTarget</c> already makes for anything it refuses
/// outright). Two independent copies of the lookup is how two backends came to share the same blind
/// spot; a third copy in the EF Core adapter (#1887) would have been a third chance to miss it.</para>
///
/// <para>This type answers "which aggregation(s) match", nothing else. Each compiler still renders
/// its own success and failure text in its own exception type and its own vocabulary (SQL's
/// expression form, Mongo's bucket path) — only the matching, and the ambiguity it can produce, is
/// shared.</para>
/// </summary>
public static class GroupedSortTarget
{
    /// <summary>
    /// Every aggregation in <paramref name="aggregations"/> whose <see cref="Aggregation.ColumnId"/>
    /// equals <paramref name="sortField"/>, in declaration order. Zero elements means the sort field
    /// is not an aggregate at all (a caller then checks it against the group column); exactly one is
    /// the ordinary case; more than one is GRP-4's ambiguity — a caller must reject it rather than
    /// pick one, and <paramref name="aggregations"/>' own order is not a tie-break a client ever chose.
    /// </summary>
    public static IReadOnlyList<Aggregation> MatchingByColumnId(string sortField, IReadOnlyList<Aggregation> aggregations)
    {
        ArgumentNullException.ThrowIfNull(sortField);
        ArgumentNullException.ThrowIfNull(aggregations);

        return [.. aggregations.Where(aggregation => aggregation.ColumnId == sortField)];
    }

    /// <summary>
    /// One sentence describing the ambiguity, shared so the two compilers' messages read as the same
    /// diagnosis rather than as two people noticing the same thing differently. Each compiler's own
    /// exception type wraps this with whatever else it wants to say.
    /// </summary>
    public static string DescribeAmbiguity(string sortField, IReadOnlyList<Aggregation> matches) =>
        $"The sort key '{sortField}' matches {matches.Count} aggregations " +
        $"({string.Join(", ", matches.Select(a => a.Func))}) that share one column id because neither " +
        "declares an alias (AGG-2's ColumnVO.id defaults to the field itself). Their output names " +
        "already differ (#1886), but the sort key does not carry a func, so the request does not say " +
        "which one to order by. Send an explicit `id` (an alias) on the valueCols that should be " +
        "sortable, so each has its own column id.";
}
