namespace Craticula.Sql;

/// <summary>
/// The six <see cref="SqlScalarComparison"/> operators' SQL spellings, shared by every dialect.
///
/// <para><b>Why this is not a method on <see cref="ISqlDialect"/> and not copied into each one</b>
/// — the same reasoning <see cref="SqlPatternEscaping"/> records for SEC-4's escaping loop. These
/// six are ANSI and vary in no v1 SQL dialect: the table was byte-identical in all four
/// implementations, and <c>DialectRenderingTests.Every_Dialect_Spells_The_Comparison_Symbols_Identically</c>
/// asserts that it must stay so. Four copies of one invariant table means four places for a dialect
/// to "helpfully" emit T-SQL's <c>!=</c> or MySQL's <c>&lt;=&gt;</c> — spellings which are accepted by
/// those engines, so the mistake produces a running query and a fixture diff rather than an error,
/// and only in whichever dialect happens to have a fixture for that operator.</para>
///
/// <para>A future dialect that genuinely needs a different spelling overrides it in its own
/// <c>RenderScalarComparison</c>; it does not need this to be an interface member to do so.</para>
/// </summary>
internal static class SqlComparisonSymbols
{
    /// <summary>The SQL operator token for <paramref name="comparison"/>.</summary>
    /// <exception cref="SqlCompilationException"><paramref name="comparison"/> is not one of the six
    /// members — unreachable while the enum is total, and thrown rather than defaulted so a member
    /// added later fails loudly instead of silently rendering as <c>=</c>.</exception>
    public static string For(SqlScalarComparison comparison) => comparison switch
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
