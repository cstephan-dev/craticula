using System.Data;
using System.Text;
using Craticula.Sql;
using Dapper;

namespace Craticula.Dapper;

/// <summary>
/// Binds a compiled <see cref="SqlQuery"/>'s flat, ordered <see cref="SqlQuery.Parameters"/> to whatever
/// placeholder syntax the dialect that produced it actually rendered into <see cref="SqlQuery.Sql"/> —
/// <c>$1</c> (Postgres), <c>@p1</c> (SQL Server), <c>:p1</c> (Oracle, bound without its leading colon —
/// ODP.NET's own convention), or bare <c>?</c> (MySQL, SQLite; see
/// <see cref="ISqlDialect.ParameterPlaceholder"/> for the source of each convention).
///
/// <para><b>Deliberately bypasses <see cref="DynamicParameters"/>.</b> Dapper's own
/// <c>DynamicParameters.AddParameters</c> unconditionally STRIPS a leading <c>@</c>/<c>:</c>/<c>?</c>
/// character before assigning <c>IDbDataParameter.ParameterName</c> — a convenience for its usual
/// "bind an anonymous object's bare property names" use case, and measured here to silently turn a bound
/// <c>?1</c> into a parameter literally named <c>1</c>, which <c>Microsoft.Data.Sqlite</c> then cannot
/// match against the <c>?1</c> token in the compiled text at all ("Must add values for the following
/// parameters: ?1, ?2"). Implementing <see cref="SqlMapper.IDynamicParameters"/> directly is Dapper's own
/// documented escape hatch for exactly this: it hands this type the raw <see cref="IDbCommand"/> and
/// steps out of the way entirely, so the name this binder chooses is the name that reaches the
/// driver.</para>
///
/// <para><b>MySQL's and SQLite's bare <c>?</c> is where the two genuinely diverge</b> — the trap this
/// package's own plan names explicitly: one dialect's fix does not generalize to the other.
/// <c>Microsoft.Data.Sqlite</c> REJECTS a bare <c>?</c> outright, so this binder rewrites SQLite's
/// compiled text into its own numbered form (<c>?1</c>, <c>?2</c>, …) — the exact technique
/// <c>Craticula.EntityFrameworkCore.Tests.SqliteFixtureDatabase.NumberPlaceholders</c> uses as a
/// TEST-ONLY workaround, promoted to production code here because this package's own execution path is
/// what a host actually runs against that provider. MySQL's <c>?</c>, by contrast, is a genuinely
/// POSITIONAL marker its ADO.NET providers bind by ORDER regardless of name — rewriting it into SQLite's
/// numbered syntax would hand the driver <c>?1</c> text it has no reason to understand, so MySQL's SQL
/// text is left untouched and its parameters are bound under arbitrary, order-preserving names
/// instead.</para>
///
/// <para>Safe to scan <see cref="SqlQuery.Sql"/> character-by-character for a bare <c>?</c> BECAUSE of
/// SEC-2: every dialect this compiler ships parameterizes every user-supplied value, so a stray <c>?</c>
/// can never be sitting inside a string literal the way it could in arbitrary, hand-written SQL — the
/// same guarantee <c>NumberPlaceholders</c>' own remarks lean on, re-verified here by counting the
/// rewritten markers against <see cref="SqlQuery.Parameters"/>'s own length.</para>
/// </summary>
internal static class DapperParameterBinder
{
    /// <summary>
    /// Prepares <paramref name="query"/> for execution: the SQL text to send (rewritten only for
    /// SQLite's bare <c>?</c>) and the matching <see cref="SqlMapper.IDynamicParameters"/>.
    /// </summary>
    public static (string Sql, SqlMapper.IDynamicParameters Parameters) Prepare(SqlQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return query.Dialect switch
        {
            "sqlite" => PrepareNumbered(query),
            "mysql" => PreparePositional(query),
            "postgres" => PrepareNamed(query, ordinal => $"${ordinal + 1}"),
            "mssql" => PrepareNamed(query, ordinal => $"@p{ordinal + 1}"),
            "oracle" => PrepareNamed(query, ordinal => $"p{ordinal + 1}"),
            _ => throw new DapperCompilationException(
                $"Unknown SQL dialect '{query.Dialect}' — no parameter-binding convention is defined for it.")
        };
    }

    private static (string Sql, SqlMapper.IDynamicParameters Parameters) PrepareNamed(
        SqlQuery query, Func<int, string> placeholder)
    {
        var names = new string[query.Parameters.Count];
        for (var index = 0; index < names.Length; index++)
        {
            names[index] = placeholder(index);
        }

        return (query.Sql, new NamedParameters(names, query.Parameters));
    }

    /// <summary>MySQL's bare <c>?</c>, bound by ORDER — any unique, stable name preserves the contract
    /// even though the driver never looks at it.</summary>
    private static (string Sql, SqlMapper.IDynamicParameters Parameters) PreparePositional(SqlQuery query)
    {
        var names = new string[query.Parameters.Count];
        for (var index = 0; index < names.Length; index++)
        {
            names[index] = $"p{index}";
        }

        return (query.Sql, new NamedParameters(names, query.Parameters));
    }

    private static (string Sql, SqlMapper.IDynamicParameters Parameters) PrepareNumbered(SqlQuery query)
    {
        var sql = NumberSqlitePlaceholders(query.Sql, query.Parameters.Count);

        var names = new string[query.Parameters.Count];
        for (var index = 0; index < names.Length; index++)
        {
            names[index] = $"?{index + 1}";
        }

        return (sql, new NamedParameters(names, query.Parameters));
    }

    private static string NumberSqlitePlaceholders(string sql, int expected)
    {
        var numbered = new StringBuilder(sql.Length + expected * 2);
        var ordinal = 0;

        foreach (var character in sql)
        {
            if (character == '?')
            {
                numbered.Append('?').Append(++ordinal);
            }
            else
            {
                numbered.Append(character);
            }
        }

        if (ordinal != expected)
        {
            throw new DapperCompilationException(
                $"Found {ordinal} '?' placeholders in the compiled SQLite statement but the query carries " +
                $"{expected} parameters. SqlQueryBuilder's own SEC-2 guarantee (every user value is a parameter, " +
                "never inlined) is what makes this rewrite safe; a mismatch means that guarantee no longer " +
                "holds for this statement.");
        }

        return numbered.ToString();
    }

    /// <summary>
    /// A flat, ordered (name, value) list bound to <paramref name="command"/> verbatim — no cleaning, no
    /// reflection, no property-name matching. This is the whole reason to implement
    /// <see cref="SqlMapper.IDynamicParameters"/> directly rather than populating a
    /// <see cref="DynamicParameters"/> bag: this type's <paramref name="names"/> ARE the exact
    /// <see cref="IDataParameter.ParameterName"/> values that reach the driver.
    /// </summary>
    private sealed class NamedParameters(IReadOnlyList<string> names, IReadOnlyList<object?> values)
        : SqlMapper.IDynamicParameters
    {
        public void AddParameters(IDbCommand command, SqlMapper.Identity identity)
        {
            for (var index = 0; index < names.Count; index++)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = names[index];
                parameter.Value = values[index] ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
        }
    }
}
