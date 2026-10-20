using Craticula.Core.Ir;

namespace Craticula.Core.Schema;

/// <summary>
/// SEM-1/SEM-2/SEM-3/SEM-6: the shared, backend-agnostic <em>decision</em> layer for
/// <c>spec/semantics.md</c>'s rules — one implementation the SQL/Mongo/Search compilers all call,
/// so a semantics rule never drifts between backends (G3/G4). Every method here is a pure function
/// from IR/schema shapes to a decision; how a decision is actually rendered (collation clause,
/// <c>ILIKE</c>, <c>$regex</c>, <c>missing</c> parameter, ...) is exclusively the consumer's job,
/// never this class's.
///
/// <para><b>"Consumer" is deliberately wider than "compiler" since #1645.</b> The three compilers
/// render these decisions into TEXT (SQL, extended JSON, Query DSL);
/// <c>Craticula.EntityFrameworkCore</c> renders them into a LINQ expression tree instead — AR-4's
/// exception — so it calls the same methods and emits no artifact at all. That is the strongest
/// argument available for why the decisions live here rather than in each backend: two of the four
/// consumers now target the same database through completely different machinery, and AR-4 requires
/// them to return the same rows.</para>
///
/// SEM-4 (dates &amp; zones) has no method <em>here</em>, but since #1645 it is no longer purely a rule
/// to read. Its <b>reading</b> half — which absolute instant a <c>datetimeValue</c> denotes — is now a
/// callable decision, <see cref="Craticula.Core.Ir.IsoInstant.TryParseUtc"/>, and all four consumers go
/// through it. <b>Call that rather than parsing the text again:</b> a second parse is how two backends
/// come to disagree about which moment a wire string names, which is the divergence AR-4 forbids.
/// What remains uncallable is SEM-4's <em>rendering</em> half — "must NOT" constraints on output (never
/// emit a zone clause for a <c>datetimeValue</c>, never promote a <c>dateValue</c> to a Mongo
/// <c>Date</c>) — and for those, read SEM-4 in <c>spec/semantics.md</c> directly.
/// </summary>
public static class SemanticsResolver
{
    /// <summary>SEM-1: which backend-native mechanism family realizes case-insensitive comparison —
    /// a collation-based equality/ordering mechanism, a pattern-match mechanism, or (for a
    /// case-sensitive field, or an operator SEM-1 doesn't cover) none at all.</summary>
    public enum CaseSensitivityMechanism
    {
        None,
        Collation,
        PatternMatch
    }

    /// <summary>
    /// SEM-1's answer to "how must this comparison treat case?", in the three values it actually has
    /// — deliberately NOT a <see cref="bool"/>.
    ///
    /// <para>A boolean has to spell <see cref="Sensitive"/> and <see cref="NotApplicable"/> the same
    /// way, and a compiler cannot tell them apart afterwards. That conflation is not academic: an
    /// engine whose own default collation is case-INsensitive (SQL Server and Azure SQL ship
    /// <c>SQL_Latin1_General_CP1_CI_AS</c>) must emit an explicit case-sensitive mechanism for
    /// <see cref="Sensitive"/> — otherwise the field the host declared
    /// <c>caseSensitivity: "sensitive"</c> silently matches case-insensitively, which is SEM-1's
    /// second Must-Not mirrored — while emitting that same mechanism for
    /// <see cref="NotApplicable"/> would attach a collation to an <c>int</c>/<c>date</c> column,
    /// which those engines reject outright.</para>
    /// </summary>
    public enum CaseSensitivityMode
    {
        /// <summary>No collation mechanism applies in either direction, so a dialect spells nothing.
        /// Reached three ways: the comparison is not a text one at all (SEM-1 governs
        /// <c>stringValue</c> comparisons only); it is a structural key of a grouping request, where
        /// GRP-2 forbids any mechanism on a <em>text</em> key too; or it is the SEM-6 tiebreaker a
        /// compiler appended, which is an identity key rather than a text comparison — see
        /// <see cref="ResolveOrderingAndGroupKeyCaseSensitivity"/> for both. All three share this
        /// value because they need the identical rendering (nothing) on every dialect; they are still
        /// separate decisions, and none of them is <see cref="Sensitive"/>.</summary>
        NotApplicable,

