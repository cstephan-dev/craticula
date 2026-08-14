using System.Globalization;
using System.Text;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Sql;

/// <summary>
/// The SQL Server / Azure SQL dialect (TRD §5). Every rendering here is pinned byte-for-byte by
/// <c>spec/fixtures/dialect-parity/*/expected/mssql.artifact.json</c> and follows
/// <c>spec/semantics.md</c>'s "Addendum — canonical SQL rendering" (SQL-0..SQL-6) for everything the
/// rule declares universal: <c>[…]</c> quoting, <c>@p1</c> named placeholders,
/// <c>OFFSET ... ROWS FETCH NEXT ... ROWS ONLY</c> paging, a column-side
/// <c>COLLATE Latin1_General_CI_AS</c> for SEM-1's exact-match family, <c>LIKE</c> over a <c>+</c>
/// -concatenated pattern for SEM-7's substring family, and <c>IN (...)</c>/<c>NOT IN (...)</c> with
/// N scalar parameters for SQL-4 set membership.
///
/// <para><b>This is the one v1 dialect that spells BOTH case-sensitivity directions.</b> SQL Server
/// and Azure SQL ship a case-INsensitive default collation (<c>SQL_Latin1_General_CP1_CI_AS</c>), so
/// leaving a <see cref="SemanticsResolver.CaseSensitivityMode.Sensitive"/> field's comparison bare
/// does not honour the schema's <c>caseSensitivity: "sensitive"</c> declaration — it silently returns
/// the rows the host asked to exclude, which is SEM-1's second Must-Not with the sign flipped and the
/// same class of defect D3 pays an operator switch to avoid on SQLite. Hence
/// <see cref="CaseSensitiveCollation"/>, applied wherever <see cref="CaseInsensitiveCollation"/>
/// would be. What must NOT gain a collation is
/// <see cref="SemanticsResolver.CaseSensitivityMode.NotApplicable"/>: SQL Server rejects a
/// <c>COLLATE</c> clause on an <c>int</c>/<c>date</c> expression outright, which is exactly why the
/// seam carries three values rather than a <c>bool</c>.</para>
///
/// <para>Two renderings here are T-SQL facts rather than style choices, and both are the kind of
/// thing a port copying another dialect gets wrong:</para>
/// <list type="bullet">
/// <item><description>T-SQL has no <c>NULLS FIRST</c>/<c>NULLS LAST</c>, and its own <c>ISNULL</c> is
/// the <b>two-argument</b> <c>COALESCE</c> equivalent, not MySQL's single-argument null test — so
/// reusing MySQL's <c>ISNULL(col)</c> spelling here would not be a naming preference but a
/// compile error at best and a silent mis-order at worst. SEM-3's null placement is emulated with
/// <c>CASE WHEN col IS NULL THEN 1 ELSE 0 END</c> instead (<c>dialect-parity/003</c>, <c>009</c>,
/// <c>010</c>).</description></item>
/// <item><description><c>OFFSET/FETCH</c> spells the offset <em>before</em> the row count, so its two
/// paging parameters bind in the opposite order to <c>LIMIT/OFFSET</c>'s — the axis
/// <see cref="ISqlDialect.RenderPaging"/> exists for, and visible in every artifact's <c>params</c>
/// tail as <c>[offset, limit]</c> where the other three dialects have
/// <c>[limit, offset]</c>.</description></item>
/// </list>
///
/// <para>Stateless and therefore safe to share; <see cref="Instance"/> exists so callers need not
/// allocate one per request.</para>
/// </summary>
public sealed class MssqlDialect : ISqlDialect
{
    public static readonly MssqlDialect Instance = new();

    /// <summary>
    /// SEM-1's case-insensitive collation for this dialect, pinned across the suite exactly as
    /// Postgres's <see cref="PostgresDialect.CaseInsensitiveCollation"/> is: one name, chosen here
    /// rather than declared per <c>GridSchema</c>, so no host invents its own and no fixture has to
    /// carry it. <c>Latin1_General_CI_AS</c> is SQL Server's ubiquitous
    /// case-insensitive/accent-sensitive collation and is what the fixtures' artifacts contain
    /// (<c>dialect-parity/006</c>, <c>007</c>).
    /// </summary>
    public const string CaseInsensitiveCollation = "Latin1_General_CI_AS";

