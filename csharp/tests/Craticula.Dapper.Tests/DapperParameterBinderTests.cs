using Craticula.Sql;
namespace Craticula.Dapper.Tests;

/// <summary>
/// <see cref="DapperParameterBinder.ToTypedArray"/> — the fix for a critical review finding on this
/// story (#1646): a Postgres set-membership parameter is <c>SqlQuery.Parameters</c>'s one nested-list
/// shape (<c>PostgresDialect.RenderSetMembership</c>'s single <c>= ANY($1)</c> array parameter), and
/// binding that <see cref="IReadOnlyList{T}"/> straight to <c>IDataParameter.Value</c> hands Npgsql a
/// boxed <c>object</c> list with no element type to infer a Postgres array type from. This is an offline
/// unit test of the rebuild logic (every other case here runs against SQLite, which never reaches this
/// branch — see <c>Craticula.Dapper.csproj</c>'s <c>InternalsVisibleTo</c> comment); the live Postgres run
/// of the same binder is <c>DapperPostgresExecutionTests</c> in the execution-equality tier (#1655).
/// </summary>
public class DapperParameterBinderTests
{
    [Fact]
    public void Rebuilds_NonNullable_Value_Type_Elements_As_Their_Own_Array_Type()
    {
        var result = DapperParameterBinder.ToTypedArray([1L, 2L, 3L]);

        var typed = Assert.IsType<long[]>(result);
        Assert.Equal([1L, 2L, 3L], typed);
    }

    [Fact]
    public void Rebuilds_String_Elements_As_A_String_Array()
    {
        var result = DapperParameterBinder.ToTypedArray(["EMEA", "APAC"]);

        var typed = Assert.IsType<string[]>(result);
        Assert.Equal(["EMEA", "APAC"], typed);
    }

    /// <summary>The mixed shape: AR-7's <c>nullValue</c> alongside real values in the same
    /// <c>in</c>/<c>notIn</c> set — a plain <c>long[]</c> cannot hold the null, so the element type must
    /// widen to <c>long?</c>.</summary>
    [Fact]
    public void Widens_A_Nullable_Value_Type_Array_When_Any_Element_Is_Null()
    {
        var result = DapperParameterBinder.ToTypedArray([1L, null, 3L]);

        var typed = Assert.IsType<long?[]>(result);
        Assert.Equal([1L, null, 3L], typed);
    }

    /// <summary>A reference type already accepts <see langword="null"/> — no widening needed, unlike
    /// the value-type case above.</summary>
    [Fact]
    public void Does_Not_Widen_A_Reference_Type_Array_When_An_Element_Is_Null()
    {
        var result = DapperParameterBinder.ToTypedArray(["EMEA", null, "APAC"]);

        var typed = Assert.IsType<string?[]>(result);
        Assert.Equal(new string?[] { "EMEA", null, "APAC" }, typed);
    }

    [Fact]
    public void Throws_WhenEveryElementIsNull_BecauseNoElementTypeCanBeInferred()
    {
        Assert.Throws<DapperCompilationException>(
            () => DapperParameterBinder.ToTypedArray([null, null]));
    }

    /// <summary>
    /// #2315: Npgsql treats a parameter NAMED <c>$1</c> as a named parameter, matches none of the statement's
    /// positional <c>$n</c> markers and fails with "bind message supplies 0 parameters"; the binder therefore binds
    /// Postgres parameters unnamed, in order. Found only by running Dapper against a live PostgreSQL (every other case
    /// here is SQLite), so this pins the shape offline.
    /// </summary>
    [Fact]
    public void Postgres_Parameters_Are_Bound_Unnamed_And_In_Order()
    {
        var query = new SqlQuery
        {
            Dialect = "postgres",
            Sql = "SELECT 1 WHERE a = $1 AND b = $2",
            Parameters = ["x", 7L]
        };

        var (sql, parameters) = DapperParameterBinder.Prepare(query);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        using var command = connection.CreateCommand();
        parameters.AddParameters(command, null!);

        Assert.Equal(query.Sql, sql);
        Assert.Equal(2, command.Parameters.Count);
        Assert.All(command.Parameters.Cast<System.Data.Common.DbParameter>(), p => Assert.Equal(string.Empty, p.ParameterName));
        Assert.Equal("x", command.Parameters[0].Value);
        Assert.Equal(7L, command.Parameters[1].Value);
    }
}
