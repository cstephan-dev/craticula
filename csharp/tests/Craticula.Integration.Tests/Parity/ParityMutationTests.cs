using Craticula.Integration.Tests.Containers.Sql;
using Craticula.Sql;

namespace Craticula.Integration.Tests.Parity;

/// <summary>
/// Offline guards for the test-only SQL mutations the parity property is shown to catch (#2270): each rewrite removes
/// what it names and nothing else, so a failing run under <c>CRATICULA_XEQ_MUTATION</c> is the property's doing, not a
/// broken query. The mutation is applied by name here, never through the environment.
/// </summary>
public sealed class ParityMutationTests
{
    private const string Request = """{"startRow":0,"endRow":10,"rowGroupCols":[],"valueCols":[],"pivotCols":[],"pivotMode":false,"groupKeys":[],"filterModel":null,"sortModel":[{"colId":"qty","sort":"asc"},{"colId":"score","sort":"desc"}]}""";

    private static string Sql(ISqlDialect dialect) => SqlFixtureCompiler.Compile(Request, ParitySchema.Create(), dialect).Sql;

    [Fact]
    public void Without_a_name_the_sql_is_untouched()
    {
        var sql = Sql(PostgresDialect.Instance);
        Assert.Equal(sql, SqlMutation.Apply(sql, null));
        Assert.Equal(sql, SqlMutation.Apply(sql, "no-such-mutation"));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlite")]
    public void Drop_nulls_removes_the_null_placement_and_only_that(string engine)
    {
        var sql = Sql(engine == "postgres" ? PostgresDialect.Instance : SqliteDialect.Instance);
        Assert.Contains("NULLS", sql);

        var mutated = SqlMutation.Apply(sql, "drop-nulls");
        Assert.DoesNotContain("NULLS", mutated);
        Assert.Contains("\"qty\" ASC, \"score\" DESC, \"id\" ASC", mutated);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlite")]
    [InlineData("mysql")]
    [InlineData("mssql")]
    public void Drop_tiebreaker_removes_the_trailing_stable_key_only(string engine)
    {
        var dialect = engine switch
        {
            "postgres" => (ISqlDialect)PostgresDialect.Instance,
            "sqlite" => SqliteDialect.Instance,
            "mysql" => MySqlDialect.Instance,
            _ => MssqlDialect.Instance
        };

        var sql = Sql(dialect);
        var mutated = SqlMutation.Apply(sql, "drop-tiebreaker");

        Assert.NotEqual(sql, mutated);
        string[] idForms = ["\"id\"", "`id`", "[id]"];
        Assert.Contains(idForms, form => sql.Split("ORDER BY")[1].Contains(form));
        Assert.DoesNotContain(idForms, form => mutated.Split("ORDER BY")[1].Contains(form));
        Assert.Contains("qty", mutated);
        Assert.Contains("score", mutated);
    }

    [Fact]
    public void Drop_tiebreaker_leaves_a_sort_that_is_only_the_stable_key()
    {
        var onlyId = Request.Replace("""[{"colId":"qty","sort":"asc"},{"colId":"score","sort":"desc"}]""", "[]");
        var sql = SqlFixtureCompiler.Compile(onlyId, ParitySchema.Create(), PostgresDialect.Instance).Sql;

        Assert.Equal(sql, SqlMutation.Apply(sql, "drop-tiebreaker"));
    }
}
