using System.Globalization;
using System.Text;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Sql;

/// <summary>
/// BM-1's dialect-agnostic query builder: walks the IR filter/sort/page spec and emits
/// <c>SELECT * FROM &lt;source&gt; [WHERE &lt;tree&gt;] ORDER BY &lt;keys&gt; [&lt;paging&gt;]</c> as a
/// <see cref="SqlQuery"/>. Every token that differs between SQL dialects goes through
/// <see cref="ISqlDialect"/>; nothing in this file switches on which dialect it was handed, which is
/// what makes "adding a dialect requires zero builder changes" checkable rather than aspirational
/// (see <c>docs/architecture/c4-components-sql-compiler.md</c>).
///
/// <para><b>Semantics are never re-derived here.</b> Case-sensitivity mechanism, null-inclusivity,
/// the <c>blank</c> condition and the SEM-6 tiebreaker all come from
/// <see cref="SemanticsResolver"/> (#1634) — the one place SEM-* rules live, so the SQL, Mongo and
/// Search compilers cannot drift apart (G3/G4). What IS this compiler's own responsibility is the
/// SQL <em>shape</em> those decisions render into: <c>spec/semantics.md</c>'s SQL-0..SQL-6 addendum,
/// and specifically the tree-level <c>NOT</c> fan-out, which SemanticsResolver deliberately has no
/// method for because it is a statement about tree shape rather than about one operator.</para>
///
/// <para><b>Scope (#1638).</b> Leaf queries only. Grouped queries, aggregations and <c>GROUP BY</c>
/// are BM-2 (#1640); the set-filter distinct-values query is BM-3 (#1641); the explicit projection
/// and <c>exportScope</c> belong to the export feature. See <see cref="SqlQueryRequest.Group"/> for
/// how group state is tolerated in the meantime.</para>
/// </summary>
public static class SqlQueryBuilder
{
    /// <summary>
    /// Compiles <paramref name="request"/> into <paramref name="dialect"/>'s SQL text plus its
    /// ordered parameters. Re-runs the IR's own structural validators first — the same
    /// defense-in-depth every other public entry point in this codebase performs
    /// (<see cref="TypeCoercionValidator.Coerce"/>, <see cref="ResourceLimitsValidator.Validate"/>,
    /// <see cref="SemanticsResolver.AppendStableKeyTiebreaker"/>), since a caller can reach this
    /// method with a hand-built tree that never went through <see cref="GridFilterPipeline"/>.
    /// </summary>
    /// <exception cref="SqlCompilationException">A structurally valid shape this compiler has no
    /// defined rendering for — an unsupported <see cref="SqlMapping"/> form, or a field the
    /// <see cref="GridSchema"/> does not declare.</exception>
    public static SqlQuery Build(SqlQueryRequest request, ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dialect);

        FilterNodeValidator.Validate(request.Filter);
        PageSpecValidator.Validate(request.Page);

        var parameters = new SqlParameterBag(dialect);
        var context = new Context(request.Schema, dialect, parameters);

        var sql = new StringBuilder("SELECT * FROM ").Append(dialect.QuoteIdentifier(request.Schema.Source));

        if (request.Filter is not null)
        {
            sql.Append(" WHERE ").Append(RenderNode(request.Filter, context));
        }

        sql.Append(" ORDER BY ").Append(RenderOrderBy(request.Sort, context));

        // EXP-2: an unpaginated request renders nothing at all, so the statement ends after ORDER BY.
        if (ResolveRowWindow(request.Page) is { } window)
        {
            sql.Append(' ').Append(dialect.RenderPaging(window.Offset, window.Limit, parameters));
        }

