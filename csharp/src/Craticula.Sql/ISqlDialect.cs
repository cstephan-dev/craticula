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
/// ANSI SQL every v1 dialect spells identically (verified across all four dialects' artifacts in
/// <c>spec/fixtures/dialect-parity/</c>), so pushing them down would add an override point no dialect
/// would ever use differently.</para>
///
/// <para><b>That last claim is scoped to the four v1 dialects (§5), and Oracle (#1642) breaks it.</b>
/// Oracle treats the empty string as NULL, so <c>blank</c>'s string arm
/// <c>("c" IS NULL OR "c" = '')</c> degenerates there — the second disjunct can never be true, and
/// the two halves of SEM-2's text/non-text split collapse into one. Adding Oracle therefore is NOT
/// just another implementation of this interface: it needs the <c>blank</c> composition itself made
/// overridable (or explicitly declared equivalent), which is a change to this file, not to a dialect.
/// Recorded here so that story discovers it while designing rather than after its artifacts
/// disagree.</para>
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
    /// </summary>
    string RenderSetMembership(
        string quotedColumn,
        bool negated,
        IReadOnlyList<object?> values,
        SemanticsResolver.CaseSensitivityMode caseSensitivity,
        ISqlParameterSink parameters);

    /// <summary>
    /// SEM-3 one <c>ORDER BY</c> key, including null placement. May legitimately expand to more than
    /// one comma-separated key: MySQL has no <c>NULLS FIRST</c>/<c>LAST</c> syntax and emulates it
    /// with a leading <c>ISNULL(col)</c> key, SQL Server with a <c>CASE WHEN col IS NULL</c> one.
    /// <paramref name="caseSensitivity"/> comes from
    /// <c>SemanticsResolver.ResolveSortOrGroupKeyCaseSensitivity</c>, which already restricts the
    /// collation mechanism to <c>string</c> keys — and SEM-1's "same mechanism in every context on a
    /// field" is why an ordering key collates in exactly the cases the exact-match family does, in
    /// both directions.
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
}
