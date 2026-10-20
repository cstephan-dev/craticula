using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Microsoft.EntityFrameworkCore;

namespace Craticula.EntityFrameworkCore;

/// <summary>
/// AR-4's exception, implemented: walks the same IR the SQL/Mongo/Search compilers walk and composes
/// it onto an <see cref="IQueryable{T}"/> as LINQ expression trees, so that <b>no SQL text is ever
/// produced by this project</b> and EF's own global query filters compose over the result.
///
/// <para><b>Why AR-4 grants the exception, restated because it is what shapes every method here.</b> A
/// compiled <c>{sql, params[]}</c> artifact is a finished statement: a host that must also apply
/// tenancy or soft-delete has to edit that text, which is the one thing SEC-2's parameterization
/// cannot protect. An <see cref="IQueryable{T}"/> composes instead — the host hands in
/// <c>context.Orders</c>, EF has already attached <c>HasQueryFilter</c> to it, and this adapter adds
/// <c>Where</c>/<c>OrderBy</c>/<c>Skip</c>/<c>Take</c> on top without ever seeing the filter it is
/// composing with. That is also why <see cref="EfCoreQueryRequest"/> does not read
/// <see cref="GridSchema.Source"/>: the <c>FROM</c> target is the caller's queryable, not a table name
/// this adapter resolves.</para>
///
/// <para><b>Semantics are never re-derived here — the same sentence
/// <c>Craticula.Sql.SqlQueryBuilder</c> opens with, and on this path it is the entire point.</b>
/// Case-sensitivity mechanism, null-inclusivity, the <c>blank</c> condition, SEM-3's default null
/// placement and SEM-6's tiebreaker all come from <see cref="SemanticsResolver"/>; SEM-2's tree-level
/// <c>NOT</c> fan-out order comes from <see cref="FilterNodeFields.DistinctInOrder"/>; SEC-4's pattern
/// escaping from <see cref="SqlPatternEscaping.Escape"/>; SEM-4's instant from
/// <see cref="IsoInstant.TryParseUtc"/>. The last three were the SQL compiler's own code until #1645
/// moved them into <c>Craticula.Core</c>, because "both paths MUST pass the same fixtures" is
/// unachievable if each path owns a copy of the rules: a divergence between two implementations of the
/// <c>NOT</c> fan-out, or of the <c>%</c> escape, shows up as a valid query returning the wrong number
/// of rows, with nothing in the shape of the output to notice.</para>
///
/// <para><b>What is genuinely this adapter's own.</b> Three things, and each has its reason recorded at
/// the method that does it. (1) SEM-3's null placement is <em>emulated</em> on every provider, because
/// LINQ has no <c>NULLS FIRST</c>/<c>NULLS LAST</c> — see <see cref="ApplyOrdering{TRow}(IQueryable{TRow}, EfCoreQueryRequest, EfCoreProviderSemantics)"/>. (2) SEM-2's
/// null-inclusive disjuncts are spelled explicitly even though EF's C#-semantics rewriter would add
/// most of them — see <see cref="NullInclusive"/>. (3) Values are bound through a wrapper object so EF
/// parameterizes them instead of inlining literals — see <see cref="Bind"/>. Everything else that
/// varies by engine arrives as data, in <see cref="EfCoreProviderSemantics"/>' five facts.</para>
///
/// <para><b>Scope (#1645): filters, sort, paging.</b> A request carrying a <see cref="GroupSpec"/> or
/// an <see cref="Aggregation"/> is refused by <see cref="ApplyFilter{TRow}"/>/
/// <see cref="ApplyOrdering{TRow}(IQueryable{TRow}, EfCoreQueryRequest, EfCoreProviderSemantics)"/> — the per-clause entry points, which still have no defined
/// meaning for group state on their own.</para>
///
/// <para><b>Grouping (#1887, BM-2's EF counterpart).</b> <see cref="EfCoreGridQuery.FetchPageAsync{TRow}"/>
/// dispatches on <see cref="GroupingPlan.Resolve"/> to a separate composition — see the "grouping"
/// region below — that builds a <c>GroupBy</c>/aggregate-selector expression tree via
/// <see cref="System.Linq.Expressions"/> and reflection, mirroring
/// <c>Craticula.Sql.SqlQueryBuilder</c>'s own BM-2 composition. No grouping RULE is re-derived here
/// either: GRP-1's dispatch, GRP-5's drill-down synthesis and the GRP-3/GRP-4 output-name guard all
/// come from <c>Craticula.Core</c>, exactly as they do for the SQL, Mongo and Search compilers.</para>
/// </summary>
public static class EfCoreQueryBuilder
{
    /// <summary>
    /// AR-5/AR-6/SEM-1/SEM-2: composes <paramref name="request"/>'s filter tree onto
    /// <paramref name="source"/> as a single <c>Where</c>. Returns <paramref name="source"/> unchanged
    /// when the request carries no filter — not <c>Where(_ =&gt; true)</c>, which would add a
    /// <c>WHERE 1</c> some providers then have to optimize away.
    ///
    /// <para>Re-runs the IR's own structural validators first, the defense-in-depth every public entry
    /// point in this codebase performs (<see cref="TypeCoercionValidator.Coerce"/>,
    /// <see cref="ResourceLimitsValidator.Validate"/>,
    /// <see cref="SemanticsResolver.AppendStableKeyTiebreaker(IReadOnlyList{SortItem}?, string)"/>,
    /// <c>SqlQueryBuilder.Build</c>), since a caller can reach this method with a hand-built tree that
    /// never went through <see cref="GridFilterPipeline"/>.</para>
    /// </summary>
    /// <exception cref="EfCoreCompilationException">Row-group state (out of scope, #1622), a field the
    /// schema does not declare, an unsupported <c>physical.sql</c> form, a value whose variant
    /// disagrees with the declared type, or one of the two shapes this adapter refuses rather than
    /// translating divergently (a <c>nullValue</c> comparison, a case-sensitive substring match on a
    /// case-folding provider).</exception>
    public static IQueryable<TRow> ApplyFilter<TRow>(
        IQueryable<TRow> source, EfCoreQueryRequest request, EfCoreProviderSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(source);
        var context = CreateValidatedContext(request, semantics, typeof(TRow));

        if (request.Filter is not { } filter)
        {
            return source;
        }

        FilterNodeValidator.Validate(filter);

        return source.Where(Expression.Lambda<Func<TRow, bool>>(BuildNode(filter, context), context.Row));
    }

    /// <summary>
    /// SEM-6/SEM-3/SEM-1: the request's own sort keys in order, then SEM-6's trailing
    /// <see cref="GridSchema.StableKey"/> tiebreaker unless the sort already ends in it. Never empty,
    /// so every query this adapter composes has a total, reproducible order — which the grid path needs
    /// for stable paging and the export path needs to chunk at all (see
    /// <see cref="EfCoreExportStream"/>).
    ///
    /// <para><b>SEM-3's null placement is emulated on every provider, and that is unavoidable rather
    /// than a choice.</b> LINQ has no way to say "nulls last" — <c>IOrderedQueryable</c> carries a key
    /// and a direction and nothing else — so each key becomes <b>two</b> keys: a leading boolean bucket
    /// <c>row.Key == null</c>, then the value itself. The bucket sorts ascending for
    /// <see cref="NullsPosition.Last"/> (<c>false</c> before <c>true</c>) and descending for
    /// <see cref="NullsPosition.First"/>, independently of the value key's own direction. Two of the
    /// five SQL dialects do exactly this for exactly this reason
    /// (<c>MssqlDialect</c>'s <c>CASE WHEN … IS NULL</c>, <c>MySqlDialect</c>'s <c>ISNULL(…)</c>), so
    /// the shape is not novel — what is new is that here it is unconditional, including on providers
    /// whose engine has the native syntax. Emitting the bucket even for a non-nullable column is
    /// deliberate: it costs a constant-false term the optimizer discards, whereas deciding per column
    /// would make the emitted order depend on EF model nullability metadata that the
    /// <see cref="GridSchema"/> does not carry — and SEM-3's rule is that null placement is always
    /// explicit, never the engine's default, "even where the two coincide".</para>
    ///
    /// <para>SEM-1 reaches the value key through
    /// <see cref="SemanticsResolver.ResolveOrderingAndGroupKeyCaseSensitivity"/>, so a
    /// case-insensitive <c>string</c> field orders under the same collation mechanism it filters
    /// under — and the appended tiebreaker resolves <see cref="SemanticsResolver.CaseSensitivityMode.NotApplicable"/>
    /// whatever its type, because it establishes row identity rather than comparing text (read that
    /// method's remarks for why a nondeterministic collation on the tiebreaker would reintroduce the
    /// very non-total order SEM-6 exists to remove).</para>
    /// </summary>
    public static IQueryable<TRow> ApplyOrdering<TRow>(
        IQueryable<TRow> source, EfCoreQueryRequest request, EfCoreProviderSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(source);
        var context = CreateValidatedContext(request, semantics, typeof(TRow));

        return ApplyOrdering(source, ResolveSortKeys(request), context);
    }

    internal static IQueryable<TRow> ApplyOrdering<TRow>(
        IQueryable<TRow> source, IReadOnlyList<SortKeyPlan> keys, RowContext context)
    {
        var ordered = source;
        var first = true;

        foreach (var key in keys)
        {
            var property = Property(context, key.Field);

            // The null bucket, ahead of its own value key. Its direction is the NULLS position's, not
            // the sort direction's: `nulls last` means "the true bucket comes second" whether the
            // values themselves ascend or descend.
            ordered = Order(
                ordered,
                Expression.Lambda(Expression.Equal(property, Expression.Constant(null, property.Type)), context.Row),
                descending: key.NullsPosition == NullsPosition.First,
                first: first);

            ordered = Order(
                ordered,
                Expression.Lambda(Collated(property, key.CaseSensitivity, context.Semantics), context.Row),
                descending: key.Direction == SortDirection.Desc,
                first: false);

            first = false;
        }

        return ordered;
    }

