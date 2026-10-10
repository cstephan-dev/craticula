using System.Text;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Sql;

/// <summary>
/// The SQLite dialect (TRD §5). Every rendering here is pinned byte-for-byte by
/// <c>spec/fixtures/dialect-parity/*/expected/sqlite.artifact.json</c> and follows
/// <c>spec/semantics.md</c>'s "Addendum — canonical SQL rendering" (SQL-0..SQL-7) for everything the
/// rule declares universal: <c>"</c> quoting, <c>?</c> positional placeholders,
/// <c>LIMIT ? OFFSET ?</c> paging, and <c>IN (...)</c>/<c>NOT IN (...)</c> with N scalar parameters
/// for SQL-4 set membership.
///
/// <para><b>Two things are genuinely SQLite's own, and both bite.</b></para>
/// <list type="bullet">
/// <item><description><b>Where <c>COLLATE NOCASE</c> attaches is per construct, not per dialect.</b>
/// SQLite resolves a binary comparison's collation from the LEFT operand's explicit collation, else
/// the right's, else the left's implicit one — so <c>col = ? COLLATE NOCASE</c> works and is what
/// <c>dialect-parity/006</c> pins. But for <c>x IN (y, z)</c> SQLite uses <em>x</em>'s collating
/// sequence and nothing else, so the same value-side trick would be silently ignored there; set
/// membership therefore collates the column (<c>col COLLATE NOCASE IN (?, ?)</c>), as does an
/// <c>ORDER BY</c> key. Getting this wrong produces valid SQL that quietly compares case-sensitively
/// — the exact failure SEM-1 exists to prevent.</description></item>
/// <item><description><b><c>LIKE</c> cannot express a case-SENSITIVE substring match at all</b>, so
/// this dialect switches operator rather than operand — see
/// <see cref="RenderSubstringMatch"/>.</description></item>
/// </list>
///
/// <para><b>Only the case-INsensitive direction has a spelling outside the substring family.</b>
/// SQLite's default collating sequence is <c>BINARY</c>, which already distinguishes case, so
/// <see cref="SemanticsResolver.CaseSensitivityMode.Sensitive"/> needs no mechanism on the scalar,
/// set-membership and sort-key paths — the one exception being <c>LIKE</c>, which ignores collating
/// sequences entirely and is why the substring family switches operator instead (D3, below).</para>
///
/// <para>SQLite has supported native <c>NULLS FIRST</c>/<c>NULLS LAST</c> since <b>3.30.0</b> (2019),
/// so SEM-3 needs no bucket-key emulation here and the sort keys read like Postgres's rather than
/// MySQL's or SQL Server's. That version is a host obligation, not an implementation detail: every
/// <c>sqlite</c> artifact in the suite pins the native syntax, so an older library rejects the
/// compiled statement outright.</para>
///
/// <para>Stateless and therefore safe to share; <see cref="Instance"/> exists so callers need not
/// allocate one per request.</para>
/// </summary>
public sealed class SqliteDialect : ISqlDialect
{
    public static readonly SqliteDialect Instance = new();

    /// <summary>SEM-1's case-insensitive mechanism here: SQLite's built-in <c>NOCASE</c> collating
    /// sequence. Built in rather than host-created (unlike Postgres's <c>craticula_ci</c>), and
    /// ASCII-only — the same limitation SQLite's <c>LIKE</c> has, and a real semantic difference from
    /// the other four dialects that the execution-equality tier (T-1b, #1624) will surface on
    /// non-ASCII data.</summary>
    public const string CaseInsensitiveCollation = "NOCASE";

    /// <summary>The <c>ESCAPE</c> character named in every emitted <c>LIKE</c>. SQLite has no default
    /// escape character at all, so SEM-7's "never a pattern without a matching <c>ESCAPE</c>" is not
    /// a formality here.</summary>
    private const char PatternEscape = '\\';

    public string Name => "sqlite";

    // SQL-7: SQLite distinguishes '' from NULL, so SEM-2's text arm keeps both disjuncts.
    public bool EmptyStringIsNull => false;

    // SEM-4: a datetime is TEXT here, compared character by character, so the bound is bound with the
    // same fixed three-digit fraction the host stores.
    public bool DatetimeBoundsAreFixedWidth => true;

    public string QuoteIdentifier(string identifier) =>
        // A literal '"' inside an identifier is doubled — the standard SQL escape, which SQLite
        // implements. (SQLite also accepts [..] and backticks for compatibility; the standard form is
        // the one SQL-1 pins for it.)
        '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    // Positional: the placeholder text is the same for every ordinal, so params[] order is the whole
    // contract — see SqlParameterBag.
    public string ParameterPlaceholder(int ordinal) => "?";

