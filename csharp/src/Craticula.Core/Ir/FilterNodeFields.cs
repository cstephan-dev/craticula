namespace Craticula.Core.Ir;

/// <summary>
/// The field names a filter subtree references, in the one order SEM-2 makes normative.
///
/// <para><b>What this is for.</b> SEM-2 requires a tree-level <c>notNode</c> to be null-inclusive one
/// syntax level up from the negative operators: the compiled negation carries one
/// "this column is null" disjunct <em>per distinct field referenced anywhere in the negated
/// subtree</em>. Without them a backend's three-valued logic drops every NULL-valued row from the
/// negation — the exact bug SEM-2 exists to prevent. Which fields, and in which order, is therefore
/// part of the rule rather than a rendering preference, because the order is visible in the emitted
/// artifact (and in its <c>params</c>, on a backend that binds anything for those disjuncts).
///
/// <para><b>Why it lives here and not in the compiler that first needed it (#1645).</b> It was
/// <c>Craticula.Sql.SqlQueryBuilder</c>'s private helper until the EF Core execution adapter had to
/// emit the same fan-out over the same tree. AR-4 permits that adapter to compile the IR directly to
/// expression trees instead of to SQL text, and requires both paths to pass the same fixtures — so
/// the one thing the two must not each own is a decision about <em>what</em> to emit. A second copy
/// of this walk would diverge silently and in the worst possible direction: a fan-out that missed a
/// field yields a perfectly valid query returning fewer rows, with no exception and nothing in the
/// shape of the output to notice. The EF Core adapter specifically has no artifact to read at all —
/// it compiles IR straight to expression trees, never to SQL text — so referencing this compiler for
/// the rule was never an option for it (#1646 corrected this paragraph's earlier framing, which
/// over-generalized that into "an adapter may not depend on a compiler" — Craticula.Dapper depends on
/// this very compiler for real). A rule both need still has exactly one legal home regardless: Core.</para>
///
/// <para>Deliberately NOT on <see cref="Craticula.Core.Schema.SemanticsResolver"/>, which is a set of
/// pure <em>decision</em> functions over one field or one operator. This is a tree traversal, and
/// SEM-2's tree-level shape is the one part of SEM-2 that resolver documents as being a statement
/// about the tree rather than about an operator (see its <c>IsNullInclusive</c> remarks). Keeping it
/// beside <see cref="FilterNode"/>, next to <see cref="FilterTreeMerge"/>, puts it with the other
/// operations <em>on</em> the AST.</para>
/// </summary>
public static class FilterNodeFields
{
    /// <summary>
    /// Every field <paramref name="node"/>'s subtree references, deduplicated, in first-appearance
    /// (left-to-right, depth-first) order.
    ///
    /// <para>First-appearance order rather than sorted, and deduplicated rather than repeated,
    /// because both are observable: the fan-out's disjuncts are emitted in this order, and
    /// <c>spec/fixtures/filter-tree/007</c>-<c>010</c>' artifacts pin the result. A tree referencing
    /// one field twice contributes one disjunct — a second copy would be a redundant term that no
    /// engine would reject, so nothing but this rule keeps the two paths spelling it the same
    /// way.</para>
    ///
    /// <para>Comparison is <see cref="StringComparer.Ordinal"/>: a colId is an identifier resolved
    /// against the <see cref="Craticula.Core.Schema.GridSchema"/> allowlist, whose dictionary is
    /// likewise ordinal, so <c>status</c> and <c>Status</c> are two fields here exactly as they are
    /// there. Folding case would make a fan-out silently lose a disjunct for a schema that declares
    /// both.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The tree contains a <see cref="FilterNode"/> subtype this
    /// method does not know how to walk. Structurally unreachable — <see cref="FilterNode"/>'s
    /// polymorphic hierarchy is closed and <see cref="FilterNodeValidator"/> has already rejected a
    /// null child — but a silently skipped subtree would mean a silently dropped disjunct, which is
    /// the failure this whole helper exists to prevent.</exception>
    public static IReadOnlyList<string> DistinctInOrder(FilterNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Collect(node, ordered, seen);
        return ordered;

        static void Collect(FilterNode node, List<string> ordered, HashSet<string> seen)
        {
            switch (node)
            {
                case CompositeFilterNode composite:
                    foreach (var child in composite.Nodes)
                    {
                        Collect(child, ordered, seen);
                    }

                    return;

                case NotNode not:
                    Collect(not.Node, ordered, seen);
                    return;

                case ComparisonNode comparison:
                    if (seen.Add(comparison.Field))
                    {
                        ordered.Add(comparison.Field);
                    }

                    return;

                default:
                    throw new ArgumentException(
                        $"Unknown FilterNode type '{node.GetType().Name}'.", nameof(node));
            }
        }
    }
}
