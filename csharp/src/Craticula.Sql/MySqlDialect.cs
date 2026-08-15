using System.Text;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Sql;

/// <summary>
/// The MySQL / MariaDB dialect (TRD §5). Every rendering here is pinned byte-for-byte by
/// <c>spec/fixtures/dialect-parity/*/expected/mysql.artifact.json</c> and follows
/// <c>spec/semantics.md</c>'s "Addendum — canonical SQL rendering" (SQL-0..SQL-7) for everything the
/// rule declares universal: backtick quoting, <c>?</c> positional placeholders,
/// <c>LIMIT ? OFFSET ?</c> paging, <c>LIKE</c> over a <c>CONCAT(...)</c> pattern for SEM-7's
/// substring family, and <c>IN (...)</c>/<c>NOT IN (...)</c> with N scalar parameters for SQL-4 set
/// membership.
///
/// <para><b>Case sensitivity is DDL-side on this engine, so this dialect emits no collation in any
/// of the three modes.</b> A MySQL column already carries a collation (the server default family is
/// <c>_ci</c>, e.g. <c>utf8mb4_0900_ai_ci</c>), and comparisons, <c>LIKE</c>, <c>IN</c> and
/// <c>ORDER BY</c> all use it — so SEM-1's index-friendly mechanism is "declare the column with the
/// collation the schema says the field has", not "spell something in the statement".
/// <c>spec/semantics.md</c>'s SEM-1 worked example pins exactly that (<c>WHERE name = :v</c>,
/// unadorned).</para>
///
/// <para><b>The three modes coincide here, but for three different reasons — they are not one
/// answer.</b> <see cref="SemanticsResolver.CaseSensitivityMode.Insensitive"/> is the column's own
/// default collation doing the folding.
/// <see cref="SemanticsResolver.CaseSensitivityMode.NotApplicable"/> has nothing to collate.
/// <see cref="SemanticsResolver.CaseSensitivityMode.Sensitive"/> is a <b>deliberate deferral to host
/// DDL</b>, and the one mode whose silence a reader must not mistake for "handled": MySQL's default
/// collation folds case, so a field a <c>GridSchema</c> declares
/// <c>caseSensitivity: "sensitive"</c> is honoured only if its column is declared with a
/// <c>_bin</c>/<c>_as_cs</c> collation. This compiler does not force it inline because no collation
/// name is pinned for that direction on this engine — no artifact in the suite contains one, MySQL's
/// case-sensitive collation names are charset-specific (<c>utf8mb4_bin</c> vs
/// <c>utf8mb4_0900_as_cs</c> vs MariaDB's own set) rather than one portable spelling, and inventing
/// one would put an unverifiable token into the oracle. SQL Server, which has one universally
/// available name, does spell it (<see cref="MssqlDialect.CaseSensitiveCollation"/>). The host
/// obligation is recorded in <c>spec/fixtures/dialect-parity/README.md</c> and belongs on PERF-4's
/// per-backend index-guidance page.</para>
///
/// <para>Stateless and therefore safe to share; <see cref="Instance"/> exists so callers need not
/// allocate one per request.</para>
/// </summary>
public sealed class MySqlDialect : ISqlDialect
{
    public static readonly MySqlDialect Instance = new();

    /// <summary>The <c>ESCAPE</c> character named in every emitted <c>LIKE</c> — the same character
    /// SEC-4's escaping prefixes with, and the same one every other v1 dialect uses. Only its
    /// spelling inside the SQL string literal differs here; see
    /// <see cref="PatternEscapeLiteral"/>.</summary>
    private const char PatternEscape = '\\';

    /// <summary>
    /// How <see cref="PatternEscape"/> is written inside a MySQL string literal — <c>\\</c>, not
    /// <c>\</c>.
    ///
    /// <para><b>This is a correctness fix, not a formatting nicety (#1639, D1).</b> With
    /// <c>NO_BACKSLASH_ESCAPES</c> off — MySQL's default — a backslash is the string-literal escape
    /// character, so <c>ESCAPE '\'</c> escapes the closing quote and the literal never terminates:
    /// the statement does not parse. Postgres (<c>standard_conforming_strings = on</c>), SQLite and
    /// SQL Server all treat <c>'\'</c> as a one-character string, which is why the same spelling is
    /// correct in their artifacts and wrong only here. The escape <em>character</em> is unchanged, so
    /// <see cref="SqlPatternEscaping.Escape"/> and the bound value are identical across all
    /// dialects — this affects the SQL text alone. T-1a artifact equality cannot see this class of
    /// defect (it compares strings); it was found by reading, like SQL-1's camelCase-folding bug, and
    /// <c>dialect-parity/007</c>'s mysql artifact was re-authored for it.</para>
    /// </summary>
    private const string PatternEscapeLiteral = @"\\";

    public string Name => "mysql";