    /// <summary>
    /// SEM-1's case-SENSITIVE collation for this dialect — the mechanical counterpart of
    /// <see cref="CaseInsensitiveCollation"/>, same family and same accent sensitivity, only the
    /// <c>CI</c>/<c>CS</c> component differs, so the two never disagree on anything but case.
    ///
    /// <para>It exists because on this engine "emit nothing" does NOT mean "compare
    /// case-sensitively": the stock server collation is <c>SQL_Latin1_General_CP1_CI_AS</c>, which
    /// folds case. A schema-declared case-sensitive field left bare would therefore match rows the
    /// host explicitly excluded, and the SQL would read as though it were correct.</para>
    /// </summary>
    public const string CaseSensitiveCollation = "Latin1_General_CS_AS";

    /// <summary>The <c>ESCAPE</c> character named in every emitted <c>LIKE</c>. SEM-7 requires naming
    /// one explicitly; T-SQL has no default at all, so omitting the clause would leave a user's
    /// literal <c>%</c> as a wildcard.</summary>
    private const char PatternEscape = '\\';

    public string Name => "mssql";

    public string QuoteIdentifier(string identifier) =>
        // A literal ']' inside a bracket-quoted identifier is doubled — T-SQL's own escape, and the
        // one place ProbeDialect's superficially identical '[' + name + ']' must NOT be copied: it
        // omits the doubling, which would turn a host-declared oddity into a truncated identifier
        // (and, with the right name, into injectable SQL) rather than a quoted one.
        '[' + identifier.Replace("]", "]]", StringComparison.Ordinal) + ']';

    public string ParameterPlaceholder(int ordinal) =>
        "@p" + (ordinal + 1).ToString(CultureInfo.InvariantCulture);

    public string RenderScalarComparison(
        string quotedColumn,
        SqlScalarComparison comparison,
        object? value,
        SemanticsResolver.CaseSensitivityMode caseSensitivity,
        ISqlParameterSink parameters)
    {
        // SEM-1: like Postgres, SQL Server attaches the collation to the COLUMN side, which keeps a
        // plain index on that collation usable. (SQLite is the outlier that collates the value.)
        var left = quotedColumn + Collation(caseSensitivity);
        return $"{left} {SqlComparisonSymbols.For(comparison)} {parameters.Bind(value)}";
    }

    /// <summary>
    /// The <c>COLLATE</c> suffix (including its leading space) for <paramref name="caseSensitivity"/>,
    /// or the empty string when no collation may be spelled at all. One place, because SEM-1 requires
    /// the same mechanism in every context on a field: the scalar, substring, set-membership and
    /// sort-key paths all attach exactly this.
    /// </summary>
    private static string Collation(SemanticsResolver.CaseSensitivityMode caseSensitivity) => caseSensitivity switch
    {
        SemanticsResolver.CaseSensitivityMode.Insensitive => $" COLLATE {CaseInsensitiveCollation}",
        SemanticsResolver.CaseSensitivityMode.Sensitive => $" COLLATE {CaseSensitiveCollation}",

        // NotApplicable: not a text comparison, and T-SQL raises "COLLATE clause cannot be used on
        // expressions of type int" rather than ignoring one — so this arm is a hard requirement, not
        // a tidiness preference.
        SemanticsResolver.CaseSensitivityMode.NotApplicable => string.Empty,

        _ => throw new SqlCompilationException($"Unknown case-sensitivity mode '{caseSensitivity}'.")
    };

    public string RenderSubstringMatch(
        string quotedColumn,
        SqlSubstringAnchor anchor,
        bool negated,
        string value,
        SemanticsResolver.CaseSensitivityMode caseSensitivity,
        ISqlParameterSink parameters)
    {
        // SEM-1: T-SQL has no ILIKE, so the substring family reaches case-insensitivity through the
        // same collation the exact-match family uses — the operator itself never changes. That is
        // still the PatternMatch mechanism as far as SemanticsResolver is concerned; only the
        // spelling coincides with the collation family's. The case-SENSITIVE direction needs the
        // collation just as much: T-SQL's LIKE folds case under the server's stock CI collation.
        var collation = Collation(caseSensitivity);
        var op = negated ? "NOT LIKE" : "LIKE";

        var placeholder = parameters.Bind(EscapePattern(value));

        // '+' is T-SQL's string concatenation operator; there is no '||' unless PIPES_AS_CONCAT is
        // enabled, which is not something a compiler may assume of a host's connection.
        var pattern = anchor switch
        {
            SqlSubstringAnchor.Anywhere => $"'%' + {placeholder} + '%'",
            SqlSubstringAnchor.Start => $"{placeholder} + '%'",
            SqlSubstringAnchor.End => $"'%' + {placeholder}",
            _ => throw new SqlCompilationException($"Unknown substring anchor '{anchor}'.")
        };

        return $"{quotedColumn}{collation} {op} {pattern} ESCAPE '{PatternEscape}'";
    }

