using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Sql;

/// <summary>
/// The scalar comparison shapes a dialect must render — deliberately a narrower vocabulary than
/// <see cref="ComparisonOperator"/>'s 17 members. A dialect never sees an operator it has no
/// rendering for (<c>blank</c>/<c>notBlank</c>/<c>inRange</c> are structural compositions
/// <see cref="SqlQueryBuilder"/> owns, and <c>true</c>/<c>false</c> reduce to
/// <see cref="Equal"/> against a minted boolean), so every implementation's switch is total by
/// construction rather than by a defensive default arm.
/// </summary>
public enum SqlScalarComparison
{
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}

/// <summary>Where a substring match anchors — SEM-7's pattern family
/// (<c>contains</c>/<c>notContains</c>/<c>startsWith</c>/<c>endsWith</c>).</summary>
public enum SqlSubstringAnchor
{
    /// <summary><c>contains</c>/<c>notContains</c> — unanchored.</summary>
    Anywhere,
    Start,
    End
}

/// <summary>
/// BM-1's whole point: every token a SQL dialect spells differently, behind one narrow interface, so
/// adding a dialect means implementing this and nothing else — zero changes to
/// <see cref="SqlQueryBuilder"/> (see <c>docs/architecture/c4-components-sql-compiler.md</c>; a
/// dialect-specific <c>if</c> in the builder is the smell that document warns about).
///
/// <para><b>Rendering only, never semantics.</b> Which mechanism realizes case-insensitivity, whether
/// a negative operator includes NULLs, what <c>blank</c> resolves to for a field type, where NULLs
/// sort — all of that is decided upstream by <c>Craticula.Core.Schema.SemanticsResolver</c> (#1634)
/// and arrives here as an already-made decision or an already-chosen shape. An implementation that
/// consults a <c>GridSchema</c>, re-derives a SEM-* rule, or decides <em>whether</em> to fold case
/// (rather than how) has crossed the line this interface exists to draw.</para>
///
/// <para><b>Case sensitivity arrives as <see cref="SemanticsResolver.CaseSensitivityMode"/>, not a
/// <c>bool</c>, and that is load-bearing.</b> A boolean cannot distinguish "text, compared
/// case-sensitively" from "not a text comparison at all", and the two demand opposite renderings on
/// an engine whose own default collation is case-insensitive: SQL Server must spell an explicit
/// case-SENSITIVE collation for the first (else it silently returns rows the host's
/// <c>caseSensitivity: "sensitive"</c> declaration excluded — SEM-1's second Must-Not mirrored) and
/// must spell nothing at all for the second (a collation on an <c>int</c> column is a hard error). The
/// distinction is a semantic one the resolver owns; each dialect only decides how — or whether — to
/// spell each of the three.</para>
///
/// <para><b>Structure is the builder's, tokens are the dialect's.</b> The parenthesisation rules
/// (<c>spec/semantics.md</c> SQL-2), the null-inclusive <c>OR col IS NULL</c> disjunct, the
/// <c>blank</c> composition and the tree-level <c>NOT</c> fan-out are identical in every SQL dialect,
/// so they stay in the builder. <c>IS NULL</c> and <c>= ''</c> are likewise emitted there: they are
/// ANSI SQL every dialect spells identically, so pushing them down would add an override point no
/// dialect would ever use differently.</para>
///
/// <para><b>Eight rendering methods and THREE engine facts — a distinction worth keeping.</b>
/// Everything above returns SQL text: the dialect is asked "how do you spell this?" and the builder
/// splices the answer in. <see cref="EmptyStringIsNull"/>, <see cref="DistinctValuesUseGroupBy"/> and
/// <see cref="DatetimeBoundsAreFixedWidth"/> are a
/// different kind of member, the only three of their kind: each answers a question <em>about the engine</em>, and the builder then composes
/// the predicate itself. Reach for that shape when the composition must stay in one place (because
/// two operators are defined as each other's negation, say) and only a single engine fact varies
/// inside it; reach for a rendering method when the whole construct's shape, token order or
/// parameter count is the dialect's own. Getting that backwards is how four dialects end up carrying
/// byte-identical implementations of the same method.</para>
///
/// <para><b>This interface has now been widened five times, and none of them was free</b> — #1639
/// turned <c>RenderScalarComparison</c>'s case-sensitivity argument from a <c>bool</c> into the
/// three-valued <see cref="SemanticsResolver.CaseSensitivityMode"/>, #1641 added
/// <see cref="RenderRowLimit"/>, #1642 added <see cref="EmptyStringIsNull"/>, #2206 added
/// <see cref="DistinctValuesUseGroupBy"/>, and #2243 added <see cref="DatetimeBoundsAreFixedWidth"/>. Each cost every existing implementation a change and every
/// language port a re-read. BM-1's claim is that adding a dialect needs no change to
/// <see cref="SqlQueryBuilder"/>, not that this file never moves; but a sixth widening should have to
/// argue that the seam, and not the candidate dialect, is what is wrong. The fourth did: T-SQL msg 145
/// refuses <c>SELECT DISTINCT</c> whose ORDER BY key is not in the select list, a fact about one engine
/// the builder cannot know. A rendering method would have put a second statement shape for the whole
/// values query behind the seam; a capability fact keeps the one shape and lets the builder pick
/// <c>GROUP BY</c> for the one engine that needs it (GRP-6(f)). The fifth did too: SQLite compares a datetime as TEXT, so
/// the bound's width is part of the answer, and only that engine's builder-side normalisation changes
/// (SEM-4); a rendering method would have moved the instant parsing behind the seam for every dialect.
/// A sixth change, #2304, is a different kind: it adds no member, it gives <see cref="RenderSetMembership"/> the
/// field's <see cref="FieldType"/> as an argument, because the Postgres array cast depends on the column type
/// and nothing else the dialect can see.</para>
///
/// <para>Every method that binds a value takes the <see cref="ISqlParameterSink"/> rather than a
/// pre-rendered placeholder, because how many parameters a construct binds is itself
/// dialect-specific: Postgres's <c>= ANY($1)</c> binds one array where SQL Server's <c>IN (...)</c>
/// binds N scalars (SQL-4), and SQL Server's <c>OFFSET/FETCH</c> binds its two paging values in the
/// opposite order to Postgres's <c>LIMIT/OFFSET</c>.</para>
/// </summary>
public interface ISqlDialect
{
    /// <summary>The artifact's <c>dialect</c> discriminator (<c>"postgres"</c>, <c>"mysql"</c>, ...).</summary>
    string Name { get; }