        /// <summary>Text, compared case-insensitively — SEM-1's default for a <c>string</c> field,
        /// realized through <see cref="CaseSensitivityDecision.Mechanism"/>.</summary>
        Insensitive,

        /// <summary>Text, compared case-sensitively — SC-3's explicit per-field opt-in, which a
        /// compiler must realize just as deliberately as the insensitive direction.</summary>
        Sensitive
    }

    /// <summary>SEM-1's resolved decision for one field+context: how the comparison treats case, and
    /// — when it is <see cref="CaseSensitivityMode.Insensitive"/> — which
    /// <see cref="CaseSensitivityMechanism"/> family a compiler must use to realize it. The mechanism
    /// is <see cref="CaseSensitivityMechanism.None"/> in the other two modes: it answers "which family
    /// folds case here", and neither of them folds case.</summary>
    public readonly record struct CaseSensitivityDecision(CaseSensitivityMode Mode, CaseSensitivityMechanism Mechanism);

    /// <summary>SEM-2: which condition <c>blank</c>/<c>notBlank</c> resolves to for a given
    /// <see cref="FieldType"/> — text fields additionally treat the empty string as blank, every
    /// other type checks null only.</summary>
    public enum BlankCondition
    {
        NullOnly,
        NullOrEmptyString
    }

    /// <summary>
    /// GRP-2: whether the key being resolved is one of the row-group protocol's OWN
    /// bucket-identity keys. This — not "sort key versus filter operator" — is the axis that decides
    /// whether it folds case, which is why it is a parameter of
    /// <see cref="ResolveOrderingAndGroupKeyCaseSensitivity"/> rather than something a call site
    /// decides for itself.
    /// </summary>
    public enum GroupingMode
    {
        /// <summary>An ordinary key, governed by SEM-1 exactly as a filter predicate is —
        /// <c>spec/fixtures/sort-paging/001</c> renders
        /// <c>ORDER BY "last_name" COLLATE craticula_ci ASC NULLS LAST</c> and pins it. Reached by
        /// every key of an ordinary row query, and also by the <c>ORDER BY</c> of a
        /// fully-consumed drill-down <b>leaf</b>: a leaf has no <c>GROUP BY</c> and no buckets, so
        /// its ordering decides nothing about bucket identity and there is nothing for GRP-2 to
        /// govern (<c>spec/fixtures/group-aggregation/005</c> sorts on <c>product</c>, which is
        /// neither a group column nor a group key).
        ///
        /// <para><b>"Carries no <c>groupSpec</c>" is NOT the test</b>, tempting as it is: a BM-3
        /// distinct-values request carries none either and is
        /// <see cref="GroupingMode.Grouped"/> — see there. The test is whether the key decides or
        /// reproduces a bucket's identity.</para></summary>
        Ungrouped,

        /// <summary>
        /// A key that decides or reproduces bucket identity, where GRP-2 forbids any case-folding
        /// mechanism: a <c>GROUP BY</c> key, a <c>groupKeys</c> drill-down match (at either level —
        /// including the leaf's, whose whole <c>WHERE</c> must select exactly the rows that produced
        /// the bucket being expanded), and a <b>grouped level's</b> <c>ORDER BY</c>, which orders the
        /// buckets the <c>GROUP BY</c> just produced and so has to agree with it.
        ///
        /// <para><b>And BM-3's <c>SELECT DISTINCT</c> key with its own <c>ORDER BY</c></b> (GRP-6(a),
        /// added in #1641) — reached from <c>SqlQueryBuilder.BuildSetValues</c>, on a request that
        /// carries no <c>groupSpec</c> at all. A distinct set is a set of buckets with no aggregate
        /// attached, and its entries are handed straight back to the grid as an <c>in</c> value or a
        /// <c>groupKey</c>, which a grouped request then matches exactly. That caller is why this
        /// mode is named for what the key <em>does</em> rather than for the request shape it appears
        /// in; a set filter's own <c>searchPrefix</c> over that same column is a filter, and stays
        /// <see cref="GroupingMode.Ungrouped"/>.</para>
        /// </summary>
        Grouped
    }

