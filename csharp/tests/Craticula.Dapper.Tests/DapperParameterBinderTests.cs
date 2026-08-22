namespace Craticula.Dapper.Tests;

/// <summary>
/// <see cref="DapperParameterBinder.ToTypedArray"/> — the fix for a critical review finding on this
/// story (#1646): a Postgres set-membership parameter is <c>SqlQuery.Parameters</c>'s one nested-list
/// shape (<c>PostgresDialect.RenderSetMembership</c>'s single <c>= ANY($1)</c> array parameter), and
/// binding that <see cref="IReadOnlyList{T}"/> straight to <c>IDataParameter.Value</c> hands Npgsql a
/// boxed <c>object</c> list with no element type to infer a Postgres array type from. No live Postgres
/// connection exists in this repo's test harness (every other case here runs against SQLite, which
/// never reaches this branch — see <c>Craticula.Dapper.csproj</c>'s <c>InternalsVisibleTo</c> comment),
/// so this is a pure unit test of the rebuild logic rather than an execution-equality case.
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
}