    /// <summary>
    /// SQL-7: does this engine treat the zero-length string as NULL? <c>false</c> on Postgres, SQL
    /// Server, MySQL and SQLite; <c>true</c> on <see cref="OracleDialect"/> alone.
    ///
    /// <para><b>Not a spelling — an engine fact, and the only member here that is one.</b> It is the
    /// single input <see cref="SqlQueryBuilder"/> needs to keep composing SEM-2's <c>blank</c> itself:
    /// <c>true</c> makes the text arm <c>"c" IS NULL</c> and <c>false</c> leaves it
    /// <c>("c" IS NULL OR "c" = '')</c>. <c>notBlank</c> is then <c>NOT (&lt;that text&gt;)</c>, which
    /// is what SEM-2 requires, in both branches.</para>
    ///
    /// <para><b>Why this is a correctness fix and not a redundant-disjunct tidy-up.</b> Oracle
    /// documents that a character value of length zero is NULL, so <c>"c" = ''</c> is <c>"c" = NULL</c>
    /// and evaluates to UNKNOWN for <em>every</em> row, NULL-valued or not. <c>blank</c> survives that
    /// by luck (<c>TRUE OR UNKNOWN</c> is TRUE, <c>FALSE OR UNKNOWN</c> is UNKNOWN, and a <c>WHERE</c>
    /// clause keeps only TRUE — so the right rows match either way). <c>notBlank</c> does not:
    /// <c>NOT (FALSE OR UNKNOWN)</c> is <c>NOT UNKNOWN</c>, which is UNKNOWN, so a row with a real
    /// value is filtered out along with the NULLs and the operator returns the <b>empty result
    /// set</b> — a silently empty grid, on well-formed SQL that the artifact tier cannot see is
    /// wrong. Same defect class as MySQL's <c>ESCAPE '\'</c> (SQL-5) and SQLite's
    /// case-insensitive <c>LIKE</c> (D3).</para>
    ///
    /// <para>Deliberately <b>not</b> a <c>RenderBlank</c> method, on two grounds. Four of five dialects
    /// would carry a byte-identical implementation of it, which is the duplicate-instead-of-share
    /// smell #1639's review flagged for the comparison symbols; and every one of those five would have
    /// to reach into <c>Craticula.Core</c> for <c>SemanticsResolver.ResolveBlankCondition</c> to
    /// answer the field-type half, which is a dependency no dialect has today. Deliberately not
    /// resolved <em>in</em> that resolver either: that assembly knows nothing about dialects and
    /// teaching it would invert the layering this seam exists to draw.</para>
    ///
    /// <para><em>What is deliberately NOT among those grounds</em>: "so that <c>blank</c> and
    /// <c>notBlank</c> cannot drift apart". They cannot drift under either design — the negation is
    /// composed builder-side as <c>NOT (…)</c> over whatever text the <c>blank</c> half produced, and
    /// a <c>RenderBlank</c> member would have fed that same composition. The argument reads well and
    /// discriminates nothing, so it is not carried here.</para>
    /// </summary>
    bool EmptyStringIsNull { get; }