    /// <summary>
    /// SEM-6: where an <c>ORDER BY</c>/<c>$sort</c>/<c>sort</c> key came from. Like
    /// <see cref="GroupingMode"/> this is <em>provenance</em>, not shape — the appended tiebreaker and
    /// a user key on the same field are the identical <see cref="SortItem"/> — so it is a parameter of
    /// <see cref="ResolveOrderingAndGroupKeyCaseSensitivity"/> rather than something recovered from
    /// the key itself.
    /// </summary>
    public enum SortKeyProvenance
    {
        /// <summary>A key the request itself carries, chosen by the user in the grid's own UI.</summary>
        Request,

        /// <summary>The <see cref="GridSchema.StableKey"/> tiebreaker
        /// <see cref="AppendStableKeyTiebreaker(IReadOnlyList{SortItem}?, string)"/> minted, which no user asked for.</summary>
        AppendedTiebreaker
    }

    /// <summary>
    /// SEM-1: resolves how a comparison <paramref name="op"/> against <paramref name="field"/> treats
    /// case. SEM-1 only ever applies to a <c>stringValue</c> comparison — a field whose
    /// <see cref="FieldConfig.Type"/> isn't <see cref="FieldType.String"/> is always
    /// <c>(NotApplicable, None)</c>, regardless of <paramref name="op"/> or any
    /// <see cref="FieldConfig.Text"/> setting (which <see cref="FieldConfig.Text"/>'s own doc already
    /// states is "only meaningful" for a string field — this method is what actually enforces that,
    /// since nothing stops a schema from configuring both). For a string field, it is
    /// <see cref="CaseSensitivityMode.Insensitive"/> unless it explicitly opts into
    /// <see cref="CaseSensitivity.Sensitive"/> via <see cref="FieldConfig.Text"/> — absent
    /// <see cref="FieldConfig.Text"/> defaults to insensitive, matching the IR-level default SEM-1
    /// pins regardless of any one engine's own default. When insensitive, the mechanism is
    /// <see cref="CaseSensitivityMechanism.Collation"/> for the exact-match/ordering/set family
    /// (<c>eq</c>/<c>neq</c>/<c>gt</c>/<c>gte</c>/<c>lt</c>/<c>lte</c>/<c>in</c>/<c>notIn</c>),
    /// <see cref="CaseSensitivityMechanism.PatternMatch"/> for the substring family
    /// (<c>contains</c>/<c>notContains</c>/<c>startsWith</c>/<c>endsWith</c>), and
    /// <see cref="CaseSensitivityMechanism.None"/> for every operator SEM-1 doesn't cover (the unary
    /// operators, and <c>inRange</c>). A case-sensitive field is always <c>(Sensitive, None)</c>
    /// regardless of <paramref name="op"/> — <see cref="CaseSensitivityMode.Sensitive"/> is a
    /// decision a compiler must render, not the absence of one.
    /// </summary>
    public static CaseSensitivityDecision ResolveCaseSensitivity(FieldConfig field, ComparisonOperator op)
    {
        var mode = ResolveMode(field);
        if (mode != CaseSensitivityMode.Insensitive)
        {
            return new CaseSensitivityDecision(mode, CaseSensitivityMechanism.None);
        }

        var mechanism = op switch
        {
            ComparisonOperator.Eq or ComparisonOperator.Neq
                or ComparisonOperator.Gt or ComparisonOperator.Gte
                or ComparisonOperator.Lt or ComparisonOperator.Lte
                or ComparisonOperator.In or ComparisonOperator.NotIn => CaseSensitivityMechanism.Collation,

            ComparisonOperator.Contains or ComparisonOperator.NotContains
                or ComparisonOperator.StartsWith or ComparisonOperator.EndsWith => CaseSensitivityMechanism.PatternMatch,

            _ => CaseSensitivityMechanism.None
        };

        return new CaseSensitivityDecision(CaseSensitivityMode.Insensitive, mechanism);
    }