    public string RenderScalarComparison(
        string quotedColumn,
        SqlScalarComparison comparison,
        object? value,
        SemanticsResolver.CaseSensitivityMode caseSensitivity,
        ISqlParameterSink parameters)
    {
        // SEM-1, value-side: SQLite takes the left operand's explicit collation if it has one and the
        // right's otherwise, so collating the parameter governs the comparison while leaving the
        // column expression bare — the form dialect-parity/006 pins. Sensitive needs nothing: the
        // default BINARY sequence already distinguishes case (contrast SQL Server, whose stock
        // collation does not).
        var placeholder = parameters.Bind(value);
        var collated = caseSensitivity == SemanticsResolver.CaseSensitivityMode.Insensitive
            ? $"{placeholder} COLLATE {CaseInsensitiveCollation}"
            : placeholder;

        return $"{quotedColumn} {SqlComparisonSymbols.For(comparison)} {collated}";
    }

    /// <summary>
    /// SEM-7's substring family — the one place this dialect renders two structurally different
    /// predicates rather than one with a token swapped.
    ///
    /// <para><b>Case-insensitive: <c>LIKE</c>.</b> SQLite's <c>LIKE</c> is ASCII-case-insensitive
    /// unconditionally, so no collation is needed or possible (<c>LIKE</c> is a function call and
    /// ignores collating sequences entirely; case sensitivity is the connection-level
    /// <c>PRAGMA case_sensitive_like</c>, which a compiler must not depend on).</para>
    ///
    /// <para><b>Case-sensitive: <c>GLOB</c> (#1639, D3).</b> That same unconditional folding means
    /// <c>LIKE</c> <em>cannot</em> honour a field the schema declares
    /// <c>caseSensitivity: "sensitive"</c>; emitting it anyway would silently return
    /// case-insensitive rows — the mirror image of SEM-1's "must NOT default to case-sensitive just
    /// because the engine does". <c>GLOB</c> is SQLite's case-sensitive pattern operator, and it is a
    /// genuinely different pattern language, not a synonym: its wildcards are <c>*</c>/<c>?</c>
    /// rather than <c>%</c>/<c>_</c>, and <b>it has no <c>ESCAPE</c> clause at all</b>, so
    /// <see cref="SqlPatternEscaping.Escape"/> does not apply and
    /// <see cref="EscapeGlobPattern"/> is used instead.</para>
    ///
    /// <para><paramref name="negated"/> renders the negated operator itself (<c>NOT LIKE</c>/
    /// <c>NOT GLOB</c>) rather than an outer <c>NOT (...)</c>, per SQL-3, on both paths.</para>
    /// </summary>
    public string RenderSubstringMatch(
        string quotedColumn,
        SqlSubstringAnchor anchor,
        bool negated,
        string value,
        SemanticsResolver.CaseSensitivityMode caseSensitivity,
        ISqlParameterSink parameters)
    {
        // NotApplicable never reaches here — a substring match against a non-text field has no
        // defined rendering and SqlQueryBuilder rejects it — so this is a two-way choice, and the
        // one place in this dialect where the case-sensitive direction costs an operator rather than
        // a token.
        if (caseSensitivity != SemanticsResolver.CaseSensitivityMode.Insensitive)
        {
            var globPlaceholder = parameters.Bind(EscapeGlobPattern(value));
            var globPattern = anchor switch
            {
                SqlSubstringAnchor.Anywhere => $"'*' || {globPlaceholder} || '*'",
                SqlSubstringAnchor.Start => $"{globPlaceholder} || '*'",
                SqlSubstringAnchor.End => $"'*' || {globPlaceholder}",
                _ => throw new SqlCompilationException($"Unknown substring anchor '{anchor}'.")
            };

            return $"{quotedColumn} {(negated ? "NOT GLOB" : "GLOB")} {globPattern}";
        }

        var placeholder = parameters.Bind(SqlPatternEscaping.Escape(value, PatternEscape));
        var pattern = anchor switch
        {
            SqlSubstringAnchor.Anywhere => $"'%' || {placeholder} || '%'",
            SqlSubstringAnchor.Start => $"{placeholder} || '%'",
            SqlSubstringAnchor.End => $"'%' || {placeholder}",
            _ => throw new SqlCompilationException($"Unknown substring anchor '{anchor}'.")
        };

        return $"{quotedColumn} {(negated ? "NOT LIKE" : "LIKE")} {pattern} ESCAPE '{PatternEscape}'";
    }