    /// <summary>
    /// GRP-6(f): whether the BM-3 distinct set is spelled <c>GROUP BY &lt;col&gt;</c> instead of
    /// <c>SELECT DISTINCT &lt;col&gt;</c>. True only where the engine refuses a <c>DISTINCT</c> query
    /// whose <c>ORDER BY</c> key is not in the select list (SQL Server, msg 145).
    /// The two spellings compute the same set; this is a spelling fact, not a semantic one.
    /// </summary>
    bool DistinctValuesUseGroupBy => false;

    /// <summary>
    /// SEM-4 (SQLite): whether the engine compares a <c>datetime</c> as TEXT, so the bound must be
    /// spelled with the width the host stores. True only on SQLite, whose column holds
    /// <c>yyyy-MM-ddTHH:mm:ss.fffZ</c> (exactly three fractional digits) and whose <c>.</c> sorts before
    /// <c>Z</c>: a bound spelled <c>...:00Z</c> would sort after a stored <c>...:00.001Z</c> and drop it.
    /// When true the builder binds a whole-millisecond instant with all three digits; a bound with a
    /// sub-millisecond part is first rounded to the millisecond by its operator (gt/lte floor, gte/lt ceil; eq, neq
    /// and set membership keep the full value). Every other dialect compares a typed instant and
    /// keeps the whole-second form. A fact about the engine; the formatting stays in the builder.
    /// </summary>
    bool DatetimeBoundsAreFixedWidth => false;

    /// <summary>
    /// SQL-1: quotes one identifier — applied to <em>every</em> column and to the table, never
    /// conditionally. Unquoted identifiers are not the same identifier: PostgreSQL case-folds them to
    /// lowercase, which silently breaks the camelCase physical columns real schemas declare.
    /// </summary>
    string QuoteIdentifier(string identifier);

    /// <summary>
    /// The placeholder text for the parameter at <paramref name="ordinal"/> (0-based). Called by
    /// <see cref="ISqlParameterSink.Bind"/>, never directly by a dialect.
    /// </summary>
    string ParameterPlaceholder(int ordinal);

    /// <summary>
    /// SEM-1 exact-match/ordering family: <c>eq</c>/<c>neq</c>/<c>gt</c>/<c>gte</c>/<c>lt</c>/<c>lte</c>
    /// against a single value. <paramref name="caseSensitivity"/> is
    /// <c>SemanticsResolver.ResolveCaseSensitivity</c>'s already-made decision that the collation
    /// mechanism applies (or does not) — the dialect only chooses how to spell each of the three
    /// modes. Postgres puts <c>COLLATE craticula_ci</c> on the column and needs nothing for
    /// <c>Sensitive</c> (its default collation already distinguishes case); SQLite puts
    /// <c>COLLATE NOCASE</c> after the value and likewise needs nothing for <c>Sensitive</c>; SQL
    /// Server spells <em>both</em> directions, because its stock collation is case-INsensitive; MySQL
    /// spells neither, because case sensitivity there is the column's declared collation.
    /// </summary>
    string RenderScalarComparison(
        string quotedColumn,
        SqlScalarComparison comparison,
        object? value,
        SemanticsResolver.CaseSensitivityMode caseSensitivity,
        ISqlParameterSink parameters);