    /// <summary>
    /// SEM-1/GRP-2: resolves how a query's <em>structural</em> keys treat case — an <c>ORDER BY</c>
    /// key, a <c>GROUP BY</c> key, and the drill-down predicate a <c>groupKeys</c> entry compiles
    /// into. The answer depends on <paramref name="grouping"/>, and that is the whole point of this
    /// method existing separately from
    /// <see cref="ResolveCaseSensitivity(FieldConfig, ComparisonOperator)"/>.
    ///
    /// <para><b><see cref="GroupingMode.Ungrouped"/></b> — the same <see cref="CaseSensitivityMode"/>
    /// classification a filter comparison gets, with the mechanism always
    /// <see cref="CaseSensitivityMechanism.Collation"/> when insensitive: SEM-1 pins that ordering
    /// uses the collation mechanism, never pattern-match, so a field never sorts under one mechanism
    /// and filters under another.</para>
    ///
    /// <para><b><see cref="GroupingMode.Grouped"/></b> — always
    /// <c>(NotApplicable, None)</c>: <b>no case-folding mechanism is spelled on a bucket-identity key
    /// of a grouping request</b> (GRP-2, <c>spec/semantics.md</c>'s grouping addendum). A grouped query's
    /// buckets are exact, so <c>"EMEA"</c> and <c>"emea"</c> are two groups with two unambiguous
    /// labels, and the <c>GROUP BY</c> key, the drill-down <c>WHERE</c> and the <c>ORDER BY</c> must
    /// agree on that or a child level stops summing to its parent's aggregate. The decision is
    /// forced rather than merely chosen: collating the group key produces SQL the engines reject
    /// outright, since the selected column would no longer match the grouped expression
    /// (<c>SELECT "segment" … GROUP BY "segment" COLLATE craticula_ci</c>). Read GRP-2 for the
    /// engines on which "spell nothing" is a host DDL obligation rather than an answer.</para>
    ///
    /// <para>Note the deliberate narrowness: this is about the keys that <em>decide or reproduce
    /// bucket identity</em>, not about "every key of a grouping request". Two things inside a
    /// grouping request are outside it. A user's own filter predicate selects rows before
    /// aggregation (AR-5) and has no say in bucket identity, so it keeps SEM-1's per-field mechanism
    /// through <see cref="ResolveCaseSensitivity(FieldConfig, ComparisonOperator)"/>. And a
    /// fully-consumed drill-down leaf's <c>ORDER BY</c> orders raw rows, not buckets — it is passed
    /// <see cref="GroupingMode.Ungrouped"/> by the compiler, which is what stops a leaf that filters
    /// and sorts the same field from spelling two mechanisms in one statement.</para>
    ///
    /// <para><b><see cref="SortKeyProvenance.AppendedTiebreaker"/> is always <c>(NotApplicable,
    /// None)</c> too, in either <paramref name="grouping"/> mode</b>, and this is SEM-6's own
    /// determinism guarantee rather than a stylistic choice. A <see cref="GridSchema.StableKey"/> is
    /// declared <em>unique</em>, and it is unique under the collation its own storage uses; a
    /// compiler that attaches a different one to it is not comparing text the user asked about, it is
    /// re-deciding row identity. On PostgreSQL the fixture suite's <c>craticula_ci</c> is declared
    /// <c>deterministic = false</c>, under which <c>'aB3'</c> and <c>'Ab3'</c> compare EQUAL — so a
    /// collated tiebreaker leaves the sort non-total, the same statement can order those rows
    /// differently at <c>OFFSET 0</c> and <c>OFFSET 100</c>, and a row is returned twice or dropped
    /// from both pages: precisely the bug SEM-6 exists to prevent, reintroduced by SEM-6's own
    /// mechanism. Spelling nothing sorts it under the column's own collation, which is by definition
    /// the one the uniqueness guarantee is stated in. This is GRP-2's argument extended one step: a
    /// key that establishes identity is not governed by how a field's <em>values</em> compare. It does
    /// NOT reach a user's own key on that same field — <c>spec/fixtures/sort-paging/001</c> still
    /// renders <c>ORDER BY "last_name" COLLATE craticula_ci ASC NULLS LAST, "id" ASC NULLS LAST</c>,
    /// collated user key and bare tiebreaker in one statement — nor a sort that already ends in the
    /// stable key, where nothing was appended and the user's own key stands.</para>
    /// </summary>
    public static CaseSensitivityDecision ResolveOrderingAndGroupKeyCaseSensitivity(
        FieldConfig field, GroupingMode grouping, SortKeyProvenance provenance = SortKeyProvenance.Request)
    {
        if (grouping == GroupingMode.Grouped || provenance == SortKeyProvenance.AppendedTiebreaker)
        {
            return new CaseSensitivityDecision(CaseSensitivityMode.NotApplicable, CaseSensitivityMechanism.None);
        }

        return ResolveMode(field) switch
        {
            CaseSensitivityMode.Insensitive =>
                new CaseSensitivityDecision(CaseSensitivityMode.Insensitive, CaseSensitivityMechanism.Collation),
            var mode => new CaseSensitivityDecision(mode, CaseSensitivityMechanism.None)
        };
    }

