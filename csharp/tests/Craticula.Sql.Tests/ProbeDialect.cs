using System.Globalization;
using Craticula.Core.Ir;

namespace Craticula.Sql.Tests;

/// <summary>
/// A second <see cref="ISqlDialect"/> that exists ONLY to prove BM-1's Definition of Done — "adding
/// a new dialect requires zero changes to the query builder" — is true today rather than a promise
/// #1639 will discover is false.
///
/// <para><b>Deliberately not any real engine.</b> Its renderings are a chimera: SQL Server's
/// <c>[bracket]</c> quoting, Oracle-style <c>:p1</c> placeholders, an <c>OFFSET/FETCH</c> paging
/// clause that binds its two values in the OPPOSITE order to Postgres's <c>LIMIT/OFFSET</c>, SQLite's
/// value-side collation position, N scalar parameters for an in-list instead of one array, a
/// <c>CONCAT</c>-built <c>LIKE</c> pattern with a non-backslash escape character, and a
/// <c>CASE WHEN ... IS NULL</c> null-ordering emulation that expands one sort key into two. Each of
/// those is a real thing some v1 dialect does, and together they exercise every axis the interface
/// claims to abstract — but the combination belongs to no engine, so this cannot be mistaken for (or
/// quietly grown into) #1639's actual MySQL/SQL Server/SQLite work.</para>
/// </summary>
internal sealed class ProbeDialect : ISqlDialect
{
    public static readonly ProbeDialect Instance = new();

    private const char PatternEscape = '!';

    public string Name => "probe";

    public string QuoteIdentifier(string identifier) => '[' + identifier + ']';

    public string ParameterPlaceholder(int ordinal) => ":p" + (ordinal + 1).ToString(CultureInfo.InvariantCulture);

    public string RenderScalarComparison(
        string quotedColumn, SqlScalarComparison comparison, object? value, bool caseInsensitive, ISqlParameterSink parameters)
    {
        var symbol = comparison switch
        {
            SqlScalarComparison.Equal => "=",
            SqlScalarComparison.NotEqual => "<>",
            SqlScalarComparison.GreaterThan => ">",
            SqlScalarComparison.GreaterThanOrEqual => ">=",
            SqlScalarComparison.LessThan => "<",
            SqlScalarComparison.LessThanOrEqual => "<=",
            _ => throw new SqlCompilationException($"Unknown scalar comparison '{comparison}'.")
        };

        // Collation on the VALUE side, unlike Postgres's column side.
        var placeholder = parameters.Bind(value);
        return caseInsensitive
            ? $"{quotedColumn} {symbol} {placeholder} COLLATE PROBE_CI"
            : $"{quotedColumn} {symbol} {placeholder}";
    }

    public string RenderSubstringMatch(
        string quotedColumn, SqlSubstringAnchor anchor, bool negated, string value, bool caseInsensitive, ISqlParameterSink parameters)
    {
        var placeholder = parameters.Bind(SqlPatternEscaping.Escape(value, PatternEscape));
        var pattern = anchor switch
        {
            SqlSubstringAnchor.Anywhere => $"CONCAT('%', {placeholder}, '%')",
            SqlSubstringAnchor.Start => $"CONCAT({placeholder}, '%')",
            SqlSubstringAnchor.End => $"CONCAT('%', {placeholder})",
            _ => throw new SqlCompilationException($"Unknown substring anchor '{anchor}'.")
        };

        var op = negated ? "NOT LIKE" : "LIKE";
        var collation = caseInsensitive ? " COLLATE PROBE_CI" : string.Empty;
        return $"{quotedColumn}{collation} {op} {pattern} ESCAPE '{PatternEscape}'";
    }

    public string RenderSetMembership(
        string quotedColumn, bool negated, IReadOnlyList<object?> values, bool caseInsensitive, ISqlParameterSink parameters)
    {
        // N scalar parameters, not one array — the SQL-4 axis that makes params.Length itself a
        // dialect decision.
        var placeholders = values.Select(parameters.Bind);
        var collation = caseInsensitive ? " COLLATE PROBE_CI" : string.Empty;
        return $"{quotedColumn}{collation} {(negated ? "NOT IN" : "IN")} ({string.Join(", ", placeholders)})";
    }

    public string RenderSortKey(string quotedColumn, SortDirection direction, NullsPosition nullsPosition, bool caseInsensitive)
    {
        // No native NULLS FIRST/LAST: one IR sort key expands into TWO ORDER BY keys.
        var nullBucket = nullsPosition == NullsPosition.First ? "DESC" : "ASC";
        var collation = caseInsensitive ? " COLLATE PROBE_CI" : string.Empty;
        var order = direction == SortDirection.Asc ? "ASC" : "DESC";

        return $"CASE WHEN {quotedColumn} IS NULL THEN 1 ELSE 0 END {nullBucket}, {quotedColumn}{collation} {order}";
    }

    // OFFSET before FETCH: the two paging values bind in the opposite order to Postgres's — the one
    // axis of paging that really is a dialect's own, now that the builder resolves the row window.
    public string RenderPaging(int offset, int limit, ISqlParameterSink parameters) =>
        $"OFFSET {parameters.Bind(offset)} ROWS FETCH NEXT {parameters.Bind(limit)} ROWS ONLY";
}