    /// <summary>
    /// Makes <paramref name="value"/> match literally under <c>GLOB</c>. Deliberately NOT
    /// <see cref="SqlPatternEscaping.Escape"/> with a different character: <c>GLOB</c> accepts no
    /// <c>ESCAPE</c> clause in SQLite, so the only way to neutralise a metacharacter is to wrap it in
    /// a single-member character class — <c>*</c> becomes <c>[*]</c>, <c>?</c> becomes <c>[?]</c>,
    /// and <c>[</c> becomes <c>[[]</c>.
    ///
    /// <para><c>]</c> is deliberately left alone: outside a class it is an ordinary character to
    /// SQLite's matcher, and every class this method opens is closed immediately, so no <c>]</c> in
    /// the value can ever be read as closing one. <c>^</c> is likewise ordinary — it only negates a
    /// class when it is the first character <em>inside</em> one, and the classes emitted here always
    /// begin with the escaped character itself.</para>
    ///
    /// <para>Public because the class-wrapping rule deserves direct tests rather than only the one
    /// value <c>spec/fixtures/dialect-parity/021</c> happens to pin. That fixture (added by #1639,
    /// the suite's only case-sensitive substring match) is this path's artifact oracle and covers a
    /// literal <c>*</c>; the combinations around it — <c>?</c>, <c>[</c>, an already-class-shaped
    /// value, and the deliberately-unescaped <c>]</c>/<c>^</c> — are unit-tested in
    /// <c>DialectRenderingTests</c>.</para>
    /// </summary>
    public static string EscapeGlobPattern(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '*' or '?' or '[')
            {
                escaped.Append('[').Append(character).Append(']');
            }
            else
            {
                escaped.Append(character);
            }
        }

        return escaped.ToString();
    }

    public string RenderSetMembership(
        string quotedColumn,
        bool negated,
        IReadOnlyList<object?> values,
        SemanticsResolver.CaseSensitivityMode caseSensitivity,
        ISqlParameterSink parameters)
    {
        // SEM-1, COLUMN-side here and value-side in RenderScalarComparison, because SQLite resolves
        // `x IN (...)` from x's collating sequence alone — a collation on a list element is ignored.
        // See the class remarks: this asymmetry is SQLite's, not a stylistic inconsistency.
        var left = caseSensitivity == SemanticsResolver.CaseSensitivityMode.Insensitive
            ? $"{quotedColumn} COLLATE {CaseInsensitiveCollation}"
            : quotedColumn;

        // SQL-4: no ANY(array) here, so N scalar parameters.
        var placeholders = values.Select(parameters.Bind);
        return $"{left} {(negated ? "NOT IN" : "IN")} ({string.Join(", ", placeholders)})";
    }

    public string RenderSortKey(
        string quotedColumn,
        SortDirection direction,
        NullsPosition nullsPosition,
        SemanticsResolver.CaseSensitivityMode caseSensitivity)
    {
        var key = new StringBuilder(quotedColumn);

        // SEM-1: on the column, where SQLite's ORDER BY reads it, and the same collation mechanism
        // the exact-match family uses — never LIKE/GLOB — so a field cannot filter under one
        // mechanism and sort under another. Sensitive is BINARY, already the default.
        if (caseSensitivity == SemanticsResolver.CaseSensitivityMode.Insensitive)
        {
            key.Append(" COLLATE ").Append(CaseInsensitiveCollation);
        }

        key.Append(direction == SortDirection.Asc ? " ASC" : " DESC");

        // SEM-3: native since SQLite 3.30.0, so no bucket-key emulation — and always explicit, never
        // the engine's default, even where the two coincide.
        key.Append(nullsPosition == NullsPosition.First ? " NULLS FIRST" : " NULLS LAST");

        return key.ToString();
    }

    // Bound in the order they are spelled — limit first, like Postgres and unlike SQL Server's
    // OFFSET/FETCH. EXP-2's unpaginated case never reaches here.
    public string RenderPaging(int offset, int limit, ISqlParameterSink parameters) =>
        $"LIMIT {parameters.Bind(limit)} OFFSET {parameters.Bind(offset)}";

    // GRP-6: a row cap with no offset. SQLite's OFFSET is optional after LIMIT, like Postgres's.
    public string RenderRowLimit(int limit, ISqlParameterSink parameters) =>
        $"LIMIT {parameters.Bind(limit)}";
}