    /// <summary>
    /// AG-3/EXP-2: applies the request's row window — <c>Skip</c> then <c>Take</c> for a
    /// <see cref="RangePage"/>, nothing at all for <see cref="NonePage"/>.
    ///
    /// <para><b>This method is the grid path's, and only the grid path's.</b> EF compiles
    /// <see cref="Queryable.Skip"/> to <c>OFFSET</c> — measured, not assumed:
    /// <c>Skip(1).Take(2)</c> emits <c>LIMIT @p1 OFFSET @p</c> on SQLite — and EXP-3 forbids
    /// OFFSET-walking an export, because the engine must re-scan and discard every skipped row on
    /// every chunk, which turns a million-row download into a quadratic one. The separation is
    /// structural rather than advisory: <see cref="EfCoreExportStream"/> never calls this method, has
    /// no offset to pass it, and is pinned by a test that reads its own source for the token
    /// <c>Skip</c> and by another that asserts the generated SQL of a chunk contains no
    /// <c>OFFSET</c>.</para>
    ///
    /// <para>The subtraction is here rather than at a call site for the reason
    /// <c>SqlQueryBuilder.ResolveRowWindow</c> gives: that <see cref="RangePage.EndRow"/> is
    /// <em>exclusive</em> is a semantic fact about the AG Grid request contract, and a caller that
    /// passed <c>EndRow</c> to <c>Take</c> would return a perfectly plausible page of the wrong
    /// size.</para>
    /// </summary>
    public static IQueryable<TRow> ApplyRowWindow<TRow>(IQueryable<TRow> source, PageSpec page)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(page);
        PageSpecValidator.Validate(page);