    /// <summary>
    /// SEM-7 substring family. <paramref name="negated"/> renders the negated operator itself
    /// (<c>NOT ILIKE</c>) rather than an outer <c>NOT (...)</c>, per SQL-3. The dialect owns the
    /// pattern's own escaping too (SEC-4's <c>%</c>/<c>_</c>/<c>\</c> set and the matching
    /// <c>ESCAPE</c> clause), since the escape character is not portable — SEM-7's "must NOT emit a
    /// SQL <c>LIKE</c> pattern without a matching <c>ESCAPE</c> clause". Only the <em>character</em>
    /// is the dialect's: call <see cref="SqlPatternEscaping.Escape"/> with it rather than reimplementing
    /// the loop. (A dialect whose <c>LIKE</c> has metacharacters beyond SEC-4's shared set — T-SQL's
    /// <c>[</c> — adds its own on top of that call rather than forking the shared one.)
    ///
    /// <para><paramref name="caseSensitivity"/> can select a different pattern OPERATOR, not just a
    /// different token around the same one: SQLite's <c>LIKE</c> is ASCII-case-insensitive
    /// unconditionally, so its <c>Sensitive</c> arm is <c>GLOB</c>, with different wildcards and no
    /// <c>ESCAPE</c> clause at all. <c>NotApplicable</c> never reaches this method — a substring match
    /// against a non-text field has no defined rendering and the builder rejects it upstream.</para>
    /// </summary>
    string RenderSubstringMatch(
        string quotedColumn,
        SqlSubstringAnchor anchor,
        bool negated,
        string value,
        SemanticsResolver.CaseSensitivityMode caseSensitivity,
        ISqlParameterSink parameters);

    /// <summary>
    /// SQL-4 set membership (<c>in</c>/<c>notIn</c>). Deliberately takes the whole value list rather
    /// than pre-bound placeholders: Postgres binds one array parameter (<c>= ANY($1)</c>) while
    /// SQL Server/MySQL/SQLite bind N scalars (<c>IN (...)</c>), so the parameter count itself is a
    /// dialect decision.
    ///
    /// <para><paramref name="fieldType"/> (#2304) is the field's type, there for one engine's sake:
    /// <c>decimal</c>, <c>date</c> and <c>datetime</c> values are strings (AR-7), so the array a host binds
    /// is <c>text[]</c> and PostgreSQL has no <c>numeric = text</c> operator; the Postgres dialect casts the
    /// placeholder to the column type (<c>$1::numeric[]</c>). The other four dialects ignore it. This is a
    /// signature change on an existing member, NOT a new member, so the seam's membership is unchanged.</para>
    /// </summary>
    string RenderSetMembership(
        string quotedColumn,
        bool negated,
        IReadOnlyList<object?> values,
        FieldType fieldType,
        SemanticsResolver.CaseSensitivityMode caseSensitivity,
        ISqlParameterSink parameters);

    /// <summary>
    /// SEM-3 one <c>ORDER BY</c> key, including null placement. May legitimately expand to more than
    /// one comma-separated key: MySQL has no <c>NULLS FIRST</c>/<c>LAST</c> syntax and emulates it
    /// with a leading <c>ISNULL(col)</c> key, SQL Server with a <c>CASE WHEN col IS NULL</c> one.
    /// <paramref name="caseSensitivity"/> comes from
    /// <c>SemanticsResolver.ResolveOrderingAndGroupKeyCaseSensitivity</c>, which already restricts the
    /// collation mechanism to <c>string</c> keys — and SEM-1's "same mechanism in every context on a
    /// field" is why an ordering key collates in exactly the cases the exact-match family does, in
    /// both directions.
    ///
    /// <para>One exception, and it is the resolver's to make, not this method's: inside BM-2's
    /// row-group protocol GRP-2 spells no mechanism at all, so every ordering key of a grouped or
    /// drill-down query arrives as
    /// <see cref="SemanticsResolver.CaseSensitivityMode.NotApplicable"/> even when it is a
    /// case-insensitive <c>string</c>. A dialect needs no code for that — it already renders that
    /// mode — but a reader comparing <c>sort-paging/001</c> with <c>group-aggregation/002</c> needs
    /// the reason.</para>
    /// </summary>
    string RenderSortKey(
        string quotedColumn,
        SortDirection direction,
        NullsPosition nullsPosition,
        SemanticsResolver.CaseSensitivityMode caseSensitivity);