    /// <summary>
    /// SEC-4's shared escaping, plus T-SQL's <b>fourth</b> <c>LIKE</c> metacharacter.
    ///
    /// <para><see cref="SqlPatternEscaping.Escape"/> covers the three every v1 dialect shares
    /// (<c>\</c>, <c>%</c>, <c>_</c>). T-SQL adds <c>[</c>, which opens a character class — so an
    /// unescaped user value <c>A[BC]</c> matches <c>AB</c> or <c>AC</c> rather than itself, and
    /// <c>[^x]</c>/<c>[a-z]</c> widen the match set further. That is SEM-7's own Must-Not ("a
    /// user-supplied literal still behaves as a wildcard") on the one dialect where the shared set is
    /// not the whole set, so it is escaped here rather than by widening the shared routine, which
    /// would put a needless <c>\[</c> into the Postgres/MySQL/SQLite arms of every fixture.</para>
    ///
    /// <para><c>]</c>, <c>^</c> and <c>-</c> need no escaping, by the same closed-class argument D3
    /// writes down for <c>GLOB</c>: all three are special only <em>inside</em> a class, and after this
    /// pass no <c>[</c> from the value can open one — the only classes left in the pattern are the
    /// anchoring <c>'%'</c> literals the dialect itself concatenates, which contain none of them.
    /// Escaping them anyway would be harmless under T-SQL but would change the bound value, and the
    /// bound value is what the artifact oracle pins.</para>
    ///
    /// <para>Composed rather than reimplemented as one loop: the <c>\</c> this pass introduces is an
    /// escape character, never a literal one, so it must NOT be doubled — which is precisely what
    /// running <see cref="SqlPatternEscaping.Escape"/> first and this second guarantees. (A literal
    /// <c>\</c> in the value was already doubled by that first pass, and carries no <c>[</c> for this
    /// one to see.)</para>
    /// </summary>
    private static string EscapePattern(string value) =>
        SqlPatternEscaping.Escape(value, PatternEscape).Replace("[", $"{PatternEscape}[", StringComparison.Ordinal);

    public string RenderSetMembership(
        string quotedColumn,
        bool negated,
        IReadOnlyList<object?> values,
        SemanticsResolver.CaseSensitivityMode caseSensitivity,
        ISqlParameterSink parameters)
    {
        var left = quotedColumn + Collation(caseSensitivity);

        // SQL-4: no ANY(array) in T-SQL, so N scalar parameters — the placeholder count depends on
        // the value count here, which is precisely why the interface takes the whole list.
        var placeholders = values.Select(parameters.Bind);
        return $"{left} {(negated ? "NOT IN" : "IN")} ({string.Join(", ", placeholders)})";
    }

    public string RenderSortKey(
        string quotedColumn,
        SortDirection direction,
        NullsPosition nullsPosition,
        SemanticsResolver.CaseSensitivityMode caseSensitivity)
    {
        // SEM-3, emulated: one IR key becomes TWO ORDER BY keys, a leading null bucket then the
        // column itself. `CASE WHEN col IS NULL THEN 1 ELSE 0 END` puts NULLs in bucket 1, so sorting
        // the bucket ASC pushes them last and DESC pulls them first — independently of the column's
        // own direction, which is what makes `salary DESC NULLS FIRST` reachable at all.
        var nullBucket = nullsPosition == NullsPosition.First ? "DESC" : "ASC";

        var key = new StringBuilder("CASE WHEN ")
            .Append(quotedColumn)
            .Append(" IS NULL THEN 1 ELSE 0 END ")
            .Append(nullBucket)
            .Append(", ")
            .Append(quotedColumn);

        // SEM-1: sorting uses the collation mechanism, the same one the exact-match family uses, so
        // a field cannot filter under one mechanism and sort under another — in EITHER direction,
        // since ORDER BY on this engine folds case under the stock CI collation exactly as `=` does.
        key.Append(Collation(caseSensitivity));

        return key.Append(direction == SortDirection.Asc ? " ASC" : " DESC").ToString();
    }

    // The offset binds BEFORE the row count, the reverse of Postgres's LIMIT/OFFSET — bound in the
    // order they are spelled, so params[] stays first-appearance ordered end to end. EXP-2's
    // unpaginated case never reaches here: the builder resolves it to "no clause" before asking.
    // (T-SQL additionally requires an ORDER BY for OFFSET/FETCH to be legal at all, which SEM-6's
    // unconditional stableKey tiebreaker guarantees for free.)
    public string RenderPaging(int offset, int limit, ISqlParameterSink parameters) =>
        $"OFFSET {parameters.Bind(offset)} ROWS FETCH NEXT {parameters.Bind(limit)} ROWS ONLY";
}