        return page switch
        {
            NonePage => source,
            RangePage range => range.StartRow == 0
                // No Skip(0): it is a no-op semantically but EF still emits `OFFSET 0`, which would
                // make the first grid page's SQL differ from an export chunk's for no reason and blunt
                // the no-OFFSET assertion that guards the export path.
                ? source.Take(range.EndRow - range.StartRow)
                : source.Skip(range.StartRow).Take(range.EndRow - range.StartRow),
            _ => throw new EfCoreCompilationException($"Unknown PageSpec type '{page.GetType().Name}'.")
        };
    }

    /// <summary>
    /// Projects each row onto <paramref name="columns"/>, in that order, as an <c>object?[]</c> of
    /// native CLR values.
    ///
    /// <para><b>Why a projection at all, when the SQL compiler renders <c>SELECT *</c> for an ordinary
    /// row query (GRP-3).</b> Because <c>*</c> returns <em>physical</em> column names, and a caller
    /// needs the values keyed to LOGICAL colIds — a schema whose <c>customerName</c> lives in
    /// <c>customer_name</c> would otherwise hand the grid a key it does not know. The SQL path can
    /// defer that mapping to whoever reads the <c>DbDataReader</c>; this path has to make it, so it
    /// makes it once, here, and every consumer receives cells already in EXP-1(b) order. It also
    /// removes change tracking from the question entirely: a projection is not an entity, so nothing is
    /// tracked, no identity resolution collapses two rows with equal keys, and no
    /// <c>AsNoTracking()</c> call is needed to say so.</para>
    ///
    /// <para><b>#1653's native-CLR-values obligation is satisfied by construction here, not by
    /// mapping work.</b> EF materializes each cell as the CLR type its model declares — measured on
    /// SQLite as <see cref="long"/>, <see cref="double"/>, <see cref="decimal"/>,
    /// <see cref="bool"/>, <see cref="DateOnly"/>, <see cref="DateTime"/>, <see cref="string"/> — never
    /// a driver wrapper, because EF's value converters are what read the provider's own types. That is
    /// what <c>CsvWriter</c>'s EXP-5 formatting and EXP-6 hardening both dispatch on, and it is the one
    /// obligation an execution adapter carries that the writer cannot check for itself.</para>
    ///
    /// <para><paramref name="columns"/> may name a field more than once, and
    /// <see cref="EfCoreExportStream"/> relies on it: it appends its keyset columns after the export's
    /// own, so one projection serves both and a sort key that happens to also be an export column is
    /// simply read twice.</para>
    /// </summary>
    public static IQueryable<object?[]> ApplyProjection<TRow>(
        IQueryable<TRow> source, GridSchema schema, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0)
        {
            throw new EfCoreCompilationException(
                "A projection of no columns has no compiled form: a row of nothing is not a row. " +
                "EXP-1(b)'s 'omit exportColumns for the default projection' is the shape that means it.");
        }

        // No EfCoreProviderSemantics here, and that is a fact about a projection rather than an
        // oversight: reading a column is the one thing in this adapter no engine spells differently.
        // SEM-1's collations, SEM-2's null tests and SEM-7's patterns all live in predicates.
        var row = Expression.Parameter(typeof(TRow), "row");

        var cells = new Expression[columns.Count];
        for (var index = 0; index < columns.Count; index++)
        {
            // Converting to object is boxing EF performs client-side, after the columns have been
            // read; the server projection is still the bare column list.
            cells[index] = Expression.Convert(Property(row, schema, columns[index]), typeof(object));
        }

        return source.Select(Expression.Lambda<Func<TRow, object?[]>>(
            Expression.NewArrayInit(typeof(object), cells), row));
    }

    /// <summary>
    /// EXP-1(b): which columns a row carries — the request's explicit
    /// <see cref="EfCoreQueryRequest.Projection"/>, else every declared field in
    /// <see cref="GridSchema.FieldOrder"/> (GRP-3's declaration order, read out of the schema document
    /// rather than out of a dictionary's unspecified enumeration).
    ///
    /// <para>Note the deliberate difference from EXP-1(b)'s own default, which filters on
    /// <see cref="FieldConfig.Exportable"/>: that flag answers "which columns belong in a CSV file",
    /// which the grid adapter has already resolved into <see cref="EfCoreQueryRequest.Projection"/> by
    /// the time an export reaches here. The fallback below is what an ordinary <em>grid page</em> gets,
    /// and a grid renders whatever columns the user has on screen — a non-exportable field is still a
    /// visible one (<c>page-export/023</c> is the fixture that separates the two flags, and it filters
    /// on the field it refuses to export).</para>
    /// </summary>
    public static IReadOnlyList<string> ResolveProjection(EfCoreQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Projection is not { } projection)
        {
            return request.Schema.FieldOrder;
        }

        if (projection.Count == 0)
        {
            throw new EfCoreCompilationException(
                "A request carries an empty EXP-1(b) export projection. Leave Projection null for the " +
                "schema-order default; AgGridRequestParser rejects an empty 'exportColumns' at the wire.");
        }

        return projection;
    }

    // ---- request-level validation -----------------------------------------------------------------

    /// <summary>
    /// The preconditions every entry point shares, checked in one place so that none of them can be
    /// reached through a method that forgot one — and the <see cref="RowContext"/> the walk needs, built
    /// only after they hold. <see cref="EfCoreGridQuery"/> and <see cref="EfCoreExportStream"/> both go
    /// through it too, so a refusal below is reported once per request rather than once per clause.
    ///
    /// <para><b>Split in two since #1887</b> (<see cref="ValidateAndBuildRowContext"/> plus this
    /// method's own two refusals): the new grouped orchestration in <see cref="EfCoreGridQuery"/> is a
    /// LEGITIMATE caller with group state on the request, so it calls the validating half directly and
    /// skips these two throws. <see cref="ApplyFilter{TRow}"/>/<see cref="ApplyOrdering{TRow}(IQueryable{TRow}, EfCoreQueryRequest, EfCoreProviderSemantics)"/> keep
    /// calling this method unchanged — a caller that reaches the per-clause entry points with group
    /// state still gets the same refusal it always did, since nothing about those methods' own contract
    /// changed.</para>
    /// </summary>
    internal static RowContext CreateValidatedContext(
        EfCoreQueryRequest request, EfCoreProviderSemantics semantics, Type rowType)
    {
        var context = ValidateAndBuildRowContext(request, semantics, rowType);

        // Refused, never ignored — and refused HERE, before anything is composed, so a grouped request
        // cannot get half a query built for it. Silently dropping the group state would return the
        // ungrouped leaf rows: the right shape, the wrong answer, and nothing to notice.
        if (request.Group is not null)
        {
            throw new EfCoreCompilationException(
                "This request carries a GroupSpec (rowGroupCols/groupKeys), which this entry point does not " +
                "support — see #1622: it applies a single clause (filter/order/projection) and has no defined " +
                "meaning for 'restrict/order by group state' on its own. Since #1887, compile a grouped request " +
                "through EfCoreGridQuery.FetchPageAsync instead, which dispatches it to the grouped orchestration " +
                "(spec/semantics.md GRP-1..GRP-6: the grouped level's projection and aggregates, GRP-2's exact " +
                "bucket keys, GRP-4's tiebreaker substitution, AG-6's auto-column colId). Craticula.Sql compiles " +
                "the same request too; dropping the group state deliberately, as EXP-1's export carve-out does, " +
                "is the third option.");
        }

        if (request.Aggregations is { Count: > 0 })
        {
            throw new EfCoreCompilationException(
                "This request carries AR-8 aggregations (valueCols), which this entry point does not support — " +
                "it applies a single clause and has no defined meaning for an aggregate on its own. If the " +
                "request also carries a GroupSpec, compile it through EfCoreGridQuery.FetchPageAsync instead " +
                "(#1887's grouped orchestration). Checked independently of GroupSpec because AG Grid sends " +
                "valueCols without rowGroupCols too (pivot mode, or a user who added an aggregation before " +
                "grouping) — that shape has NO translation anywhere in this adapter yet (#1622: aggregating " +
                "without a group column), and returning leaf rows where the caller asked for totals would be a " +
                "wrong answer that looks healthy.");
        }

        return context;
    }

    /// <summary>
    /// The structural-validator half of <see cref="CreateValidatedContext"/>, split out for #1887's
    /// grouped orchestration: it needs the SAME defense-in-depth validation (a caller can reach it with
    /// a hand-built <see cref="EfCoreQueryRequest"/> that never went through a pipeline) and the SAME
    /// <see cref="RowContext"/>, but must NOT inherit the two refusals below — a grouped request is
    /// exactly what it is built to compile.
    /// </summary>
    internal static RowContext ValidateAndBuildRowContext(
        EfCoreQueryRequest request, EfCoreProviderSemantics semantics, Type rowType)
    {
        var row = Expression.Parameter(rowType, "row");
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(semantics);

        SortItemValidator.Validate(request.Sort);
        PageSpecValidator.Validate(request.Page);
        GroupSpecValidator.Validate(request.Group);
        AggregationValidator.Validate(request.Aggregations);

        return new RowContext(row, request.Schema, semantics);
    }

    // ---- filter tree ------------------------------------------------------------------------------

    /// <summary>
    /// The per-compilation state threaded through the tree walk. Carries the one
    /// <see cref="ParameterExpression"/> every property access must be rooted in — two parameters for
    /// the same row would build a tree EF cannot translate — plus the schema and the provider facts.
    ///
    /// <para><paramref name="IsGroupKeyMatch"/> mirrors <c>Craticula.Sql.SqlQueryBuilder.Context</c>'s
    /// field of the same name and purpose exactly (#1887): it carries GRP-2's PROVENANCE, not shape —
    /// a drill-down predicate and the user's own filter are both ordinary IR, and yet the two resolve
    /// case sensitivity by different rules (GRP-2's exact match for the former, SEM-1 for the latter).
    /// It is set once, by the caller composing the two halves (see
    /// <see cref="ApplyGroupedFilter{TRow}"/>), on the context the drill-down subtree is built under —
    /// never inferred from a node, for the identical reason <c>SqlQueryBuilder.RenderWhere</c> gives:
    /// the two are built as two separate walks precisely so nothing downstream has to tell them apart
    /// by inspecting a tree they have both been merged into.</para>
    /// </summary>
    internal readonly record struct RowContext(
        ParameterExpression Row,
        GridSchema Schema,
        EfCoreProviderSemantics Semantics,
        bool IsGroupKeyMatch = false);

    private static Expression BuildNode(FilterNode node, RowContext context) => node switch
    {
        AndNode and => BuildComposite(and, Expression.AndAlso, context),
        OrNode or => BuildComposite(or, Expression.OrElse, context),
        NotNode not => BuildNot(not, context),
        ComparisonNode comparison => BuildComparison(comparison, context),
        _ => throw new EfCoreCompilationException($"Unknown FilterNode type '{node.GetType().Name}'.")
    };

    /// <summary>
    /// An <c>and</c>/<c>or</c> node, folded left. No parenthesisation decision to make — SQL-2 exists
    /// because a compiler emitting text has to choose where the brackets go, and an expression tree
    /// carries the structure itself, which is one of the few places this path is simpler than the SQL
    /// one rather than merely different.
    /// </summary>
    private static Expression BuildComposite(
        CompositeFilterNode composite, Func<Expression, Expression, BinaryExpression> combine, RowContext context)
    {
        if (composite.Nodes.Count == 0)
        {
            // FilterNodeValidator rejects this upstream (ir.schema.json's minItems: 1). Checked anyway
            // because the alternative is worse than an exception in both directions: folding to `true`
            // widens the match set to every row and folding to `false` empties the grid, and either is
            // a silent answer to a malformed request.
            throw new EfCoreCompilationException(
                $"A '{composite.GetType().Name}' carries no child nodes; ir.schema.json requires at least one.");
        }

        var result = BuildNode(composite.Nodes[0], context);
        for (var index = 1; index < composite.Nodes.Count; index++)
        {
            result = combine(result, BuildNode(composite.Nodes[index], context));
        }

        return result;
    }

    /// <summary>
    /// SEM-2: a tree-level <c>not</c> is null-inclusive one syntax level up from the negative
    /// operators — the negated child, OR'd with one "is null" test per distinct field referenced
    /// anywhere in the child subtree, in <see cref="FilterNodeFields.DistinctInOrder"/>'s
    /// first-appearance order. Without them a backend's three-valued logic drops every NULL-valued row
    /// from the negation, which is the exact bug SEM-2 exists to prevent.
    ///
    /// <para><b>EF would add most of these disjuncts by itself, and they are spelled anyway.</b> EF's
    /// null-semantics rewriter implements C# comparison semantics, under which
    /// <c>!(row.Status == "archived")</c> is true for a null <c>Status</c> — so it emits
    /// <c>NOT (…) OR "status" IS NULL</c> unprompted, and the disjunct below is duplicated in the
    /// generated SQL (harmlessly: an idempotent term). It is spelled explicitly because that rewriter
    /// is <b>a configuration option</b>: <c>UseRelationalNulls(true)</c> turns it off, and a host that
    /// sets it — for the query-plan benefits it exists for — would otherwise silently lose SEM-2 on
    /// every negation in the application. A rule this project states normatively must not depend on a
    /// provider setting the host controls.</para>
    /// </summary>
    private static Expression BuildNot(NotNode not, RowContext context)
    {
        Expression negation = Expression.Not(BuildNode(not.Node, context));

        foreach (var field in FilterNodeFields.DistinctInOrder(not.Node))
        {
            var property = Property(context, field);
            negation = Expression.OrElse(negation, Expression.Equal(property, Expression.Constant(null, property.Type)));
        }

        return negation;
    }

    private static Expression BuildComparison(ComparisonNode node, RowContext context)
    {
        var field = ResolveField(node.Field, context);
        var property = Property(context, node.Field);

        return node.Op switch
        {
            ComparisonOperator.Blank => Blank(property, field, context),
            ComparisonOperator.NotBlank => Expression.Not(Blank(property, field, context)),

            // AR-6's unary boolean shorthand carries no value of its own, so this mints one rather
            // than emitting a literal — SEC-2 wants every compared value bound, and Bind is what makes
            // EF parameterize it.
            ComparisonOperator.True => Scalar(property, field, ComparisonOperator.Eq, new BooleanValue { Value = true }, context),
            ComparisonOperator.False => Scalar(property, field, ComparisonOperator.Eq, new BooleanValue { Value = false }, context),

            ComparisonOperator.Eq or ComparisonOperator.Neq
                or ComparisonOperator.Gt or ComparisonOperator.Gte
                or ComparisonOperator.Lt or ComparisonOperator.Lte =>
                NullInclusive(Scalar(property, field, node.Op, RequireValue(node), context), node.Op, property),

            ComparisonOperator.Contains or ComparisonOperator.NotContains
                or ComparisonOperator.StartsWith or ComparisonOperator.EndsWith =>
                NullInclusive(Substring(property, field, node, context), node.Op, property),

            // SEM-8: an empty list is a constant that already covers NULL rows, so no null disjunct.
            ComparisonOperator.In or ComparisonOperator.NotIn when node.Values is { Count: 0 } =>
                Expression.Constant(node.Op == ComparisonOperator.NotIn),

            ComparisonOperator.In or ComparisonOperator.NotIn =>
                NullInclusive(SetMembership(property, field, node, context), node.Op, property),

            ComparisonOperator.InRange => Range(property, field, node, context),

            _ => throw new EfCoreCompilationException($"Unknown comparison operator '{node.Op}'.")
        };
    }

    /// <summary>
    /// SEM-2: a negative operator's match set includes NULL-valued rows — <c>neq</c>,
    /// <c>notContains</c>, <c>notIn</c>, as decided by <see cref="SemanticsResolver.IsNullInclusive"/>
    /// and nowhere else.
    ///
    /// <para>Spelled for the reason <see cref="BuildNot"/> records at length: EF's C#-semantics
    /// rewriter already produces the disjunct for <c>!=</c> and for <c>NOT LIKE</c>, and
    /// <c>UseRelationalNulls(true)</c> removes it. The one operator where EF would <em>not</em> have
    /// added it is <c>notIn</c> over a parameterized list, since EF cannot know at translation time
    /// whether the list contains a null — so this is load-bearing rather than merely defensive even at
    /// EF's default settings.</para>
    /// </summary>
    private static Expression NullInclusive(Expression predicate, ComparisonOperator op, Expression property) =>
        SemanticsResolver.IsNullInclusive(op)
            ? Expression.OrElse(predicate, Expression.Equal(property, Expression.Constant(null, property.Type)))
            : predicate;

    /// <summary>
    /// SEM-2: <c>blank</c> is "null or empty string" for a text field and "null" for every other type.
    /// <c>notBlank</c> negates THIS expression (see <see cref="BuildComparison"/>) rather than
    /// re-deriving "not null and not empty" independently, so the two cannot drift apart at the edges.
    ///
    /// <para>SQL-7: on an engine that treats <c>''</c> as NULL the text arm collapses onto the non-text
    /// one, because the <c>= ''</c> disjunct is not merely redundant there but UNKNOWN for every row —
    /// which leaves <c>notBlank</c> matching nothing at all. That single engine fact arrives as
    /// <see cref="EfCoreProviderSemantics.EmptyStringIsNull"/>, under the same name and for the same
    /// reason <c>ISqlDialect</c> carries it; the composition stays here.</para>
    /// </summary>
    private static Expression Blank(Expression property, FieldConfig field, RowContext context)
    {
        var isNull = Expression.Equal(property, Expression.Constant(null, property.Type));

        return SemanticsResolver.ResolveBlankCondition(field.Type) switch
        {
            SemanticsResolver.BlankCondition.NullOrEmptyString when !context.Semantics.EmptyStringIsNull =>
                Expression.OrElse(isNull, Expression.Equal(property, Bind(string.Empty, property.Type))),
            SemanticsResolver.BlankCondition.NullOrEmptyString or SemanticsResolver.BlankCondition.NullOnly => isNull,
            var condition => throw new EfCoreCompilationException($"Unknown blank condition '{condition}'.")
        };
    }

    /// <summary>
    /// SEM-1's exact-match/ordering family. <see cref="Expression.Equal(Expression, Expression)"/> and
    /// friends are used in their two-argument form, whose <c>liftToNull</c> is <see langword="false"/>,
    /// so a comparison of two <see cref="Nullable{T}"/> operands has type <see cref="bool"/> rather
    /// than <c>bool?</c> and can be composed into a predicate directly.
    ///
    /// <para><see cref="bool"/> has no relational operators, so a <c>gt</c>/<c>lt</c> on a
    /// <see cref="FieldType.Boolean"/> field cannot be built at all — <see cref="Expression.GreaterThan(Expression, Expression)"/>
    /// throws for it. That is left as the exception it is rather than caught and re-thrown: no wire
    /// shape produces it (AG Grid's boolean columns send a set filter, which AR-6 turns into the unary
    /// <c>true</c>/<c>false</c>), so a message here would document a path nothing reaches.</para>
    /// </summary>
    private static Expression Scalar(
        Expression property, FieldConfig field, ComparisonOperator op, TypedValue value, RowContext context)
    {
        var mode = ResolveCaseSensitivity(field, op, SemanticsResolver.CaseSensitivityMechanism.Collation, context);
        var left = Collated(property, mode, context.Semantics);
        var right = Bind(EncodeValue(value, field, property.Type), left.Type);

        return op switch
        {
            ComparisonOperator.Eq => Expression.Equal(left, right),
            ComparisonOperator.Neq => Expression.NotEqual(left, right),
            ComparisonOperator.Gt => Compare(left, right, Expression.GreaterThan),
            ComparisonOperator.Gte => Compare(left, right, Expression.GreaterThanOrEqual),
            ComparisonOperator.Lt => Compare(left, right, Expression.LessThan),
            ComparisonOperator.Lte => Compare(left, right, Expression.LessThanOrEqual),
            _ => throw new EfCoreCompilationException($"Operator '{op}' is not a scalar comparison.")
        };
    }

    /// <summary>
    /// An ordering comparison, with the one type that needs a different spelling handled here rather
    /// than at four call sites: C# defines no relational operators for <see cref="string"/>, so
    /// <c>Expression.GreaterThan</c> on two string operands throws. <c>string.Compare(a, b) &gt; 0</c>
    /// is the form EF translates back into the engine's own <c>a &gt; b</c> — verified on SQLite,
    /// including with a collated left operand, which is what a case-insensitive <c>gt</c> on a text
    /// field needs.
    /// </summary>
    internal static Expression Compare(
        Expression left, Expression right, Func<Expression, Expression, BinaryExpression> comparison) =>
        left.Type == typeof(string)
            ? comparison(Expression.Call(StringCompareMethod, left, right), Expression.Constant(0))
            : comparison(left, right);

    /// <summary>
    /// SEM-7's substring family, realized with <c>EF.Functions.Like</c> over a pattern this method
    /// builds: SEC-4's escaping (<see cref="SqlPatternEscaping.Escape"/>, plus T-SQL's <c>[</c> where
    /// the provider says it is a metacharacter) then the anchor's <c>%</c> wildcards.
    ///
    /// <para>The declared type is checked FIRST, and separately from the value's variant, exactly as
    /// <c>SqlQueryBuilder.RenderSubstring</c> does and for the same reason: there is no portable
    /// meaning for "does this number contain that substring", and a caller that skipped SEM-5 coercion
    /// can hand a <see cref="StringValue"/> to an <c>int64</c> field. Since
    /// <see cref="SemanticsResolver"/> resolves the pattern-match mechanism only for a <c>string</c>
    /// field, translating it anyway would produce a case-SENSITIVE match against a numeric column — a
    /// query that runs, and the guess this refuses to make.</para>
    ///
    /// <para><b>The case-sensitive direction is refused on a provider whose <c>LIKE</c> folds case, and
    /// that refusal is the considered answer rather than a gap.</b> On such an engine (SQLite; MySQL,
    /// depending on the column's collation) <c>LIKE</c> cannot express a case-sensitive match at all,
    /// and neither can the two obvious LINQ alternatives: <c>string.StartsWith</c>/<c>EndsWith</c>
    /// compile to <c>LIKE</c> as well (measured — <c>LIKE @p_startswith</c>), and while
    /// <c>string.Contains</c> happens to compile to a case-sensitive <c>instr</c> on SQLite, using it
    /// would make <c>contains</c> honour SC-3 while <c>startsWith</c> silently did not, which is worse
    /// than a uniform refusal. <c>SqliteDialect</c> reaches for <c>GLOB</c> here — a different pattern
    /// language with different wildcards and no <c>ESCAPE</c> clause — and <c>EF.Functions.Glob</c>
    /// exists in <c>Microsoft.EntityFrameworkCore.Sqlite</c>, a provider package AR-3 keeps out of this
    /// one. The remaining option, folding case anyway, is SEM-1's second Must-Not: returning rows the
    /// host's <c>caseSensitivity: "sensitive"</c> declaration excluded, on well-formed SQL, invisibly.
    /// One fixture in the suite is in this population (<c>dialect-parity/021</c>), and a host that needs
    /// it declares the column's collation in DDL — the same host obligation GRP-2 already imposes on
    /// PostgreSQL and MySQL — after which the field is <c>sensitive</c> by storage and this method's
    /// insensitive arm is the one that needs a mechanism.</para>
    /// </summary>
    private static Expression Substring(
        Expression property, FieldConfig field, ComparisonNode node, RowContext context)
    {
        if (field.Type != FieldType.String)
        {
            throw new EfCoreCompilationException(
                $"Field '{node.Field}' is declared '{field.Type}', but operator '{node.Op}' is a substring match " +
                "and only has a defined translation against a 'string' field.");
        }

        if (RequireValue(node) is not StringValue value)
        {
            throw new EfCoreCompilationException(
                $"Operator '{node.Op}' on field '{node.Field}' is a substring match, so its value must be a " +
                "stringValue — run TypeCoercionValidator (SEM-5) before compiling.");
        }

        var mode = ResolveCaseSensitivity(field, node.Op, SemanticsResolver.CaseSensitivityMechanism.PatternMatch, context);

        if (mode == SemanticsResolver.CaseSensitivityMode.Sensitive && context.Semantics.PatternMatchFoldsCase)
        {
            throw new EfCoreCompilationException(
                $"Field '{node.Field}' is declared caseSensitivity: 'sensitive' (SC-3) and operator '{node.Op}' is a " +
                "substring match, but this provider's LIKE folds case unconditionally " +
                "(EfCoreProviderSemantics.PatternMatchFoldsCase), so no provider-independent EF translation can " +
                "honour the declaration. Translating it anyway would silently return rows the schema excludes " +
                "(SEM-1). Compile this request with Craticula.Sql instead: its dialects reach for the " +
                "engine-specific operator EF has no provider-independent equivalent of (GLOB on SQLite, a " +
                "case-sensitive collation elsewhere), and spec/fixtures/dialect-parity/021 is that path's " +
                "oracle. Note that a column collation is NOT a fix on every engine — SQLite's LIKE ignores " +
                "collating sequences entirely, so only the connection-wide PRAGMA case_sensitive_like would " +
                "move it, and that would break the insensitive direction for every other field. Where a " +
                "collation IS the lever (MySQL), the provider fact " +
                "EfCoreProviderSemantics.PatternMatchFoldsCase must be set false to match, since it describes " +
                "the provider rather than the column.");
        }

        // SQL-5's '[' pass is NOT re-implemented here. Both this adapter and MssqlDialect route through
        // the one Core helper, because an unescaped '[' silently WIDENS a match set — wrong rows, no
        // exception — and a second copy of that decision is precisely what moving SqlPatternEscaping
        // into Craticula.Core was meant to prevent. The composition order the two passes require is
        // stated once, there.
        var escaped = context.Semantics.PatternMatchTreatsLeftBracketAsMetacharacter
            ? SqlPatternEscaping.EscapeIncludingLeftBracket(value.Value, PatternEscapeCharacter)
            : SqlPatternEscaping.Escape(value.Value, PatternEscapeCharacter);

        var pattern = node.Op switch
        {
            ComparisonOperator.Contains or ComparisonOperator.NotContains => $"%{escaped}%",
            ComparisonOperator.StartsWith => $"{escaped}%",
            ComparisonOperator.EndsWith => $"%{escaped}",
            _ => throw new EfCoreCompilationException($"Operator '{node.Op}' is not a substring match.")
        };

        // SEM-1's insensitive direction on a provider whose LIKE does not fold: lower() BOTH sides.
        // The pattern is lowered here, in C#, because it is a value this compiler owns; only the column
        // needs the engine's own lower(). See EfCoreProviderSemantics.PatternMatchFoldsCase for why the
        // portable mechanism is lower() rather than a collation on the operand.
        var fold = mode == SemanticsResolver.CaseSensitivityMode.Insensitive && !context.Semantics.PatternMatchFoldsCase;
        var matchExpression = fold ? Expression.Call(property, StringToLowerMethod) : property;

        if (mode == SemanticsResolver.CaseSensitivityMode.Sensitive)
        {
            // Only reached when the engine's own default folds case (SQL Server) — otherwise
            // CaseSensitiveCollation is null and plain LIKE already distinguishes case.
            matchExpression = Collated(matchExpression, mode, context.Semantics);
        }

        var like = Expression.Call(
            LikeMethod,
            Expression.Constant(EF.Functions),
            matchExpression,
            Bind(fold ? pattern.ToLowerInvariant() : pattern, typeof(string)),
            Expression.Constant(PatternEscapeCharacter.ToString()));

        // SQL-3's "render the negated operator itself" has no analogue to choose here: EF turns
        // Not(Like(...)) into NOT LIKE on its own, so there is one spelling.
        return node.Op == ComparisonOperator.NotContains ? Expression.Not(like) : like;
    }

    /// <summary>
    /// SQL-4/SEM-1 set membership: a bound <see cref="List{T}"/> of the field's CLR type, with
    /// <c>Contains</c> called on it against the (possibly collated) column — the form EF compiles to
    /// <c>column IN (@p1, @p2, …)</c>, N scalar parameters, which is what four of the five SQL dialects
    /// emit and semantically identical to PostgreSQL's single-array <c>= ANY($1)</c>.
    ///
    /// <para>The collation goes on the COLUMN and not on the list, which is not a style choice: SQLite
    /// resolves <c>x IN (…)</c> from <c>x</c>'s collating sequence alone and silently ignores one
    /// attached to a list element. <c>SqliteDialect</c> records the same asymmetry from the other side.
    /// Column-side works on every engine, so this adapter needs the asymmetry as neither a fact nor a
    /// branch.</para>
    /// </summary>
    private static Expression SetMembership(
        Expression property, FieldConfig field, ComparisonNode node, RowContext context)
    {
        var values = node.Values
            ?? throw new EfCoreCompilationException($"Operator '{node.Op}' on field '{node.Field}' requires 'values'.");

        var mode = ResolveCaseSensitivity(field, node.Op, SemanticsResolver.CaseSensitivityMechanism.Collation, context);
        var left = Collated(property, mode, context.Semantics);

        var listType = typeof(List<>).MakeGenericType(left.Type);
        var list = (System.Collections.IList)Activator.CreateInstance(listType, values.Count)!;
        foreach (var value in values)
        {
            list.Add(EncodeValue(value, field, property.Type));
        }

        var membership = Expression.Call(
            Bind(list, listType), listType.GetMethod(nameof(List<object>.Contains))!, left);

        return node.Op == ComparisonOperator.NotIn ? Expression.Not(membership) : membership;
    }

    /// <summary>
    /// SEM-4: <c>inRange</c> becomes the two bounds' own comparisons, each spelled from its EXPLICIT
    /// inclusivity flag — never a guessed default, and never one flag applied to both ends.
    /// </summary>
    private static Expression Range(
        Expression property, FieldConfig field, ComparisonNode node, RowContext context)
    {
        if (node.From is null || node.To is null || node.FromInclusive is null || node.ToInclusive is null)
        {
            throw new EfCoreCompilationException(
                $"Operator 'inRange' on field '{node.Field}' requires 'from', 'to', 'fromInclusive' and 'toInclusive'.");
        }

        return Expression.AndAlso(
            Scalar(property, field, node.FromInclusive.Value ? ComparisonOperator.Gte : ComparisonOperator.Gt, node.From, context),
            Scalar(property, field, node.ToInclusive.Value ? ComparisonOperator.Lte : ComparisonOperator.Lt, node.To, context));
    }

    private static TypedValue RequireValue(ComparisonNode node) =>
        node.Value ?? throw new EfCoreCompilationException(
            $"Operator '{node.Op}' on field '{node.Field}' requires 'value'.");

    // ---- SEM-1 ------------------------------------------------------------------------------------

    /// <summary>
    /// SEM-1: the resolver decides HOW a comparison treats case and, when it folds it, by which
    /// MECHANISM; this adapter only realizes that decision. The three-valued answer is passed through
    /// intact — collapsing it to "insensitive or not" is the conflation that lets a schema-declared
    /// case-SENSITIVE field compare bare on an engine whose default collation folds case.
    ///
    /// <para><paramref name="family"/> is the mechanism family the CALLER is about to realize, and a
    /// mismatch is a bug in this file rather than a client error — unreachable by construction, since
    /// the scalar/set call sites only pass collation-family operators and the substring one only
    /// pattern-family operators. It fails loudly instead of degrading to "no mechanism", which would be
    /// a query that folds case in the wrong place.</para>
    ///
    /// <para><b>#1887: a predicate inside the synthesised drill-down is not a filter at all</b> — it is
    /// a group-key MATCH, and GRP-2 requires it to agree with the <c>GROUP BY</c> that produced the
    /// bucket label being expanded, exactly the reasoning
    /// <c>Craticula.Sql.SqlQueryBuilder.ResolveCaseSensitivity</c> gives for the identical branch. It
    /// therefore resolves through <see cref="SemanticsResolver.ResolveOrderingAndGroupKeyCaseSensitivity"/>
    /// instead, and the provenance comes from <see cref="RowContext.IsGroupKeyMatch"/>, which
    /// <see cref="ApplyGroupedFilter{TRow}"/> sets on the walk it starts over the drill-down — never
    /// inferred from the node itself.</para>
    /// </summary>
    private static SemanticsResolver.CaseSensitivityMode ResolveCaseSensitivity(
        FieldConfig field, ComparisonOperator op, SemanticsResolver.CaseSensitivityMechanism family, RowContext context)
    {
        if (context.IsGroupKeyMatch)
        {
            return SemanticsResolver
                .ResolveOrderingAndGroupKeyCaseSensitivity(field, SemanticsResolver.GroupingMode.Grouped)
                .Mode;
        }

        var decision = SemanticsResolver.ResolveCaseSensitivity(field, op);

        if (decision.Mode == SemanticsResolver.CaseSensitivityMode.Insensitive && decision.Mechanism != family)
        {
            throw new EfCoreCompilationException(
                $"Operator '{op}' resolves to the '{decision.Mechanism}' case-insensitivity mechanism, but is being " +
                $"translated through the '{family}' family — SEM-1 requires one mechanism per field across every context.");
        }

        return decision.Mode;
    }

    /// <summary>
    /// Wraps <paramref name="expression"/> in <c>EF.Functions.Collate</c> when SEM-1's decision needs a
    /// collation mechanism AND the provider names one — and returns it untouched otherwise, which
    /// covers three distinct cases that all render nothing: a non-text comparison
    /// (<see cref="SemanticsResolver.CaseSensitivityMode.NotApplicable"/>, where a collation on an
    /// <c>int</c> column is a hard error on several engines), a direction the engine's own default
    /// collation already implements (<see langword="null"/> in
    /// <see cref="EfCoreProviderSemantics"/>), and MySQL, where a query spells neither direction
    /// because the column's declared collation decides.
    /// </summary>
    internal static Expression Collated(
        Expression expression, SemanticsResolver.CaseSensitivityMode mode, EfCoreProviderSemantics semantics)
    {
        var collation = mode switch
        {
            SemanticsResolver.CaseSensitivityMode.Insensitive => semantics.CaseInsensitiveCollation,
            SemanticsResolver.CaseSensitivityMode.Sensitive => semantics.CaseSensitiveCollation,
            _ => null
        };

        return collation is null
            ? expression
            : Expression.Call(
                CollateMethod.MakeGenericMethod(expression.Type),
                Expression.Constant(EF.Functions),
                expression,
                Expression.Constant(collation));
    }

    // ---- sort keys --------------------------------------------------------------------------------

    /// <summary>
    /// One resolved <c>ORDER BY</c> key: the field, its direction and explicit null placement, and
    /// SEM-1's already-made case decision for it. Shared with <see cref="EfCoreExportStream"/>, whose
    /// keyset predicate must reproduce the ordering's comparison exactly — including its collation, or
    /// a chunk boundary would compare rows differently from the sort that placed them and the walk
    /// would skip or repeat rows.
    /// </summary>
    internal sealed record SortKeyPlan(
        string Field,
        SortDirection Direction,
        NullsPosition NullsPosition,
        SemanticsResolver.CaseSensitivityMode CaseSensitivity);

    /// <summary>
    /// SEM-6/SEM-1: the request's keys plus the appended stable-key tiebreaker, each carrying the case
    /// decision its provenance resolves to. The provenance distinction is not recoverable from the
    /// returned list (an appended tiebreaker and a user key on the same field are the identical
    /// <see cref="SortItem"/>), which is why
    /// <see cref="SemanticsResolver.AppendStableKeyTiebreaker(IReadOnlyList{SortItem}?, string, out bool)"/>
    /// reports it and this method does not re-derive it by comparing the last field to the stable key.
    /// </summary>
    internal static IReadOnlyList<SortKeyPlan> ResolveSortKeys(EfCoreQueryRequest request)
    {
        var keys = SemanticsResolver.AppendStableKeyTiebreaker(
            request.Sort, request.Schema, groupColumn: null, out var tiebreakerAppended);

        var plans = new List<SortKeyPlan>(keys.Count);
        for (var index = 0; index < keys.Count; index++)
        {
            var key = keys[index];
            var provenance = tiebreakerAppended && index == keys.Count - 1
                ? SemanticsResolver.SortKeyProvenance.AppendedTiebreaker
                : SemanticsResolver.SortKeyProvenance.Request;

            // GroupingMode.Ungrouped always: this method is reused unchanged for the drill-down leaf
            // (#1887), which has no GROUP BY and so keeps SEM-6's stable-key tiebreaker — GRP-4's
            // substitution of the group column for it is the GROUPED LEVEL's own rule, resolved by
            // ApplyGroupedOrdering instead (GroupingMode.Grouped), never by this method.
            var mode = SemanticsResolver.ResolveOrderingAndGroupKeyCaseSensitivity(
                ResolveFieldConfig(key.Field, request.Schema),
                SemanticsResolver.GroupingMode.Ungrouped,
                provenance).Mode;

            plans.Add(new SortKeyPlan(key.Field, key.Direction, key.NullsPosition, mode));
        }

        return plans;
    }

    private static IQueryable<TRow> Order<TRow>(
        IQueryable<TRow> source, LambdaExpression key, bool descending, bool first) =>
        (IQueryable<TRow>)ApplyOrderingStep(source, typeof(TRow), key, descending, first);

    /// <summary>
    /// The reflection dispatch <see cref="Order{TRow}"/> performs, generalised over an untyped
    /// <paramref name="source"/> and its own <paramref name="sourceType"/> — #1887's grouped-level
    /// ordering (<see cref="ApplyGroupedOrdering{TRow}"/>) needs the SAME four
    /// <see cref="Queryable"/> methods over <c>IGrouping&lt;TKey,TRow&gt;</c>, a type resolved at
    /// runtime from the group column's declared <see cref="FieldType"/> rather than known at compile
    /// time, so it cannot go through the generic <c>TRow</c> parameter
    /// <see cref="Order{TRow}"/> itself is typed on.
    /// </summary>
    private static object ApplyOrderingStep(object source, Type sourceType, LambdaExpression key, bool descending, bool first)
    {
        var name = (descending, first) switch
        {
            (false, true) => nameof(Queryable.OrderBy),
            (false, false) => nameof(Queryable.ThenBy),
            (true, true) => nameof(Queryable.OrderByDescending),
            (true, false) => nameof(Queryable.ThenByDescending)
        };

        // Reflection because the key's type varies per field and the Queryable methods are generic in
        // it; there is no non-generic overload to call. `source` is an IOrderedQueryable<sourceType> at
        // runtime whenever `first` is false, which is what makes the ThenBy arms legal.
        var method = OrderingMethods[name].MakeGenericMethod(sourceType, key.ReturnType);
        return method.Invoke(null, [source, key])!;
    }

    private static readonly Dictionary<string, MethodInfo> OrderingMethods =
        new[] { nameof(Queryable.OrderBy), nameof(Queryable.OrderByDescending), nameof(Queryable.ThenBy), nameof(Queryable.ThenByDescending) }
            .ToDictionary(
                name => name,
                name => typeof(Queryable).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(method => method.Name == name && method.GetParameters().Length == 2),
                StringComparer.Ordinal);

    // ---- grouping (#1887) -------------------------------------------------------------------------

    /// <summary>
    /// GRP-5: composes the drill-down predicate (already-consumed <c>groupKeys</c>, GRP-5's own IR)
    /// with the request's own filter — the identical composition
    /// <c>Craticula.Sql.SqlQueryBuilder.RenderWhere</c> performs, ported to expression trees. Used by
    /// both the drill-down leaf and the grouped level (GRP-5's "the grouped level's own DrillDown is
    /// the ALREADY-consumed keys").
    ///
    /// <para><b>The two halves are built as two SEPARATE walks, each under its own
    /// <see cref="RowContext"/>, and joined with <see cref="Expression.AndAlso(Expression, Expression)"/>
    /// — never merged into one <see cref="FilterNode"/> via <see cref="FilterTreeMerge.And"/> first.</b>
    /// <c>Craticula.Sql</c> does not do that either, and for the reason recorded there: the two halves
    /// resolve case sensitivity by different rules (GRP-2 for the drill-down, SEM-1 for the user's own
    /// predicate), and the only thing that could tell a group-key match from a user predicate once both
    /// are ordinary IR walked uniformly is the node's reference identity — a non-local invariant with no
    /// guard. Rendering each half under its own <see cref="RowContext.IsGroupKeyMatch"/> removes the
    /// invariant instead of documenting it.</para>
    ///
    /// <para>Built in this order — drill-down first — not merely joined in it, mirroring
    /// <c>RenderWhere</c>'s own comment: there is no parameter bag here for the order to matter to, but
    /// the order is kept anyway for consistency with the other three compilers' emitted text/pipeline.</para>
    /// </summary>
    internal static IQueryable<TRow> ApplyGroupedFilter<TRow>(
        IQueryable<TRow> source, FilterNode? drillDown, FilterNode? filter, RowContext context)
    {
        ArgumentNullException.ThrowIfNull(source);

        Expression? groupPath = drillDown is null ? null : BuildNode(drillDown, context with { IsGroupKeyMatch = true });

        Expression? userFilter = null;
        if (filter is { } userFilterNode)
        {
            FilterNodeValidator.Validate(userFilterNode);
            userFilter = BuildNode(userFilterNode, context);
        }

        var predicate = (groupPath, userFilter) switch
        {
            (null, null) => (Expression?)null,
            ({ } path, null) => path,
            (null, { } user) => user,
            ({ } path, { } user) => Expression.AndAlso(path, user)
        };

        return predicate is null ? source : source.Where(Expression.Lambda<Func<TRow, bool>>(predicate, context.Row));
    }

    /// <summary>
    /// GRP-1's grouped level: <c>GroupBy</c> on <paramref name="groupColumn"/>, one aggregate selector
    /// per <paramref name="aggregations"/> (AGG-1's non-null-count spelling for <c>count</c>), the
    /// level's own <c>ORDER BY</c> (GRP-4) resolved and applied over the grouped sequence BEFORE the
    /// final projection, and the flat <c>object?[]</c> shape every other projection in this file
    /// produces — <c>[groupColumn, ...aggregates]</c>, so <see cref="ApplyRowWindow{TRow}"/> (with
    /// <c>TRow = object?[]</c>) serves this level unchanged.
    ///
    /// <para><b>Why every step below is built via <see cref="System.Linq.Expressions"/> and reflection,
    /// mirroring <see cref="Order{TRow}"/>/<see cref="OrderingMethods"/>.</b> The group key's CLR type —
    /// and therefore the concrete <c>IGrouping&lt;TKey,TRow&gt;</c> this level's rows become — is only
    /// known once <paramref name="groupColumn"/>'s declared <see cref="FieldType"/> is resolved, at
    /// runtime; there is no <typeparamref name="TRow"/>-only generic signature that could express it.
    /// So the <c>GroupBy</c>/<c>OrderBy</c>/<c>Select</c> composition below carries the intermediate
    /// grouped query as <see cref="object"/> and dispatches each step through
    /// <see cref="MethodInfo.MakeGenericMethod(Type[])"/> + <c>MethodInfo.Invoke</c> — the same
    /// pattern <see cref="Order{TRow}"/> already uses for a per-field key type, one level up.</para>
    /// </summary>
    internal static IQueryable<object?[]> ApplyGroupedLevel<TRow>(
        IQueryable<TRow> source, string groupColumn, IReadOnlyList<Aggregation> aggregations,
        IReadOnlyList<SortItem>? sort, RowContext context)
    {
        ArgumentNullException.ThrowIfNull(source);

        var grouped = BuildGroupedQuery<TRow>(
            source, groupColumn, context, out var groupingType, out var groupParam, out var keyExpression);

        var sortKeys = SemanticsResolver.AppendStableKeyTiebreaker(sort, groupColumn, out var tiebreakerAppended);
        grouped = ApplyGroupedOrdering<TRow>(
            grouped, groupingType, groupParam, keyExpression, groupColumn, aggregations, sortKeys, tiebreakerAppended, context);

        var cells = new Expression[1 + aggregations.Count];
        cells[0] = Expression.Convert(keyExpression, typeof(object));
        for (var i = 0; i < aggregations.Count; i++)
        {
            cells[i + 1] = Expression.Convert(BuildAggregateExpression<TRow>(groupParam, aggregations[i], context), typeof(object));
        }

        var projectionSelector = Expression.Lambda(Expression.NewArrayInit(typeof(object), cells), groupParam);
        var projected = QueryableSelectMethod.MakeGenericMethod(groupingType, typeof(object[]))
            .Invoke(null, [grouped, projectionSelector])!;

        return (IQueryable<object?[]>)projected;
    }

    /// <summary>
    /// The grouped level's row count — the bucket count, not a row count, mirroring
    /// <c>Craticula.Sql.SqlQueryBuilder</c>'s "GRP-6(e)-style" reading of "how many rows this query
    /// answers" one level up: for a grouped level that is "how many buckets", so this counts the
    /// SAME <c>GroupBy</c> query <see cref="ApplyGroupedLevel{TRow}"/> builds, with no aggregate
    /// selectors needed to answer it.
    /// </summary>
    internal static Task<long> CountGroupsAsync<TRow>(
        IQueryable<TRow> source, string groupColumn, RowContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var grouped = BuildGroupedQuery<TRow>(source, groupColumn, context, out var groupingType, out _, out _);
        return (Task<long>)LongCountAsyncMethod.MakeGenericMethod(groupingType).Invoke(null, [grouped, cancellationToken])!;
    }

    /// <summary>
    /// Builds <c>source.GroupBy(row =&gt; EF.Property&lt;TKey&gt;(row, physicalColumn))</c> — GRP-2's
    /// key, which never folds case (no <see cref="Collated"/> call here, deliberately: a <c>GROUP BY</c>
    /// key is one of the protocol's own bucket-identity keys). Returns the grouped query as
    /// <see cref="object"/> (its true runtime shape is <c>IQueryable&lt;IGrouping&lt;TKey,TRow&gt;&gt;</c>
    /// for a <paramref name="groupingType"/> resolved here), plus the pieces every caller needs to keep
    /// building on top of it without re-deriving them: the grouping's own element type, the
    /// <see cref="ParameterExpression"/> a later lambda over one group binds to, and that parameter's
    /// <c>.Key</c> access already built once.
    /// </summary>
    private static object BuildGroupedQuery<TRow>(
        IQueryable<TRow> source, string groupColumn, RowContext context,
        out Type groupingType, out ParameterExpression groupParam, out Expression keyExpression)
    {
        var keyType = ClrTypeFor(ResolveFieldConfig(groupColumn, context.Schema).Type);
        var keySelector = Expression.Lambda(Property(context, groupColumn), context.Row);

        var grouped = QueryableGroupByMethod.MakeGenericMethod(typeof(TRow), keyType).Invoke(null, [source, keySelector])!;

        groupingType = typeof(IGrouping<,>).MakeGenericType(keyType, typeof(TRow));
        groupParam = Expression.Parameter(groupingType, "g");
        keyExpression = Expression.Property(groupParam, groupingType.GetProperty(nameof(IGrouping<object, object>.Key))!);

        return grouped;
    }

    /// <summary>
    /// GRP-4: the grouped level's own <c>ORDER BY</c>, built over the grouped sequence
    /// (<c>IGrouping&lt;TKey,TRow&gt;</c>) — BEFORE the final <c>Select</c> to <c>object?[]</c> —
    /// exactly mirroring <c>Craticula.Sql.SqlQueryBuilder.RenderOrderBy</c>'s grouped branch: each key
    /// resolves to either the group column itself or one of <paramref name="aggregations"/>'s own
    /// expressions (never a raw aggregated column, and never a reference back to an output alias — see
    /// <see cref="BuildAggregateExpression{TRow}"/>, built fresh here rather than referred to by name,
    /// which is one code path instead of two that could drift), through the SAME
    /// <see cref="GroupedSortTarget"/> shared by the SQL and Mongo compilers. SEM-6's appended
    /// tiebreaker is minted from the GROUP COLUMN, not the schema's <see cref="GridSchema.StableKey"/>
    /// (GRP-4) — the caller (<see cref="ApplyGroupedLevel{TRow}"/>) mints it that way BEFORE calling
    /// here, so <paramref name="sort"/> already carries it and <paramref name="tiebreakerAppended"/>
    /// only reports whether the trailing key is that minted one.
    /// </summary>
    private static object ApplyGroupedOrdering<TRow>(
        object grouped, Type groupingType, ParameterExpression groupParam, Expression keyExpression,
        string groupColumn, IReadOnlyList<Aggregation> aggregations,
        IReadOnlyList<SortItem> sort, bool tiebreakerAppended, RowContext context)
    {
        var ordered = grouped;
        var first = true;

        for (var i = 0; i < sort.Count; i++)
        {
            var item = sort[i];
            var (target, field) = ResolveGroupedSortTarget<TRow>(item.Field, groupColumn, aggregations, keyExpression, groupParam, context);

            var provenance = tiebreakerAppended && i == sort.Count - 1
                ? SemanticsResolver.SortKeyProvenance.AppendedTiebreaker
                : SemanticsResolver.SortKeyProvenance.Request;

            var caseSensitivity = SemanticsResolver
                .ResolveOrderingAndGroupKeyCaseSensitivity(field, SemanticsResolver.GroupingMode.Grouped, provenance)
                .Mode;

            // SEM-3's null bucket, ahead of the value key itself — unconditional, exactly as
            // ApplyOrdering<TRow> emits it for the ungrouped/leaf path, even on an aggregate (like
            // `count`, wrapped nullable below) that can in practice never BE null.
            ordered = ApplyOrderingStep(
                ordered, groupingType,
                Expression.Lambda(Expression.Equal(target, Expression.Constant(null, target.Type)), groupParam),
                descending: item.NullsPosition == NullsPosition.First,
                first: first);

            ordered = ApplyOrderingStep(
                ordered, groupingType,
                Expression.Lambda(Collated(target, caseSensitivity, context.Semantics), groupParam),
                descending: item.Direction == SortDirection.Desc,
                first: false);

            first = false;
        }

        return ordered;
    }

    /// <summary>
    /// AR-9/GRP-4: what a grouped level's sort key actually orders by — the group column or one of
    /// <paramref name="aggregations"/>, matched by <see cref="Aggregation.ColumnId"/> through the
    /// shared <see cref="GroupedSortTarget"/> (never <see cref="Aggregation.OutputName"/> — see that
    /// type's own remarks for why the two diverge and why a first-match pick is #1886's bug, not a
    /// simplification). Mirrors <c>Craticula.Sql.SqlQueryBuilder.ResolveGroupedSortTarget</c> exactly,
    /// including its two rejections (an ambiguous column id, and a column id that is ALSO the group
    /// column's own name) — this adapter inherits both from the identical shared helper rather than
    /// re-deriving either.
    /// </summary>
    private static (Expression Target, FieldConfig Field) ResolveGroupedSortTarget<TRow>(
        string sortField, string groupColumn, IReadOnlyList<Aggregation> aggregations,
        Expression keyExpression, ParameterExpression groupParam, RowContext context)
    {
        var matches = GroupedSortTarget.MatchingByColumnId(sortField, aggregations);

        if (matches.Count > 1)
        {
            throw new EfCoreCompilationException(
                $"Ambiguous ORDER BY target for a grouped level. {GroupedSortTarget.DescribeAmbiguity(sortField, matches)}");
        }

        if (GroupedSortTarget.MatchesGroupColumnToo(sortField, groupColumn, matches))
        {
            throw new EfCoreCompilationException(
                $"Ambiguous ORDER BY target for a grouped level. {GroupedSortTarget.DescribeGroupColumnAmbiguity(sortField, matches)}");
        }

        if (matches.Count == 1)
        {
            return (BuildAggregateExpression<TRow>(groupParam, matches[0], context), ResolveFieldConfig(matches[0].Field, context.Schema));
        }

        if (sortField == groupColumn)
        {
            return (keyExpression, ResolveFieldConfig(sortField, context.Schema));
        }

        throw new EfCoreCompilationException(
            $"A grouped query can be ordered only by its group column ('{groupColumn}') or by one of its " +
            $"aggregations, but the request sorts on '{sortField}', which the grouped projection does not contain.");
    }

    /// <summary>
    /// One aggregate selector, e.g. <c>g.Sum(row =&gt; EF.Property&lt;decimal?&gt;(row, "amount"))</c> —
    /// built as an <see cref="Expression.Call(MethodInfo, Expression[])"/> NODE (never
    /// <c>MethodInfo.Invoke</c>: unlike <see cref="BuildGroupedQuery{TRow}"/>'s <c>GroupBy</c> or
    /// <see cref="ApplyGroupedOrdering{TRow}"/>'s <c>OrderBy</c>, this call is not a top-level query
    /// composition step against a real <see cref="IQueryable{T}"/> — it is a SUB-expression inside the
    /// body of a larger lambda (the level's projection, or one of its sort keys) that EF's own query
    /// translator evaluates once that outer lambda is materialised).
    ///
    /// <para><b>AGG-1's trap, and why it is spelled rather than <c>g.Count()</c>.</b> A bare row count
    /// answers a different question the moment the counted field can be <see langword="null"/> — it
    /// counts every row in the bucket regardless of the field. The non-null-count form
    /// (<c>g.Count(row =&gt; EF.Property&lt;T&gt;(row, field) != null)</c>) is <see cref="Enumerable.Count{TSource}(IEnumerable{TSource}, Func{TSource, bool})"/>'s
    /// PREDICATE overload, matching <c>Craticula.Sql</c>'s <c>COUNT(&lt;field&gt;)</c> and
    /// <c>Craticula.Mongo</c>'s conditional <c>$sum</c> — the identical rule realised through this
    /// backend's own mechanism.</para>
    ///
    /// <para><b>Kept nullable even where an aggregate can never actually be null</b> (<c>count</c>'s
    /// result, converted to <c>int?</c>) — the uniform, "even where inert" convention
    /// <see cref="ApplyOrdering{TRow}(IQueryable{TRow}, EfCoreQueryRequest, EfCoreProviderSemantics)"/>'s own null bucket already follows, so
    /// <see cref="ApplyGroupedOrdering{TRow}"/> can build the SAME null-bucket comparison over every
    /// aggregate without a second, nullability-conditional code path.</para>
    ///
    /// <para><b>AGG-6 (sum over an all-null group is 0, avg/min/max are null) needs no code here at
    /// all.</b> EF's own translation of the nullable <c>Sum</c>/<c>Average</c>/<c>Min</c>/<c>Max</c>
    /// overloads already answers it (measured, not assumed — see
    /// <c>EfCoreGroupingTests.An_All_Null_Group_Sums_To_Zero_And_Averages_Mins_And_Maxes_To_Null</c>),
    /// unlike <c>Craticula.Sql</c>, whose bare <c>SUM</c> disagrees with the ratified rule and needs
    /// <c>AggregateCall</c>'s <c>COALESCE</c> wrap.</para>
    /// </summary>
    private static Expression BuildAggregateExpression<TRow>(
        ParameterExpression groupParam, Aggregation aggregation, RowContext context)
    {
        var field = ResolveFieldConfig(aggregation.Field, context.Schema);
        var elementRow = Expression.Parameter(typeof(TRow), "row");
        var property = Property(elementRow, context.Schema, aggregation.Field);

        if (aggregation.Func == AggregationFunc.Count)
        {
            var predicate = Expression.Lambda(
                Expression.NotEqual(property, Expression.Constant(null, property.Type)), elementRow);
            var call = Expression.Call(EnumerableCountMethod.MakeGenericMethod(typeof(TRow)), groupParam, predicate);

            return Expression.Convert(call, typeof(int?));
        }

        var selector = Expression.Lambda(property, elementRow);

        if (aggregation.Func is AggregationFunc.Min or AggregationFunc.Max)
        {
            var minOrMaxMethod = (aggregation.Func == AggregationFunc.Min ? EnumerableMinMethod : EnumerableMaxMethod)
                .MakeGenericMethod(typeof(TRow), property.Type);

            return Expression.Call(minOrMaxMethod, groupParam, selector);
        }

        // Sum/Avg have no defined translation outside the three numeric FieldTypes: Enumerable.Sum/
        // Average are overloaded PER concrete numeric type rather than generic in it (unlike Min/Max),
        // so a non-numeric field has no overload to resolve at all — refused here with a message that
        // names the reason, rather than surfacing as a bare reflection lookup failure.
        if (field.Type is not (FieldType.Int64 or FieldType.Decimal or FieldType.Double))
        {
            throw new EfCoreCompilationException(
                $"Aggregation '{aggregation.Func}' on field '{aggregation.Field}' (declared '{field.Type}') has no " +
                "defined LINQ translation: 'sum'/'avg' require a numeric field.");
        }

        var name = aggregation.Func == AggregationFunc.Sum ? nameof(Enumerable.Sum) : nameof(Enumerable.Average);
        var method = ResolveSumOrAverageMethod(name, property.Type).MakeGenericMethod(typeof(TRow));

        return Expression.Call(method, groupParam, selector);
    }

    /// <summary>
    /// Resolves <c>Enumerable.Sum</c>/<c>Enumerable.Average</c>'s generic method DEFINITION whose
    /// selector returns exactly <paramref name="selectorReturnType"/> (<c>long?</c>, <c>decimal?</c> or
    /// <c>double?</c> — the three <see cref="ClrTypeFor"/> ever hands a numeric field). Unlike
    /// <c>Min</c>/<c>Max</c>, which are generic in their result type, <c>Sum</c>/<c>Average</c> are a
    /// family of overloads each fixed to one concrete numeric type, so the lookup is BY that type
    /// rather than a single cached <see cref="MethodInfo"/>.
    /// </summary>
    private static MethodInfo ResolveSumOrAverageMethod(string name, Type selectorReturnType) =>
        typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == name
                && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 1
                && method.GetParameters().Length == 2
                && method.GetParameters()[1].ParameterType.IsGenericType
                && method.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(Func<,>)
                && method.GetParameters()[1].ParameterType.GetGenericArguments()[1] == selectorReturnType);

    private static MethodInfo ResolveMinOrMaxMethodDefinition(string name) =>
        typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == name
                && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 2
                && method.GetParameters().Length == 2
                && method.GetParameters()[1].ParameterType.IsGenericType
                && method.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(Func<,>));

    private static readonly MethodInfo EnumerableMinMethod = ResolveMinOrMaxMethodDefinition(nameof(Enumerable.Min));

    private static readonly MethodInfo EnumerableMaxMethod = ResolveMinOrMaxMethodDefinition(nameof(Enumerable.Max));

    private static readonly MethodInfo EnumerableCountMethod = typeof(Enumerable)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(Enumerable.Count) && method.GetParameters().Length == 2);

    private static readonly MethodInfo QueryableGroupByMethod = typeof(Queryable)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(Queryable.GroupBy)
            && method.GetGenericArguments().Length == 2
            && method.GetParameters().Length == 2);

    private static readonly MethodInfo QueryableSelectMethod = typeof(Queryable)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(Queryable.Select)
            && method.GetGenericArguments().Length == 2
            && method.GetParameters().Length == 2
            && method.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);

    private static readonly MethodInfo LongCountAsyncMethod = typeof(EntityFrameworkQueryableExtensions)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(EntityFrameworkQueryableExtensions.LongCountAsync)
            && method.GetParameters().Length == 2
            && method.GetParameters()[1].ParameterType == typeof(CancellationToken));

    // ---- schema resolution ------------------------------------------------------------------------

    private static FieldConfig ResolveField(string field, RowContext context) =>
        ResolveFieldConfig(field, context.Schema);

    internal static FieldConfig ResolveFieldConfig(string field, GridSchema schema) =>
        schema.Fields.TryGetValue(field, out var config)
            ? config
            : throw new EfCoreCompilationException(
                $"Field '{field}' is not declared in the GridSchema (SC-1/SC-2) — this adapter is reached only after " +
                "FieldAllowlistValidator, so this is a caller precondition failure, not a client error.");

    /// <summary>
    /// The typed <c>EF.Property&lt;T&gt;(row, name)</c> access for a logical field.
    ///
    /// <para><b>SC-4: the EF property name is the field's physical SQL column</b>
    /// (<c>physical.sql.column</c>), falling back to the colId — that is, byte-for-byte the same string
    /// <c>SqlQueryBuilder</c> quotes into its <c>SELECT</c> and <c>WHERE</c>. It has to be: AR-4's
    /// promise is that both paths return the same rows from the same database, and the SQL path names
    /// the column. <b>The host obligation this creates is real and worth stating plainly:</b> the EF
    /// model must expose each field under that name, because EF resolves
    /// <c>EF.Property</c> against MODEL property names, not against column names. That is automatic for
    /// a property-bag model (<see cref="GridSchemaPropertyBag"/> declares the indexer properties from
    /// the same string) and automatic for entity classes under EF's default convention, where a
    /// property maps to a column of its own name. A class that renames its columns with
    /// <c>HasColumnName</c> must either declare no <c>physical.sql</c> mapping — so the colId is used,
    /// and the colId is then the property name — or align the two. The failure mode is loud: EF throws
    /// "the property … was not found" at translation time rather than reading the wrong column.</para>
    ///
    /// <para>The other two <c>SqlMapping</c> forms are refused, as they are by every other compiler in
    /// this repo: <c>qualifiedColumn</c> is a join reference EF expresses as a navigation rather than a
    /// name, <c>expression</c> is raw SQL that has no expression-tree form at all, and every one of the
    /// suite's field declarations uses the plain <c>column</c> form, so neither has an oracle to check a
    /// guess against.</para>
    /// </summary>
    internal static Expression Property(RowContext context, string field) =>
        Property(context.Row, context.Schema, field);

    internal static Expression Property(ParameterExpression rowParameter, GridSchema schema, string field)
    {
        var config = ResolveFieldConfig(field, schema);
        var mapping = config.Physical?.Sql;

        if (mapping?.QualifiedColumn is not null)
        {
            throw new EfCoreCompilationException(
                $"Field '{field}' declares physical.sql.qualifiedColumn, which this adapter does not implement: a " +
                "qualified column names another table, which EF reaches through a navigation property rather than " +
                "through a name (and no conformance fixture pins either rendering — every field in spec/fixtures/ " +
                "uses the plain 'column' form).");
        }

        if (mapping?.Expression is not null)
        {
            throw new EfCoreCompilationException(
                $"Field '{field}' declares physical.sql.expression, which this adapter cannot implement at all: raw " +
                "SQL text has no LINQ expression-tree equivalent. A host needing a computed field maps it in the EF " +
                "model instead (a computed column or a value converter), where EF can translate it.");
        }

        // EF.Property's first parameter is `object`; a reference-typed row needs no conversion node
        // (fewer nodes for EF's own visitors to see through), a value-typed one would not be
        // assignable without a boxing convert. No EF entity type is a value type, so the second arm is
        // unreachable in practice and present only to keep Expression.Call from throwing on a caller
        // who proves otherwise.
        Expression row = rowParameter.Type.IsValueType
            ? Expression.Convert(rowParameter, typeof(object))
            : rowParameter;

        return Expression.Call(
            EfPropertyMethod.MakeGenericMethod(ClrTypeFor(config.Type)),
            row,
            Expression.Constant(mapping?.Column ?? field));
    }

    /// <summary>
    /// AR-7: the CLR type this adapter asks EF for, per declared <see cref="FieldType"/> — and
    /// therefore the type a host's own EF model must expose, which is why it is public and why
    /// <see cref="GridSchemaPropertyBag"/> declares its indexer properties from this same method.
    ///
    /// <para><b>Every value type is <see cref="Nullable{T}"/>, including the stable key's.</b> Not for
    /// storage reasons — a column may well be <c>NOT NULL</c> — but because SEM-2 and SEM-3 both need
    /// "is this cell null" to be <em>expressible</em>: <c>row.Amount == null</c> does not compile
    /// against a bare <see cref="long"/>, and it is the leading key of every ordering this adapter
    /// emits (see <see cref="ApplyOrdering{TRow}(IQueryable{TRow}, EfCoreQueryRequest, EfCoreProviderSemantics)"/>) as well as every null-inclusive disjunct SEM-2
    /// requires. Asking for <c>long?</c> where the model declares <c>long</c> is harmless: EF resolves
    /// <c>EF.Property</c> by name and the generic argument only shapes the C# expression.</para>
    ///
    /// <para><c>decimal</c> is a <see cref="decimal"/> here and a numeric <see cref="string"/> in
    /// <c>SqlQuery.Parameters</c>, and the difference is not a divergence: AR-7 carries a decimal as a
    /// string so it survives JSON round-tripping into an artifact, whereas EF hands a real
    /// <see cref="decimal"/> to a provider that knows the column's type. Both preserve scale; only one
    /// of them has to serialize.</para>
    /// </summary>
    public static Type ClrTypeFor(FieldType type) => type switch
    {
        FieldType.String => typeof(string),
        FieldType.Int64 => typeof(long?),
        FieldType.Decimal => typeof(decimal?),
        FieldType.Double => typeof(double?),
        FieldType.Boolean => typeof(bool?),
        FieldType.Date => typeof(DateOnly?),
        FieldType.Datetime => typeof(DateTime?),
        _ => throw new EfCoreCompilationException($"Unknown FieldType '{type}'.")
    };

    // ---- value binding ---------------------------------------------------------------------------

    /// <summary>
    /// AR-7/SEM-4: turns a <see cref="TypedValue"/> into the CLR value bound as a parameter, typed from
    /// the schema's DECLARED type rather than from the value's surface shape — asserted here rather
    /// than trusted, since a caller may have skipped <see cref="TypeCoercionValidator"/>.
    ///
    /// <para><b>A <c>nullValue</c> in a comparison is refused, and this is the one place #1645 chose an
    /// exception over matching the SQL compiler's behaviour.</b> That compiler binds it as a NULL
    /// parameter, rendering <c>"col" = $1</c> — which under three-valued logic matches no row — and
    /// says it declines to rewrite that into <c>IS NULL</c> because no spec text or oracle states such
    /// a semantics. This path cannot make the same choice: EF collapses a comparison against a null
    /// parameter to <c>IS NULL</c> (measured — even when the null arrives boxed as a parameter it
    /// cannot see the value of), so the two paths would answer the same request with "no rows" and
    /// "the null rows" respectively, silently, on every fixture that never covers it. Refusing names
    /// the divergence at the one moment someone can act on it, and costs nothing: no fixture carries
    /// this shape, and SEM-2's <c>blank</c>/<c>notBlank</c> is the operator for null.</para>
    /// </summary>
    private static object? EncodeValue(TypedValue value, FieldConfig field, Type clrType)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value is NullValue)
        {
            throw new EfCoreCompilationException(
                "A 'nullValue' cannot be compared on the EF Core path. Craticula.Sql binds it as a NULL parameter, " +
                "which matches no row under SQL's three-valued logic; EF compiles a comparison against a null " +
                "parameter to IS NULL, which matches exactly the null rows. Rather than pick one and have the two " +
                "paths disagree silently (AR-4), this is refused: use the 'blank'/'notBlank' operators, which are " +
                "SEM-2's defined null handling.");
        }

        RequireDeclaredType(value, field);

        return value switch
        {
            StringValue s => s.Value,
            Int64Value i => i.Value,
            DoubleValue d => d.Value,
            BooleanValue b => b.Value,

            // AR-7 carries a decimal as a numeric string precisely so nothing rounds it on the way in;
            // parsed with the invariant culture, never the machine's, or a host in a comma-decimal
            // locale would compile the same fixture to a different number.
            DecimalValue d => decimal.TryParse(d.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new EfCoreCompilationException(
                    $"decimalValue '{d.Value}' is not a decimal number (FilterNodeValidator should have rejected it upstream)."),

            DateValue d => DateOnly.TryParseExact(d.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : throw new EfCoreCompilationException(
                    $"dateValue '{d.Value}' is not an ISO-8601 calendar date (FilterNodeValidator should have rejected it upstream)."),

            // SEM-4: the instant, in UTC, read by the one helper the SQL compiler reads it with — the
            // Zone is provenance and never reaches a backend. Only the RENDERING differs between the
            // two paths (a canonical '...Z' string there, a DateTime here).
            DatetimeValue dt => IsoInstant.TryParseUtc(dt.Value, out var instant)
                ? instant
                : throw new EfCoreCompilationException(
                    $"datetimeValue '{dt.Value}' is not an ISO-8601 instant (FilterNodeValidator should have rejected it upstream)."),

            _ => throw new EfCoreCompilationException($"Unknown TypedValue type '{value.GetType().Name}'.")
        };
    }

    /// <summary>
    /// Defense in depth against a caller that skipped SEM-5 coercion. Binding a value whose variant
    /// disagrees with the declared type is the <c>decimal</c>-vs-<c>double</c> silent-precision risk
    /// AR-7 exists to prevent, and on this path it has a second consequence the SQL path does not have:
    /// EF resolves <c>EF.Property</c> by NAME and ignores the generic argument, so a mismatched type
    /// would not be caught by EF either — it would translate, run, and compare a column against a
    /// coerced literal of the provider's choosing.
    /// </summary>
    private static void RequireDeclaredType(TypedValue value, FieldConfig field)
    {
        var matches = (value, field.Type) switch
        {
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
            throw new EfCoreCompilationException(
                $"A '{value.GetType().Name}' cannot be bound against a field declared '{field.Type}' — run " +
                "TypeCoercionValidator (SEM-5) before compiling.");
        }
    }

    /// <summary>
    /// SEC-2: wraps a value so EF binds it as a <b>parameter</b> rather than inlining it as a literal.
    ///
    /// <para><b>Why the wrapper is necessary, measured.</b> EF's parameter extraction turns every
    /// evaluatable non-constant subtree of a query into a parameter and leaves a
    /// <see cref="ConstantExpression"/> as a literal. So <c>Expression.Constant(5L)</c> emits
    /// <c>WHERE "qty" &gt; 5</c> while a member access over a constant box emits
    /// <c>WHERE "qty" &gt; @p</c>. Both are injection-safe — EF's literal generator escapes — but only
    /// the second one is what AR-4's own first sentence asks for and what SEC-2 states: every compared
    /// value bound. The difference is also operational rather than cosmetic, and in the direction that
    /// hurts most under load: a literal is part of the query text, so <em>every distinct filter value a
    /// user types</em> produces a new statement for the engine to parse and plan, and defeats EF's own
    /// compiled-query cache.</para>
    ///
    /// <para>The box is a fresh object per call while the expression tree's SHAPE is identical across
    /// calls, which is exactly the arrangement EF's plan cache wants: one cache entry, one parameter
    /// per value.</para>
    /// </summary>
    internal static Expression Bind(object? value, Type clrType)
    {
        var boxType = typeof(BoundValue<>).MakeGenericType(clrType);
        var box = Activator.CreateInstance(boxType, [value])!;

        return Expression.Property(Expression.Constant(box, boxType), nameof(BoundValue<object>.Value));
    }

    /// <summary>The box of <see cref="Bind"/>. A class rather than a record or a tuple so the member
    /// name EF derives its parameter name from is stable and readable in a captured query log.</summary>
    private sealed class BoundValue<T>(T value)
    {
        public T Value { get; } = value;
    }

    // ---- method handles ---------------------------------------------------------------------------

    /// <summary>SEC-4's escape character. All five <c>Craticula.Sql</c> dialects name <c>\</c>, so
    /// there is no per-provider axis here — and EF renders the <c>ESCAPE</c> clause itself, through the
    /// provider's own literal generator, which is what makes MySQL's doubled <c>'\\'</c> (SQL-5) EF's
    /// problem rather than this adapter's.</summary>
    private const char PatternEscapeCharacter = '\\';

    private static readonly MethodInfo EfPropertyMethod =
        typeof(EF).GetMethod(nameof(EF.Property), BindingFlags.Public | BindingFlags.Static)!;

    private static readonly MethodInfo CollateMethod =
        typeof(RelationalDbFunctionsExtensions).GetMethod(nameof(RelationalDbFunctionsExtensions.Collate))!;

    private static readonly MethodInfo LikeMethod = typeof(DbFunctionsExtensions)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(DbFunctionsExtensions.Like) && method.GetParameters().Length == 4);

    private static readonly MethodInfo StringCompareMethod =
        typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;

    private static readonly MethodInfo StringToLowerMethod =
        typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
}