    /// <summary>
    /// The single source of SEM-1's field-level classification, which both resolvers above derive
    /// their <see cref="CaseSensitivityDecision.Mode"/> from — so "is this field case-sensitive?" has
    /// exactly one answer no matter which context asks.
    ///
    /// <para>SEM-1 only ever applies to a <c>stringValue</c> comparison, hence the
    /// <see cref="FieldConfig.Type"/> gate: without it, a plain numeric/date/boolean field with no
    /// <see cref="FieldConfig.Text"/> at all would resolve to case-insensitive-via-collation, which is
    /// not merely wrong but a compile-time error on a backend that rejects a collation on a non-text
    /// type. It is a THREE-way classification rather than that gate plus a boolean precisely because
    /// the gate's "no" and the opt-in's "no" demand opposite renderings — see
    /// <see cref="CaseSensitivityMode"/>.</para>
    /// </summary>
    private static CaseSensitivityMode ResolveMode(FieldConfig field) =>
        field.Type != FieldType.String ? CaseSensitivityMode.NotApplicable
        : field.Text?.CaseSensitivity == CaseSensitivity.Sensitive ? CaseSensitivityMode.Sensitive
        : CaseSensitivityMode.Insensitive;

    /// <summary>
    /// SEM-2: whether <paramref name="op"/> MUST include NULL-valued rows in its match set —
    /// <c>neq</c>, <c>notContains</c>, and <c>notIn</c>. A tree-level <c>notNode</c> carries the
    /// identical null-inclusive requirement one syntax level up (SEM-2), but that is a compiler
    /// rendering concern over the tree shape, not a per-operator decision this method makes.
    /// </summary>
    public static bool IsNullInclusive(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Neq or ComparisonOperator.NotContains or ComparisonOperator.NotIn => true,
        _ => false
    };