    /// <summary>
    /// The trailing paging clause for an already-resolved row window: skip <paramref name="offset"/>
    /// rows, return at most <paramref name="limit"/> of them. Binds its own values through
    /// <paramref name="parameters"/>, in whatever order the clause spells them — that order is the
    /// axis this method exists for (Postgres's <c>LIMIT $n OFFSET $n</c> binds the limit first,
    /// SQL Server's <c>OFFSET ... FETCH NEXT</c> the offset first), and it is why the two values
    /// arrive unbound rather than as pre-rendered placeholders.
    ///
    /// <para>The dialect is never handed the <see cref="PageSpec"/> itself. Deciding that
    /// <c>NonePage</c> means "no clause at all" (EXP-2) and that AG-3's <em>exclusive</em>
    /// <c>endRow</c> makes the window <c>endRow - startRow</c> rows wide are semantic readings of
    /// the request contract, not spellings — so <see cref="SqlQueryBuilder"/> performs both once and
    /// every dialect inherits the same answer. A dialect that re-derived the subtraction and got it
    /// wrong would still produce a perfectly plausible statement, which is exactly the failure mode
    /// this interface's "rendering only, never semantics" line exists to prevent.</para>
    /// </summary>
    string RenderPaging(int offset, int limit, ISqlParameterSink parameters);

    /// <summary>
    /// GRP-6: the trailing clause that caps a BM-3 distinct-values query at
    /// <paramref name="limit"/> rows, with <b>no offset at all</b>.
    ///
    /// <para><b>Why this is not <see cref="RenderPaging"/> with a zero offset — the one place BM-3
    /// had to widen this seam.</b> A distinct-values query is not paged: the set filter asks for the
    /// column's values up to a ceiling and there is no second page to fetch, which is why
    /// <c>group-aggregation/016</c> pins <c>LIMIT $1</c> and its Mongo/Search arms pin a bare
    /// <c>$limit</c>/<c>size</c> rather than a <c>$skip</c>/<c>from</c> pair. Passing
    /// <c>offset: 0</c> to <see cref="RenderPaging"/> would emit an <c>OFFSET</c> those artifacts do
    /// not have and bind a parameter the query has no value for.</para>
    ///
    /// <para>And a bare row cap is genuinely per-engine, in position as well as spelling: three of the
    /// five dialects spell it <c>LIMIT ?</c>, while T-SQL has no <c>LIMIT</c> at all and reaches it
    /// through <c>OFFSET 0 ROWS FETCH NEXT … ROWS ONLY</c> (or a <c>TOP (…)</c> that would have to be
    /// injected into the select list instead — the reason this member returns a trailing clause, so
    /// the builder's statement shape stays one shape). That is exactly the axis this interface exists
    /// for; a builder that spelled it itself would be the BM-1 smell, and
    /// <c>DialectIndependenceTests</c>' forbidden-token list contains <c>LIMIT</c> for that reason.</para>
    ///
    /// <para>#1642 added a <b>third</b> spelling and confirmed the member was the right call:
    /// <see cref="OracleDialect"/> writes <c>FETCH FIRST … ROWS ONLY</c> — the same keyword family as
    /// T-SQL's but legal with no <c>OFFSET</c> before it, so it needs neither that dialect's literal
    /// zero nor Postgres's <c>LIMIT</c>. It fit this signature unchanged, which is what #1641
    /// predicted when it wrote the member.</para>
    ///
    /// <para><paramref name="limit"/> arrives already resolved by
    /// <c>Craticula.Core.Schema.ResourceLimitsValidator.ResolveSetValuesLimit</c> — clamping the
    /// client's request to the host's configured maximum is a SEC-3 decision, not a spelling, so no
    /// dialect ever sees the unclamped number.</para>
    /// </summary>
    string RenderRowLimit(int limit, ISqlParameterSink parameters);
}