    // SQL-7: MySQL/MariaDB distinguish '' from NULL, so SEM-2's text arm keeps both disjuncts.
    public bool EmptyStringIsNull => false;

    public string QuoteIdentifier(string identifier) =>
        // A literal backtick inside a backtick-quoted identifier is doubled — MySQL's own escape.
        // Backticks rather than '"' because '"' only quotes identifiers under ANSI_QUOTES, which is
        // off by default and is a session-level mode a compiler must not assume of a host.
        '`' + identifier.Replace("`", "``", StringComparison.Ordinal) + '`';

    // Positional: the placeholder text is the same for every ordinal, and the driver matches
    // parameters to '?' by position — which is exactly why SqlParameterBag, not the dialect, owns the
    // ordering, and why params[] must stay first-appearance ordered.
    public string ParameterPlaceholder(int ordinal) => "?";

    public string RenderScalarComparison(
        string quotedColumn,
        SqlScalarComparison comparison,
        object? value,
        SemanticsResolver.CaseSensitivityMode caseSensitivity,
        ISqlParameterSink parameters) =>
        // SEM-1: nothing to spell in any of the three modes — see the class remarks.
        // `caseSensitivity` is still taken (and deliberately unused) because the decision is the
        // resolver's; this dialect's answer to it is "the column's declared collation, not the
        // statement", which for the Sensitive mode is a host DDL obligation rather than a no-op.
        $"{quotedColumn} {SqlComparisonSymbols.For(comparison)} {parameters.Bind(value)}";

    public string RenderSubstringMatch(
        string quotedColumn,
        SqlSubstringAnchor anchor,
        bool negated,
        string value,
        SemanticsResolver.CaseSensitivityMode caseSensitivity,
        ISqlParameterSink parameters)
    {
        var op = negated ? "NOT LIKE" : "LIKE";
        var placeholder = parameters.Bind(SqlPatternEscaping.Escape(value, PatternEscape));

        // CONCAT(), not '||': in MySQL's default mode '||' is logical OR, so the Postgres/SQLite
        // spelling would silently compile to a boolean expression instead of a pattern.
        var pattern = anchor switch
        {
            SqlSubstringAnchor.Anywhere => $"CONCAT('%', {placeholder}, '%')",
            SqlSubstringAnchor.Start => $"CONCAT({placeholder}, '%')",
            SqlSubstringAnchor.End => $"CONCAT('%', {placeholder})",
            _ => throw new SqlCompilationException($"Unknown substring anchor '{anchor}'.")
        };

        return $"{quotedColumn} {op} {pattern} ESCAPE '{PatternEscapeLiteral}'";
    }

    public string RenderSetMembership(
        string quotedColumn,
        bool negated,
        IReadOnlyList<object?> values,
        SemanticsResolver.CaseSensitivityMode caseSensitivity,
        ISqlParameterSink parameters)
    {
        // SQL-4: no ANY(array) here, so N scalar parameters.
        var placeholders = values.Select(parameters.Bind);
        return $"{quotedColumn} {(negated ? "NOT IN" : "IN")} ({string.Join(", ", placeholders)})";
    }

    public string RenderSortKey(
        string quotedColumn,
        SortDirection direction,
        NullsPosition nullsPosition,
        SemanticsResolver.CaseSensitivityMode caseSensitivity)
    {
        // SEM-3, emulated: MySQL has no NULLS FIRST/LAST, so one IR key becomes TWO ORDER BY keys.
        // ISNULL(col) here is MySQL's SINGLE-argument null test returning 1/0 — NOT SQL Server's
        // two-argument COALESCE equivalent of the same name (MssqlDialect uses CASE WHEN for that
        // reason). NULLs land in bucket 1, so ASC pushes them last and DESC pulls them first,
        // independently of the column's own direction.
        var nullBucket = nullsPosition == NullsPosition.First ? "DESC" : "ASC";
        var order = direction == SortDirection.Asc ? "ASC" : "DESC";

        return new StringBuilder("ISNULL(")
            .Append(quotedColumn)
            .Append(") ")
            .Append(nullBucket)
            .Append(", ")
            .Append(quotedColumn)
            .Append(' ')
            .Append(order)
            .ToString();
    }

    // Bound in the order they are spelled — limit first, like Postgres and unlike SQL Server's
    // OFFSET/FETCH. EXP-2's unpaginated case never reaches here.
    public string RenderPaging(int offset, int limit, ISqlParameterSink parameters) =>
        $"LIMIT {parameters.Bind(limit)} OFFSET {parameters.Bind(offset)}";

    // GRP-6: a row cap with no offset. MySQL's OFFSET is optional after LIMIT (its own
    // `LIMIT offset, count` shorthand is deliberately not used — the ordering of the two numbers is
    // reversed there, which is a trap for a reader comparing this with RenderPaging above).
    public string RenderRowLimit(int limit, ISqlParameterSink parameters) =>
        $"LIMIT {parameters.Bind(limit)}";
}