    /// <summary>
    /// SEM-2: resolves which condition <c>blank</c>/<c>notBlank</c> compiles to for
    /// <paramref name="fieldType"/> — <see cref="BlankCondition.NullOrEmptyString"/> for
    /// <see cref="FieldType.String"/> (<c>IS NULL OR = ''</c>), <see cref="BlankCondition.NullOnly"/>
    /// for every other type (<c>IS NULL</c>). <c>notBlank</c> is always this condition's exact
    /// Boolean negation — a compiler must negate the compiled <c>blank</c> condition, never
    /// re-derive it independently (SEM-2's "must NOT" — the two could otherwise drift apart at the
    /// edges).
    ///
    /// <para><b>This answers the FIELD-TYPE half only, and deliberately so.</b> There is a second,
    /// independent question — does the target engine treat <c>''</c> as NULL? — which collapses the
    /// text arm onto the non-text one on Oracle (<c>spec/semantics.md</c> SQL-7, where emitting the
    /// <c>= ''</c> disjunct anyway makes <c>notBlank</c> match no row at all). That question is NOT
    /// answered here: this assembly knows the schema and nothing about backends, and giving it a
    /// dialect would invert the layering the compiler seams exist to draw. The SQL compiler composes
    /// the two answers (<c>Craticula.Sql.ISqlDialect.EmptyStringIsNull</c>); a Mongo or Search
    /// compiler will have its own second half or none.</para>
    /// </summary>
    public static BlankCondition ResolveBlankCondition(FieldType fieldType) =>
        fieldType == FieldType.String ? BlankCondition.NullOrEmptyString : BlankCondition.NullOnly;

    /// <summary>
    /// SEM-3: the adapter's pinned default <see cref="NullsPosition"/> for <paramref name="direction"/>
    /// when AG Grid's <c>sortModel</c> gives no null-placement signal at all — <c>"last"</c> on
    /// <see cref="SortDirection.Asc"/>, <c>"first"</c> on <see cref="SortDirection.Desc"/>. This is
    /// the adapter's own rule for producing <see cref="SortItem.NullsPosition"/>, never a license for
    /// a compiler to re-derive <see cref="SortItem.NullsPosition"/> from <see cref="SortItem.Direction"/>
    /// itself once the IR already carries it explicitly (SEM-3's "must NOT").
    /// </summary>
    public static NullsPosition DefaultNullsPosition(SortDirection direction) =>
        direction == SortDirection.Asc ? NullsPosition.Last : NullsPosition.First;

    /// <summary>
    /// SEM-6: appends the schema-declared <paramref name="stableKey"/> as a trailing sort tiebreaker
    /// — always last, never ahead of or interleaved with the user's own chosen keys — unless
    /// <paramref name="sort"/> already reaches it. The sort is reduced first (SEM-6, Bug #2565): a later occurrence of a field the sort
    /// already named is dropped, and so is everything after the first occurrence of the stable key. The appended key is always
    /// <c>{ field: stableKey, direction: asc, nullsPosition: last }</c> (SEM-6: the tiebreaker's
    /// direction is always ascending, since it exists purely to break ties and carries no direction
    /// the user chose, and SEM-3's own adapter default for <c>asc</c> applies to it identically).
    /// Runs <see cref="SortItemValidator.Validate"/> on <paramref name="sort"/> first as a
    /// defense-in-depth check, mirroring <see cref="ResourceLimitsValidator.Validate"/>/
    /// <see cref="TypeCoercionValidator.Coerce"/>/<see cref="StaticFilterInjector.Inject"/>'s own
    /// precedent, since this is a public entry point in its own right rather than one only ever
    /// reached via an already-validated pipeline. It then validates the minted tiebreaker itself the
    /// same way — this method is the one place that MINTS a new <see cref="SortItem"/> from a bare
    /// <paramref name="stableKey"/> string rather than merely passing an already-validated one
    /// through, so a blank or overlong <paramref name="stableKey"/> (reachable via a
    /// <see cref="GridSchema"/> built directly rather than through <see cref="GridSchema.Parse"/>,
    /// the same bypass <see cref="StaticFilterInjector"/>'s own doc calls out) must not silently
    /// produce an output that would itself fail <see cref="SortItemValidator.Validate"/>.
    /// <paramref name="sort"/> may be <see langword="null"/> (no sort chosen at all), in which case
    /// the result is a single-element list containing only the tiebreaker.
    ///
    /// <para>Known, accepted scope limit: this method does not allowlist <paramref name="sort"/>'s
    /// field names against a <see cref="GridSchema"/> (SC-1) — it only enforces SEM-6's own shape.
    /// There is no orchestrator yet analogous to <see cref="GridFilterPipeline"/> that assembles a
    /// full request's sort array from client input before it reaches a compiler; when one exists, it
    /// must run <see cref="FieldAllowlistValidator"/> over the sort field names (and check
    /// <see cref="FieldConfig.Sortable"/>) before calling this method, the same way
    /// <see cref="GridFilterPipeline"/> already does for filter field names.</para>
    /// </summary>
    // No schema here: this overload cannot tell a case-insensitive text key from an exact one nor a known field from an undeclared one, so it
    // always stops at the key and never checks a field. Use the schema-aware overload below wherever a GridSchema is at hand (every compiler does).
    public static IReadOnlyList<SortItem> AppendStableKeyTiebreaker(IReadOnlyList<SortItem>? sort, string stableKey) =>
        AppendStableKeyTiebreaker(sort, stableKey, out _);

