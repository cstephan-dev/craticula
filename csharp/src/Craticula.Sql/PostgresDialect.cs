using System.Globalization;
using System.Text;
using Craticula.Core.Ir;

namespace Craticula.Sql;

/// <summary>
/// The reference dialect (TRD §5). Every rendering here is pinned byte-for-byte by
/// <c>spec/fixtures/*/expected/postgres.artifact.json</c> and documented in <c>spec/semantics.md</c>'s
/// "Addendum — canonical SQL rendering" (SQL-0..SQL-4): <c>"</c> quoting, <c>$n</c> placeholders,
/// <c>LIMIT $n OFFSET $n</c>, the host-created <c>craticula_ci</c> ICU collation for SEM-1's
/// exact-match family, <c>ILIKE ... ESCAPE '\'</c> for SEM-7's substring family, and
/// <c>= ANY($1)</c>/<c>&lt;&gt; ALL($1)</c> for SQL-4 set membership.
///
/// <para>Stateless and therefore safe to share; <see cref="Instance"/> exists so callers need not
/// allocate one per request.</para>
/// </summary>
public sealed class PostgresDialect : ISqlDialect
{
    public static readonly PostgresDialect Instance = new();

    /// <summary>
    /// SEM-1's one canonical, explicitly host-created ICU collation name — a fixed part of this
    /// dialect's contract, deliberately NOT a <c>GridSchema</c> value: pinning one name across the
    /// suite and every language port is what stops each host from inventing its own and every
    /// fixture from having to declare it. The host DDL is
    /// <c>CREATE COLLATION craticula_ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);</c>
    /// (PERF-4's per-backend index-guidance page).
    /// </summary>
    public const string CaseInsensitiveCollation = "craticula_ci";

    /// <summary>The <c>ESCAPE</c> character named in every emitted <c>LIKE</c>/<c>ILIKE</c> — the
    /// same character SEC-4's escaping prefixes with. PostgreSQL's own default happens to be <c>\</c>
    /// too, but SEM-7 requires naming it explicitly anyway, since three of the four v1 SQL dialects
    /// have no default at all.</summary>
    private const char PatternEscape = '\\';

    public string Name => "postgres";

    public string QuoteIdentifier(string identifier) =>
        // A literal '"' inside an identifier is doubled — the standard SQL escape. No fixture
        // exercises it (no schema declares such a column), but leaving it unhandled would turn a
        // host-declared oddity into malformed SQL rather than a quoted identifier.
        '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    public string ParameterPlaceholder(int ordinal) =>
        "$" + (ordinal + 1).ToString(CultureInfo.InvariantCulture);

    public string RenderScalarComparison(
        string quotedColumn,
        SqlScalarComparison comparison,
        object? value,
        bool caseInsensitive,
        ISqlParameterSink parameters)
    {
        // SEM-1: on Postgres the collation attaches to the column side of the comparison, which is
        // also what keeps a plain B-tree index on that collation usable.
        var left = caseInsensitive ? $"{quotedColumn} COLLATE {CaseInsensitiveCollation}" : quotedColumn;
        return $"{left} {Symbol(comparison)} {parameters.Bind(value)}";
    }

    public string RenderSubstringMatch(
        string quotedColumn,
        SqlSubstringAnchor anchor,
        bool negated,
        string value,
        bool caseInsensitive,
        ISqlParameterSink parameters)
    {
        // SEM-1: the substring family uses the pattern-match mechanism, so case-insensitivity is
        // ILIKE rather than a collation. A case-SENSITIVE field falls back to plain LIKE.
        var op = (negated, caseInsensitive) switch
        {
            (false, true) => "ILIKE",
            (false, false) => "LIKE",
            (true, true) => "NOT ILIKE",
            (true, false) => "NOT LIKE"
        };

        var placeholder = parameters.Bind(SqlPatternEscaping.Escape(value, PatternEscape));
        var pattern = anchor switch
        {
            SqlSubstringAnchor.Anywhere => $"'%' || {placeholder} || '%'",
            SqlSubstringAnchor.Start => $"{placeholder} || '%'",
            SqlSubstringAnchor.End => $"'%' || {placeholder}",
            _ => throw new SqlCompilationException($"Unknown substring anchor '{anchor}'.")
        };

        return $"{quotedColumn} {op} {pattern} ESCAPE '{PatternEscape}'";
    }

    public string RenderSetMembership(
        string quotedColumn,
        bool negated,
        IReadOnlyList<object?> values,
        bool caseInsensitive,
        ISqlParameterSink parameters)
    {
        var left = caseInsensitive ? $"{quotedColumn} COLLATE {CaseInsensitiveCollation}" : quotedColumn;

        // SQL-4: ONE array parameter, so the placeholder count never depends on the value count.
        var placeholder = parameters.Bind(values);
        return negated ? $"{left} <> ALL({placeholder})" : $"{left} = ANY({placeholder})";
    }

    public string RenderSortKey(
        string quotedColumn,
        SortDirection direction,
        NullsPosition nullsPosition,
        bool caseInsensitive)
    {
        var key = new StringBuilder(quotedColumn);

        // SEM-1: the collation sits between the column and the direction, and is the SAME mechanism
        // the exact-match family uses above — never ILIKE — so a field can't sort under one
        // mechanism and filter under another.
        if (caseInsensitive)
        {
            key.Append(" COLLATE ").Append(CaseInsensitiveCollation);
        }

        key.Append(direction == SortDirection.Asc ? " ASC" : " DESC");

        // SEM-3: always explicit, never the engine's default — even though Postgres's own default
        // happens to coincide with the adapter's pinned asc/last, desc/first mapping.
        key.Append(nullsPosition == NullsPosition.First ? " NULLS FIRST" : " NULLS LAST");

        return key.ToString();
    }

    // Bound in the order they are spelled, which is what makes params[] first-appearance ordered end
    // to end (SQL Server's OFFSET/FETCH binds the same two values the other way round — the axis this
    // method lives in the dialect for). EXP-2's unpaginated case never reaches here: the builder
    // resolves it to "no clause" before asking.
    public string RenderPaging(int offset, int limit, ISqlParameterSink parameters) =>
        $"LIMIT {parameters.Bind(limit)} OFFSET {parameters.Bind(offset)}";

    private static string Symbol(SqlScalarComparison comparison) => comparison switch
    {
        SqlScalarComparison.Equal => "=",
        SqlScalarComparison.NotEqual => "<>",
        SqlScalarComparison.GreaterThan => ">",
        SqlScalarComparison.GreaterThanOrEqual => ">=",
        SqlScalarComparison.LessThan => "<",
        SqlScalarComparison.LessThanOrEqual => "<=",
        _ => throw new SqlCompilationException($"Unknown scalar comparison '{comparison}'.")
    };

}
