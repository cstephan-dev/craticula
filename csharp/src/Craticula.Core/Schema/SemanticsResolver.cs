using Craticula.Core.Ir;

namespace Craticula.Core.Schema;

/// <summary>
/// SEM-1/SEM-2/SEM-3/SEM-6: the shared, backend-agnostic <em>decision</em> layer for
/// <c>spec/semantics.md</c>'s rules — one implementation the SQL/Mongo/Search compilers all call,
/// so a semantics rule never drifts between backends (G3/G4). Every method here is a pure function
/// from IR/schema shapes to a decision; how a decision is actually rendered (collation clause,
/// <c>ILIKE</c>, <c>$regex</c>, <c>missing</c> parameter, ...) is exclusively a future compiler's
/// job, never this class's.
///
/// SEM-4 (dates &amp; zones) has no corresponding method here: it is almost entirely "must NOT"
/// constraints on compiler <em>rendering</em> (never emit a zone clause for a <c>datetimeValue</c>,
/// never promote a <c>dateValue</c> to a Mongo <c>Date</c>) rather than a callable decision — a
/// future compiler author should read SEM-4 in <c>spec/semantics.md</c> directly rather than expect
/// a resolver method for it.
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
        /// <summary>Not a text comparison at all, so no collation mechanism applies in either
        /// direction — SEM-1 governs <c>stringValue</c> comparisons only.</summary>
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
    /// SEM-1: resolves how sorting or group-key drilldown matching on <paramref name="field"/> treats
    /// case — the same <see cref="CaseSensitivityMode"/> classification as
    /// <see cref="ResolveCaseSensitivity(FieldConfig, ComparisonOperator)"/>, but the mechanism is
    /// always <see cref="CaseSensitivityMechanism.Collation"/> when insensitive: SEM-1 pins that sort
    /// and group-key matching always use the collation mechanism, never pattern-match, so the same
    /// field never sorts under one mechanism and filters under another.
    /// </summary>
    public static CaseSensitivityDecision ResolveSortOrGroupKeyCaseSensitivity(FieldConfig field) =>
        ResolveMode(field) switch
        {
            CaseSensitivityMode.Insensitive =>
                new CaseSensitivityDecision(CaseSensitivityMode.Insensitive, CaseSensitivityMechanism.Collation),
            var mode => new CaseSensitivityDecision(mode, CaseSensitivityMechanism.None)
        };

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
    /// <paramref name="sort"/> already ends in it. The appended key is always
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
    public static IReadOnlyList<SortItem> AppendStableKeyTiebreaker(IReadOnlyList<SortItem>? sort, string stableKey)
    {
        SortItemValidator.Validate(sort);

        if (sort is not null && sort[^1].Field == stableKey)
        {
            return sort;
        }

        var tiebreaker = new SortItem
        {
            Field = stableKey,
            Direction = SortDirection.Asc,
            NullsPosition = NullsPosition.Last
        };

        SortItemValidator.Validate([tiebreaker]);

        return sort is null ? [tiebreaker] : [..sort, tiebreaker];
    }
}