    /// <summary>
    /// <see cref="AppendStableKeyTiebreaker(IReadOnlyList{SortItem}?, string, out bool)"/> for a compiler that has the schema: it decides, from
    /// the stable key's own field, whether the sort may stop at the stable key, and it never lets the reduction hide a key the compilers
    /// would have rejected. <b>The sort is reduced only when every key in it is one the query knows</b> (SC-1, SEC-6, GRP-4): on a row query a
    /// field of <paramref name="schema"/>; on a grouped level (<paramref name="groupColumn"/> not null, where the group column plays the stable
    /// key's role and is exact by construction, GRP-2/GRP-4) the group column or one of the level's aggregate column ids
    /// (<paramref name="aggregateColumnIds"/>). A sort that names anything else is left as it is (nothing after a key is dropped), so the
    /// compiler's own lookup still rejects the unknown key, as it did before the reduction existed.
    /// On a row query the user's own key on the stable key resolves under SEM-1 like any other: a case-insensitive text stable key can tie
    /// (<c>'aB3'</c> and <c>'Ab3'</c> compare equal), so nothing after it is dropped and the exact tiebreaker is still appended; any other
    /// stable key is exact and the sort stops at it. Repeated fields are dropped in every case.
    /// </summary>
    public static IReadOnlyList<SortItem> AppendStableKeyTiebreaker(
        IReadOnlyList<SortItem>? sort, GridSchema schema, string? groupColumn, out bool appended,
        IReadOnlyCollection<string>? aggregateColumnIds = null)
    {
        if (groupColumn is not null)
        {
            var known = AllKeysKnown(sort, field =>
                string.Equals(field, groupColumn, StringComparison.Ordinal) || aggregateColumnIds?.Contains(field) == true);
            return AppendStableKeyTiebreaker(sort, groupColumn, known, out appended);
        }

        var stop = StableKeyOrdersExactly(schema) && AllKeysKnown(sort, schema.Fields.ContainsKey);
        return AppendStableKeyTiebreaker(sort, schema.StableKey, stop, out appended);
    }

    private static bool AllKeysKnown(IReadOnlyList<SortItem>? sort, Func<string, bool> isKnown) =>
        sort is null || sort.All(item => isKnown(item.Field));

    /// <summary>True when a user's key on the schema's stable key compares exactly (SEM-1 resolves it to a mode other than case-insensitive),
    /// so two rows with different stable-key values never tie under it.</summary>
    public static bool StableKeyOrdersExactly(GridSchema schema) =>
        !schema.Fields.TryGetValue(schema.StableKey, out var field)
        || ResolveOrderingAndGroupKeyCaseSensitivity(field, GroupingMode.Ungrouped, SortKeyProvenance.Request).Mode
            != CaseSensitivityMode.Insensitive;