        return new SqlQuery
        {
            Dialect = dialect.Name,
            Sql = sql.ToString(),
            Parameters = parameters.Values
        };
    }

    /// <summary>The per-compilation state threaded through the tree walk, so no rendering method
    /// needs four parameters of plumbing.</summary>
    private readonly record struct Context(GridSchema Schema, ISqlDialect Dialect, ISqlParameterSink Parameters);

    // ---- filter tree ----------------------------------------------------------------------------

    private static string RenderNode(FilterNode node, Context context) => node switch
    {
        AndNode and => RenderComposite(and, " AND ", context),
        OrNode or => RenderComposite(or, " OR ", context),
        NotNode not => RenderNot(not, context),
        ComparisonNode comparison => RenderComparison(comparison, context),
        _ => throw new SqlCompilationException($"Unknown FilterNode type '{node.GetType().Name}'.")
    };

    /// <summary>
    /// SQL-2: a composite is wrapped and its children are rendered bare — <c>(a AND b)</c>, never
    /// <c>(a) AND (b)</c>. A single-child composite still wraps (<c>(x)</c>), which is what makes
    /// <c>spec/fixtures/filter-tree/011</c>-<c>013</c>'s nesting depths reproducible rather than
    /// collapsed away.
    /// </summary>
    private static string RenderComposite(CompositeFilterNode composite, string separator, Context context)
    {
        var rendered = new List<string>(composite.Nodes.Count);
        foreach (var child in composite.Nodes)
        {
            rendered.Add(RenderNode(child, context));
        }

        return "(" + string.Join(separator, rendered) + ")";
    }

    /// <summary>
    /// SEM-2/SQL-2: a tree-level <c>NOT</c> is null-inclusive one syntax level up from the negative
    /// operators — <c>(NOT (&lt;child&gt;) OR &lt;col&gt; IS NULL [OR ...])</c>, with one disjunct per
    /// distinct field referenced anywhere in the child subtree, in first-appearance order. Without
    /// them, SQL's three-valued logic drops every NULL row from the negation, which is the exact bug
    /// SEM-2 exists to prevent — <c>NOT (col ILIKE ...)</c> and a bare <c>col &lt;&gt; ...</c> fail
    /// identically.
    ///
    /// <para>This is the one SEM-2 shape <see cref="SemanticsResolver"/> deliberately has no method
    /// for (see <see cref="SemanticsResolver.IsNullInclusive"/>'s own doc): it is a statement about
    /// the tree, not about an operator, so the compiler owns it.</para>
    /// </summary>
    private static string RenderNot(NotNode not, Context context)
    {
        // Rendered BEFORE the IS NULL disjuncts so the child's own parameters keep first-appearance
        // order; the disjuncts bind nothing.
        var child = RenderNode(not.Node, context);

        var negation = new StringBuilder("(NOT (").Append(child).Append(')');
        foreach (var field in DistinctFieldsInOrder(not.Node))
        {
            negation.Append(" OR ").Append(ResolveColumn(field, context)).Append(" IS NULL");
        }

        return negation.Append(')').ToString();
    }

    /// <summary>Every field a subtree references, deduplicated, in first-appearance (left-to-right,
    /// depth-first) order — the order the <c>NOT</c> fan-out's <c>IS NULL</c> disjuncts follow.</summary>
    private static IReadOnlyList<string> DistinctFieldsInOrder(FilterNode node)
    {
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
                    throw new SqlCompilationException($"Unknown FilterNode type '{node.GetType().Name}'.");
            }
        }
    }

    private static string RenderComparison(ComparisonNode node, Context context)
    {
        var field = ResolveField(node.Field, context);
        var column = ResolveColumn(node.Field, context);

        return node.Op switch
        {
            ComparisonOperator.Blank => RenderBlank(column, field),
            ComparisonOperator.NotBlank => $"NOT ({RenderBlank(column, field)})",

            // AR-6's unary boolean shorthand carries no value of its own, so the compiler mints one
            // rather than inlining a `TRUE`/`FALSE` literal — SEC-2 wants every compared value bound,
            // and the fixtures pin the parameterized form (`"is_active" = $1`, params [true]).
            ComparisonOperator.True => RenderScalar(column, field, ComparisonOperator.Eq, new BooleanValue { Value = true }, context),
            ComparisonOperator.False => RenderScalar(column, field, ComparisonOperator.Eq, new BooleanValue { Value = false }, context),

            ComparisonOperator.Eq or ComparisonOperator.Neq
                or ComparisonOperator.Gt or ComparisonOperator.Gte
                or ComparisonOperator.Lt or ComparisonOperator.Lte =>
                NullInclusive(RenderScalar(column, field, node.Op, RequireValue(node), context), node.Op, column),

            ComparisonOperator.Contains or ComparisonOperator.NotContains
                or ComparisonOperator.StartsWith or ComparisonOperator.EndsWith =>
                NullInclusive(RenderSubstring(column, field, node, context), node.Op, column),

            ComparisonOperator.In or ComparisonOperator.NotIn =>
                NullInclusive(RenderSet(column, field, node, context), node.Op, column),

            ComparisonOperator.InRange => RenderRange(column, field, node, context),

            _ => throw new SqlCompilationException($"Unknown comparison operator '{node.Op}'.")
        };
    }

    /// <summary>
    /// SEM-2/SQL-2: a negative operator's match set includes NULL-valued rows, and the resulting
    /// disjunction is ALWAYS wrapped — even when it is the whole <c>WHERE</c> clause. The
    /// unconditional wrap is what makes the leaf safe to drop into any parent without the parent
    /// knowing what it contains; a top-level <c>OR</c> left bare inside an <c>AND</c> parent is a
    /// silent correctness bug, and wrapping always removes the entire class.
    /// </summary>
    private static string NullInclusive(string predicate, ComparisonOperator op, string column) =>
        SemanticsResolver.IsNullInclusive(op) ? $"({predicate} OR {column} IS NULL)" : predicate;

    /// <summary>
    /// SEM-2: <c>blank</c> is <c>IS NULL OR = ''</c> for a text field and <c>IS NULL</c> for every
    /// other type. <c>notBlank</c> negates THIS text literally (giving the doubled parentheses in the
    /// string case, SQL-2) rather than re-deriving <c>IS NOT NULL AND &lt;&gt; ''</c> independently,
    /// so the two can never drift apart at the edges.
    /// </summary>
    private static string RenderBlank(string column, FieldConfig field) =>
        SemanticsResolver.ResolveBlankCondition(field.Type) switch
        {
            SemanticsResolver.BlankCondition.NullOrEmptyString => $"({column} IS NULL OR {column} = '')",
            SemanticsResolver.BlankCondition.NullOnly => $"{column} IS NULL",
            var condition => throw new SqlCompilationException($"Unknown blank condition '{condition}'.")
        };

    /// <summary>
    /// SEM-1: the resolver decides HOW the comparison treats case and, when it folds it, BY WHICH
    /// MECHANISM; the dialect only spells that decision. The three-valued answer is passed through
    /// intact — collapsing it to "insensitive or not" here is exactly the conflation that let a
    /// schema-declared case-SENSITIVE field render bare on an engine whose default collation folds
    /// case (see <see cref="SemanticsResolver.CaseSensitivityMode"/>).
    ///
    /// <para><paramref name="family"/> is the mechanism family the CALLER is about to render, and
    /// mismatching it is a compiler bug rather than a client error: an insensitive field whose
    /// operator resolves to <see cref="SemanticsResolver.CaseSensitivityMechanism.PatternMatch"/> must
    /// never reach the collation rendering, and vice versa. It is unreachable by construction — the
    /// scalar/set call sites only ever pass SEM-1's collation-family operators and the substring one
    /// only its pattern-family operators — so it fails loudly instead of silently degrading to "no
    /// mechanism", which would be a plausible-looking artifact that folds case in the wrong place.</para>
    /// </summary>
    private static SemanticsResolver.CaseSensitivityMode ResolveCaseSensitivity(
        FieldConfig field, ComparisonOperator op, SemanticsResolver.CaseSensitivityMechanism family)
    {
        var decision = SemanticsResolver.ResolveCaseSensitivity(field, op);

        if (decision.Mode == SemanticsResolver.CaseSensitivityMode.Insensitive && decision.Mechanism != family)
        {
            throw new SqlCompilationException(
                $"Operator '{op}' resolves to the '{decision.Mechanism}' case-insensitivity mechanism, but is being " +
                $"rendered through the '{family}' family — SEM-1 requires one mechanism per field across every context.");
        }

        return decision.Mode;
    }

    private static string RenderScalar(string column, FieldConfig field, ComparisonOperator op, TypedValue value, Context context)
    {
        var caseSensitivity = ResolveCaseSensitivity(field, op, SemanticsResolver.CaseSensitivityMechanism.Collation);

        return context.Dialect.RenderScalarComparison(
            column, ScalarComparison(op), EncodeValue(value, field), caseSensitivity, context.Parameters);
    }

    private static string RenderSubstring(string column, FieldConfig field, ComparisonNode node, Context context)
    {
        var caseSensitivity = ResolveCaseSensitivity(field, node.Op, SemanticsResolver.CaseSensitivityMechanism.PatternMatch);

        // There is no portable meaning for "does this number contain that substring", and guessing
        // one (casting the column to text) would silently change both semantics and index usage. The
        // DECLARED type is what decides that — checked first and separately from the value's variant,
        // because a caller that skipped SEM-5 coercion can hand a StringValue to an int64/date field,
        // and rendering it would produce a case-SENSITIVE substring match against a numeric column
        // (SemanticsResolver only resolves the pattern-match mechanism for a `string` field) — a
        // plausible-looking artifact that is exactly the guess this refuses to make. This is the one
        // operator path that never reaches EncodeValue, so it carries its own type check.
        if (field.Type != FieldType.String)
        {
            throw new SqlCompilationException(
                $"Field '{node.Field}' is declared '{field.Type}', but operator '{node.Op}' is a substring match " +
                "and only has a defined rendering against a 'string' field.");
        }

        if (RequireValue(node) is not StringValue pattern)
        {
            throw new SqlCompilationException(
                $"Operator '{node.Op}' on field '{node.Field}' is a substring match, so its value must be a " +
                "stringValue — run TypeCoercionValidator (SEM-5) before compiling.");
        }

        return context.Dialect.RenderSubstringMatch(
            column, SubstringAnchor(node.Op), negated: node.Op == ComparisonOperator.NotContains,
            pattern.Value, caseSensitivity, context.Parameters);
    }

    private static string RenderSet(string column, FieldConfig field, ComparisonNode node, Context context)
    {
        var caseSensitivity = ResolveCaseSensitivity(field, node.Op, SemanticsResolver.CaseSensitivityMechanism.Collation);

        var values = node.Values
            ?? throw new SqlCompilationException($"Operator '{node.Op}' on field '{node.Field}' requires 'values'.");

        var encoded = new List<object?>(values.Count);
        foreach (var value in values)
        {
            encoded.Add(EncodeValue(value, field));
        }

        return context.Dialect.RenderSetMembership(column, negated: node.Op == ComparisonOperator.NotIn, encoded, caseSensitivity, context.Parameters);
    }

    /// <summary>
    /// SEM-4: <c>inRange</c> compiles to the two bounds' own comparisons, each spelled from its
    /// EXPLICIT inclusivity flag — never a guessed default — composed with the same wrapped-composite
    /// shape SQL-2 gives an <c>and</c> node.
    /// </summary>
    private static string RenderRange(string column, FieldConfig field, ComparisonNode node, Context context)
    {
        if (node.From is null || node.To is null || node.FromInclusive is null || node.ToInclusive is null)
        {
            throw new SqlCompilationException(
                $"Operator 'inRange' on field '{node.Field}' requires 'from', 'to', 'fromInclusive' and 'toInclusive'.");
        }

        var lower = RenderScalar(column, field, node.FromInclusive.Value ? ComparisonOperator.Gte : ComparisonOperator.Gt, node.From, context);
        var upper = RenderScalar(column, field, node.ToInclusive.Value ? ComparisonOperator.Lte : ComparisonOperator.Lt, node.To, context);

        return $"({lower} AND {upper})";
    }

    private static TypedValue RequireValue(ComparisonNode node) =>
        node.Value ?? throw new SqlCompilationException($"Operator '{node.Op}' on field '{node.Field}' requires 'value'.");

    private static SqlScalarComparison ScalarComparison(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Eq => SqlScalarComparison.Equal,
        ComparisonOperator.Neq => SqlScalarComparison.NotEqual,
        ComparisonOperator.Gt => SqlScalarComparison.GreaterThan,
        ComparisonOperator.Gte => SqlScalarComparison.GreaterThanOrEqual,
        ComparisonOperator.Lt => SqlScalarComparison.LessThan,
        ComparisonOperator.Lte => SqlScalarComparison.LessThanOrEqual,
        _ => throw new SqlCompilationException($"Operator '{op}' is not a scalar comparison.")
    };

    private static SqlSubstringAnchor SubstringAnchor(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Contains or ComparisonOperator.NotContains => SqlSubstringAnchor.Anywhere,
        ComparisonOperator.StartsWith => SqlSubstringAnchor.Start,
        ComparisonOperator.EndsWith => SqlSubstringAnchor.End,
        _ => throw new SqlCompilationException($"Operator '{op}' is not a substring match.")
    };

    // ---- ORDER BY -------------------------------------------------------------------------------

    /// <summary>
    /// SEM-6/SEM-3: the request's own keys in order, then the schema's stable key as a trailing
    /// tiebreaker (unless the sort already ends in it), each key carrying its explicit
    /// <see cref="NullsPosition"/>. Never empty — the tiebreaker guarantees at least one key, so
    /// every compiled query has a total, reproducible order and server-side paging is stable.
    /// </summary>
    private static string RenderOrderBy(IReadOnlyList<SortItem>? sort, Context context)
    {
        var keys = SemanticsResolver.AppendStableKeyTiebreaker(sort, context.Schema.StableKey);

        var rendered = new List<string>(keys.Count);
        foreach (var key in keys)
        {
            var field = ResolveField(key.Field, context);

            // SEM-1: sorting always uses the collation mechanism when the field is case-insensitive
            // — never the pattern-match one — so a field cannot filter under one mechanism and sort
            // under another. The resolver already restricts this to `string` fields, and the mode is
            // passed through whole so a case-SENSITIVE key can be spelled as deliberately as an
            // insensitive one on an engine that needs it.
            var caseSensitivity = SemanticsResolver.ResolveSortOrGroupKeyCaseSensitivity(field).Mode;

            rendered.Add(context.Dialect.RenderSortKey(
                ResolveColumn(key.Field, context), key.Direction, key.NullsPosition, caseSensitivity));
        }

        return string.Join(", ", rendered);
    }

    // ---- paging ---------------------------------------------------------------------------------

    /// <summary>
    /// AG-3/EXP-2: resolves a <see cref="PageSpec"/> to the row window a paging clause renders —
    /// <see langword="null"/> for <see cref="NonePage"/> (no clause at all), otherwise the offset and
    /// the row count.
    ///
    /// <para>The subtraction is deliberately NOT a dialect's job. That
    /// <see cref="RangePage.EndRow"/> is <em>exclusive</em> is a semantic fact about the AG Grid
    /// request contract, not a spelling, and a dialect that bound <c>EndRow</c> instead would emit a
    /// perfectly plausible statement returning the wrong rows — the failure mode
    /// <see cref="ISqlDialect"/>'s "rendering only, never semantics" line exists to prevent, and one
    /// that would otherwise be re-typed into each of #1639's four dialects.</para>
    /// </summary>
    private static (int Offset, int Limit)? ResolveRowWindow(PageSpec page) => page switch
    {
        NonePage => null,
        RangePage range => (range.StartRow, range.EndRow - range.StartRow),
        _ => throw new SqlCompilationException($"Unknown PageSpec type '{page.GetType().Name}'.")
    };

    // ---- schema resolution ----------------------------------------------------------------------

    private static FieldConfig ResolveField(string field, Context context) =>
        context.Schema.Fields.TryGetValue(field, out var config)
            ? config
            : throw new SqlCompilationException(
                $"Field '{field}' is not declared in the GridSchema (SC-1/SC-2) — a compiler is reached only after " +
                "FieldAllowlistValidator, so this is a caller precondition failure, not a client error.");

    /// <summary>
    /// SC-4: the field's physical SQL column, quoted per SQL-1. A field with no
    /// <see cref="PhysicalMapping"/> at all falls back to its own colId, which is the identity
    /// mapping the schema is implicitly declaring by omitting one.
    /// </summary>
    private static string ResolveColumn(string field, Context context)
    {
        var mapping = ResolveField(field, context).Physical?.Sql;

        if (mapping is null)
        {
            return context.Dialect.QuoteIdentifier(field);
        }

        // #1638 deliberately implements only the plain `column` form: 752/752 field declarations in
        // spec/fixtures/ use it, so the other two SqlMapping shapes have no oracle at all and any
        // rendering chosen for them here would be unverifiable guesswork (does `qualifiedColumn`
        // quote both halves? does `expression` get parenthesised? does a collation attach to it?).
        // Rejecting cleanly keeps the gap visible until fixtures exist for it.
        if (mapping.QualifiedColumn is not null)
        {
            throw new SqlCompilationException(
                $"Field '{field}' declares physical.sql.qualifiedColumn, which this compiler does not implement " +
                "(#1638: no conformance fixture pins its rendering — see spec/fixtures/, where every field uses " +
                "the plain 'column' form).");
        }

        if (mapping.Expression is not null)
        {
            throw new SqlCompilationException(
                $"Field '{field}' declares physical.sql.expression, which this compiler does not implement " +
                "(#1638: no conformance fixture pins its rendering — see spec/fixtures/, where every field uses " +
                "the plain 'column' form).");
        }

        return context.Dialect.QuoteIdentifier(mapping.Column ?? field);
    }

    // ---- parameter encoding ---------------------------------------------------------------------

    /// <summary>
    /// AR-7/SEM-4: turns a <see cref="TypedValue"/> into the CLR value bound as a parameter. The one
    /// non-obvious case is <see cref="DecimalValue"/>, which stays a <see cref="string"/>: it and
    /// <see cref="DoubleValue"/> are the same wire shape and differ only by the schema's declared
    /// type, so binding a decimal through a floating type would be a silent precision loss no test
    /// output would show. <paramref name="field"/> is taken for that reason — the declared type, not
    /// the value's surface shape, is the authority — and is asserted against the value here rather
    /// than trusted, since a caller may have skipped <see cref="TypeCoercionValidator"/>.
    /// </summary>
    private static object? EncodeValue(TypedValue value, FieldConfig field)
    {
        RequireDeclaredType(value, field);

        return value switch
        {
            StringValue s => s.Value,
            Int64Value i => i.Value,
            DecimalValue d => d.Value,
            DoubleValue d => d.Value,
            BooleanValue b => b.Value,
            DateValue d => d.Value,
            DatetimeValue dt => NormalizeInstant(dt.Value),

            // AR-7's nullValue. No fixture pins a comparison against it (SEM-2's null handling goes
            // through blank/notBlank, which never carry a value), so it is bound as a NULL parameter
            // rather than rewritten into an IS NULL predicate — rewriting would be inventing a
            // semantics no spec text or oracle states.
            NullValue => null,

            _ => throw new SqlCompilationException($"Unknown TypedValue type '{value.GetType().Name}'.")
        };
    }

    /// <summary>
    /// SEM-4: a <c>datetime</c> parameter is the absolute instant, normalised to UTC with a trailing
    /// <c>Z</c>, and the <see cref="DatetimeValue.Zone"/> is NOT carried into the SQL or the
    /// parameter — it is provenance recording how the wire string was resolved, never an instruction
    /// to the database. Normalising rather than emitting the value verbatim is what stops the same
    /// instant written <c>+02:00</c> and written <c>Z</c> from producing two different artifacts.
    /// Sub-second precision is preserved when present, and dropped from the text when absent, so the
    /// common whole-second case round-trips to exactly the form the fixtures pin.
    ///
    /// <para><b>Load-bearing dependency:</b> this is safe from local-time drift only because
    /// <see cref="Build"/> runs <see cref="FilterNodeValidator"/> first, and its
    /// <c>DatetimePattern</c> mandates a trailing <c>Z</c> or an explicit offset. Without that
    /// guarantee <see cref="DateTimeOffset.TryParse(string, IFormatProvider, DateTimeStyles, out DateTimeOffset)"/>
    /// would assume the MACHINE's local zone for a zone-less string, and the same fixture would
    /// compile to a different instant on a different host.</para>
    /// </summary>
    private static string NormalizeInstant(string value)
    {
        // Zone-less input cannot reach here — FilterNodeValidator (run by Build) rejects it — which
        // is what makes the local-time assumption below unreachable rather than merely unlikely.
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant))
        {
            throw new SqlCompilationException(
                $"datetimeValue '{value}' is not an ISO-8601 instant (FilterNodeValidator should have rejected it upstream).");
        }

        var utc = instant.UtcDateTime;
        var format = utc.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd'T'HH:mm:ss'Z'" : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";
        return utc.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Defense in depth against a caller that skipped SEM-5 coercion: binding a value whose variant
    /// disagrees with the field's declared type is precisely the <c>decimal</c>-vs-<c>double</c>
    /// silent-divergence risk <see cref="EncodeValue"/> exists to prevent, so it fails loudly here
    /// instead of producing a plausible-looking artifact. <see cref="NullValue"/> is accepted against
    /// any declared type, matching <see cref="TypeCoercionValidator"/>'s own rule.
    /// </summary>
    private static void RequireDeclaredType(TypedValue value, FieldConfig field)
    {
        var matches = (value, field.Type) switch
        {
            (NullValue, _) => true,
            (StringValue, FieldType.String) => true,
            (Int64Value, FieldType.Int64) => true,
            (DecimalValue, FieldType.Decimal) => true,
            (DoubleValue, FieldType.Double) => true,
            (BooleanValue, FieldType.Boolean) => true,
            (DateValue, FieldType.Date) => true,
            (DatetimeValue, FieldType.Datetime) => true,
            _ => false
        };

        if (!matches)
        {
            throw new SqlCompilationException(
                $"A '{value.GetType().Name}' cannot be bound against a field declared '{field.Type}' — run " +
                "TypeCoercionValidator (SEM-5) before compiling.");
        }
    }
}
