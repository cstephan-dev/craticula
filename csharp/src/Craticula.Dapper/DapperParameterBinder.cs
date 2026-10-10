using System.Data;
using System.Text;
using Craticula.Sql;
using Dapper;

namespace Craticula.Dapper;

/// <summary>
/// Binds a compiled <see cref="SqlQuery"/>'s flat, ordered <see cref="SqlQuery.Parameters"/> to whatever
/// placeholder syntax the dialect that produced it actually rendered into <see cref="SqlQuery.Sql"/> —
/// <c>$1</c> (Postgres, bound as UNNAMED parameters in order: Npgsql treats a parameter named <c>$1</c> as a named one,
/// matches none of the statement's positional markers and fails with "bind message supplies 0 parameters" — measured
/// against a real PostgreSQL 16 in #2315), <c>@p1</c> (SQL Server), <c>:p1</c> (Oracle, bound without its leading colon —
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
            "postgres" => PrepareNamed(query, _ => string.Empty),
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
    /// Rebuilds a set-membership parameter's boxed elements into a strongly-typed <see cref="Array"/>,
    /// needed because a straight assignment fails for Postgres's set-membership array specifically.
    /// <c>SqlQuery.Parameters</c>'s own contract names the shape directly — "a nested
    /// <c>IReadOnlyList&lt;object?&gt;</c> for a set membership's single array parameter" — produced by
    /// <c>PostgresDialect.RenderSetMembership</c>'s <c>= ANY($1)</c>/<c>&lt;&gt; ALL($1)</c>, which for a decimal, date or
    /// datetime field casts the array to the column's type (<c>= ANY($1::numeric[])</c>, <c>$1::date[]</c>,
    /// <c>$1::timestamptz[]</c>, SQL-4), so the <c>string[]</c> (<c>text[]</c>) this method builds from those fields'
    /// string-encoded values is converted server-side; without the cast PostgreSQL has no <c>numeric = text</c> operator (the other
    /// four dialects bind N scalars instead, so they never reach this method). Assigning that
    /// <see cref="IReadOnlyList{T}"/> straight to <see cref="IDataParameter.Value"/> hands Npgsql a
    /// boxed <c>object</c> list with no element type to infer a Postgres array type from — every element
    /// is individually boxed by <c>SqlQueryBuilder.EncodeValue</c> (a <see cref="long"/>, a
    /// <see cref="string"/>, …), so the LIST's own static element type is <c>object</c> regardless of
    /// what the elements actually are.
    ///
    /// <para><paramref name="values"/>'s elements all share one <see cref="Craticula.Core.Schema.FieldType"/>'s
    /// encoded CLR shape — found from the first non-null element, since AR-6's set filter is homogeneous
    /// by construction (every value coerced against the same declared field) — and this method rebuilds a
    /// strongly-typed CLR array (<see cref="long"/><c>[]</c>, <see cref="string"/><c>[]</c>, …) from that
    /// shared RUNTIME type instead, which Npgsql infers a <c>NpgsqlDbType.Array</c> from the same way it
    /// would from a hand-written <c>long[]</c> — without this package adding an Npgsql package reference
    /// merely to name a type hint explicitly, which would weld it to one provider the same way a
    /// provider package would (see <c>Craticula.EntityFrameworkCore.csproj</c>'s identical reasoning for
    /// why it carries no provider package either).</para>
    ///
    /// <para>A <see cref="Craticula.Core.Ir.ComparisonNode.Values"/> entry MAY itself be AR-7's
    /// <c>nullValue</c> (an ordinary <c>in</c>/<c>notIn</c> testing "or is this NULL" alongside real
    /// values), so a value-type element (<see cref="long"/>, <see cref="double"/>, <see cref="bool"/>)
    /// widens to its <see cref="Nullable{T}"/> array form when any element is null — a plain
    /// <c>long[]</c> cannot hold one. A reference type (<see cref="string"/>) already can, unchanged.
    /// Internal rather than private specifically so this reflection-based rebuild — the one piece of
    /// this binder that needs no live Postgres connection to unit-test (the live run is
    /// <c>DapperPostgresExecutionTests</c> in the execution-equality tier, #1655) — can be unit-tested directly.</para>
    /// </summary>
    internal static object ToTypedArray(IReadOnlyList<object?> values)
    {
        var elementType = values.FirstOrDefault(value => value is not null)?.GetType()
            ?? throw new DapperCompilationException(
                "A set-membership parameter has no non-null value to infer its array element type from " +
                "(every element is null). SEC-6's own resource limits require at least one value in a set " +
                "filter, so this indicates a caller bypassed that validation.");

        var arrayElementType = elementType.IsValueType && values.Any(value => value is null)
            ? typeof(Nullable<>).MakeGenericType(elementType)
            : elementType;

        var typed = Array.CreateInstance(arrayElementType, values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            typed.SetValue(values[index], index);
        }

        return typed;
    }

    /// <summary>
    /// A flat, ordered (name, value) list bound to the command verbatim — no cleaning, no reflection
    /// over parameter NAMES, no property-name matching. This is the whole reason to implement
    /// <see cref="SqlMapper.IDynamicParameters"/> directly rather than populating a
    /// <see cref="DynamicParameters"/> bag: this type's <paramref name="names"/> ARE the exact
    /// <see cref="IDataParameter.ParameterName"/> values that reach the driver. The one value shape that
    /// needs more than a straight assignment — Postgres's set-membership array — goes through
    /// <see cref="ToTypedArray"/>; see that method's own remarks.
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
                parameter.Value = values[index] switch
                {
                    null => DBNull.Value,
                    IReadOnlyList<object?> set => ToTypedArray(set),
                    var scalar => scalar
                };
                command.Parameters.Add(parameter);
            }
        }
    }
}