    /// <summary>
    /// <see cref="AppendStableKeyTiebreaker(IReadOnlyList{SortItem}?, string)"/>, additionally
    /// reporting whether the trailing key is the one this method MINTED
    /// (<paramref name="appended"/> <see langword="true"/>) or a key the request already carried.
    /// That distinction is not recoverable from the returned list — the appended tiebreaker and a
    /// user key on the same field are the identical <see cref="SortItem"/> — and it is load-bearing:
    /// <see cref="SortKeyProvenance.AppendedTiebreaker"/> resolves case sensitivity differently from
    /// <see cref="SortKeyProvenance.Request"/>. A caller that re-derived it by comparing the last
    /// field to <paramref name="stableKey"/> would be duplicating this method's own rule, so it is
    /// reported from the one place that decides it.
    /// </summary>
    public static IReadOnlyList<SortItem> AppendStableKeyTiebreaker(
        IReadOnlyList<SortItem>? sort, string stableKey, out bool appended) =>
        AppendStableKeyTiebreaker(sort, stableKey, true, out appended);

    private static IReadOnlyList<SortItem> AppendStableKeyTiebreaker(
        IReadOnlyList<SortItem>? sort, string stableKey, bool stopAtStableKey, out bool appended)
    {
        SortItemValidator.Validate(sort);

        // SEM-6 (Bug #2565): reduce first, append second. A later occurrence of a field the sort already named is dropped, and so is
        // everything after the first occurrence of the stable key (it is unique by declaration, so nothing after it can break a tie).
        // Both only remove keys that can never change the order; the result names every field once and, when the stable key is among
        // them, ends in it. Without this, [id desc, age asc] compiled to ORDER BY id DESC, age ASC, id ASC, which SQL Server rejects
        // (error 169) and a MongoDB $sort document cannot hold.
        var reduced = ReduceSort(sort, stableKey, stopAtStableKey, out var reachedStableKey);

        // Stopping at the stable key needs it to order exactly. When it does not (a case-insensitive text key can tie), nothing after it was
        // dropped, and the sort counts as total only if it already ENDS in the key, as it always has; otherwise the exact tiebreaker is appended.
        if (stopAtStableKey ? reachedStableKey : reduced is { Count: > 0 } && reduced[^1].Field == stableKey)
        {
            appended = false;
            return reduced!;
        }

        appended = true;

        var tiebreaker = new SortItem
        {
            Field = stableKey,
            Direction = SortDirection.Asc,
            NullsPosition = NullsPosition.Last
        };

        SortItemValidator.Validate([tiebreaker]);

        return reduced is null ? [tiebreaker] : [..reduced, tiebreaker];
    }

    /// <summary>
    /// The two SEM-6 reductions, in one pass: keep the first occurrence of each field, and (when <paramref name="stopAtStableKey"/>, i.e. the stable
    /// key orders exactly) stop after the stable key. Returns
    /// <paramref name="sort"/> itself, by reference, when nothing was dropped (a sort that needs no reduction is not copied), and
    /// <see langword="null"/> for a null sort. <paramref name="reachedStableKey"/> says the result stops at the stable key.
    /// Direction, null placement and case handling play no part: a dropped key could not have mattered under any of them.
    /// </summary>
    private static IReadOnlyList<SortItem>? ReduceSort(
        IReadOnlyList<SortItem>? sort, string stableKey, bool stopAtStableKey, out bool reachedStableKey)
    {
        reachedStableKey = false;
        if (sort is null)
        {
            return null;
        }

        HashSet<string> seen = new(sort.Count, StringComparer.Ordinal);
        List<SortItem> kept = new(sort.Count);
        foreach (var item in sort)
        {
            if (!seen.Add(item.Field))
            {
                continue;
            }

            kept.Add(item);
            if (stopAtStableKey && string.Equals(item.Field, stableKey, StringComparison.Ordinal))
            {
                reachedStableKey = true;
                break;
            }
        }

        return kept.Count == sort.Count ? sort : kept;
    }
}
