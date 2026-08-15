using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Sql.Tests;

/// <summary>
/// BM-3's distinct-values statement, hand-built for the shapes
/// <c>spec/fixtures/group-aggregation/016</c>-<c>019</c> do not reach — a case-SENSITIVE target
/// column, a non-text one, a physical/logical column-name mismatch, and the caller-precondition
/// failures.
///
/// <para>Everything the four fixtures DO pin — the <c>SELECT DISTINCT</c> shape, the bare
/// <c>ORDER BY</c>, the surviving filter, the prefix predicate and the resolved cap on all four
/// dialects — is asserted against the artifacts by <see cref="PostgresConformanceFixtureTests"/> and
/// <see cref="DialectConformanceFixtureTests"/>, never re-copied here.</para>
/// </summary>
public class SqlQueryBuilderSetValuesTests
{
    private static FieldConfig Field(FieldType type, string column, CaseSensitivity? caseSensitivity = null) => new()
    {
        Type = type,
        Physical = new PhysicalMapping { Sql = new SqlMapping { Column = column } },
        Text = caseSensitivity is null ? null : new TextOptions { CaseSensitivity = caseSensitivity.Value }
    };

    private static readonly GridSchema Schema = new()
    {
        Source = "sales_fact",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = Field(FieldType.String, "id"),
            ["region"] = Field(FieldType.String, "region"),
            ["employeeCode"] = Field(FieldType.String, "employee_code", CaseSensitivity.Sensitive),
            ["fiscalYear"] = Field(FieldType.Int64, "fiscal_year")
        }
    };

    private static SqlQuery Compile(
        string colId = "region", FilterNode? filter = null, int rowLimit = 1000, ISqlDialect? dialect = null) =>
        SqlQueryBuilder.BuildSetValues(
            new SqlSetValuesRequest { Schema = Schema, ColId = colId, Filter = filter, RowLimit = rowLimit },
            dialect ?? PostgresDialect.Instance);

    /// <summary>
    /// GRP-6: a case-INsensitive string column still renders bare in both positions — the same
    /// exactness GRP-2 gives a <c>GROUP BY</c> key, for the same reason (these values come straight
    /// back as <c>in</c> values or a <c>groupKey</c>, which a grouped query matches exactly). Pinned
    /// by <c>016</c>'s artifacts too; repeated here as the control for the case-sensitive twin below.
    /// </summary>
    [Fact]
    public void A_Case_Insensitive_Target_Folds_No_Case_In_Either_Position()
    {
        Assert.Equal(
            "SELECT DISTINCT \"region\" FROM \"sales_fact\" ORDER BY \"region\" ASC NULLS LAST LIMIT $1",
            Compile().Sql);
    }

    /// <summary>
    /// And so does a case-SENSITIVE one, which is the assertion that shows GRP-6 resolves
    /// <c>NotApplicable</c> rather than <c>Sensitive</c>: on Postgres the two are spelled identically
    /// (nothing), so this is asserted on SQL Server, where <c>Sensitive</c> would emit
    /// <c>COLLATE Latin1_General_CS_AS</c> and <c>NotApplicable</c> emits nothing at all.
    /// </summary>
    [Fact]
    public void A_Case_Sensitive_Target_Is_Not_Sensitive_Either_It_Is_NotApplicable()
    {
        var sql = Compile(colId: "employeeCode", dialect: MssqlDialect.Instance).Sql;

        Assert.DoesNotContain("COLLATE", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// A non-text column is a perfectly ordinary set-filter target (a year, a status code), and
    /// nothing about the statement changes — SEM-1 never applied to it in the first place.
    /// </summary>
    [Fact]
    public void A_Non_Text_Target_Compiles_And_Resolves_Its_Physical_Column()
    {
        Assert.Equal(
            "SELECT DISTINCT \"fiscal_year\" FROM \"sales_fact\" ORDER BY \"fiscal_year\" ASC NULLS LAST LIMIT $1",
            Compile(colId: "fiscalYear").Sql);
    }

    /// <summary>
    /// No SEM-6 tiebreaker, and it is not an oversight: the distinct column's values are unique
    /// across the result rows by construction, and the schema's <c>stableKey</c> is not in the
    /// projection at all — ordering by it would be the same invalid SQL GRP-4 avoids at a grouped
    /// level. The schema here declares one (<c>id</c>) precisely so its absence is a decision.
    /// </summary>
    [Fact]
    public void The_Ordering_Is_The_Distinct_Column_Alone_With_No_StableKey_Appended()
    {
        Assert.DoesNotContain("\"id\"", Compile().Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Surviving_Filter_Renders_As_An_Ordinary_Where_Clause()
    {
        var filter = new ComparisonNode
        {
            Field = "fiscalYear",
            Op = ComparisonOperator.Gt,
            Value = new Int64Value { Value = 2020 }
        };

        var query = Compile(filter: filter);

        Assert.Equal(
            "SELECT DISTINCT \"region\" FROM \"sales_fact\" WHERE \"fiscal_year\" > $1 " +
            "ORDER BY \"region\" ASC NULLS LAST LIMIT $2",
            query.Sql);
        Assert.Equal([2020L, 1000], query.Parameters);
    }

    /// <summary>
    /// The cap binds LAST, after every filter value — the row-fetch path's own convention, and what
    /// makes <c>params</c> first-appearance ordered end to end on a positional dialect.
    /// </summary>
    [Fact]
    public void The_Cap_Is_The_Final_Bound_Parameter()
    {
        var filter = new ComparisonNode
        {
            Field = "region",
            Op = ComparisonOperator.Neq,
            Value = new StringValue { Value = "EMEA" }
        };

        Assert.Equal(["EMEA", 250], Compile(filter: filter, rowLimit: 250).Parameters);
    }

    [Fact]
    public void An_Undeclared_Target_Column_Is_A_Caller_Precondition_Failure()
    {
        Assert.Throws<SqlCompilationException>(() => Compile(colId: "ssn"));
    }

    /// <summary>
    /// Defense in depth against a caller that skipped
    /// <see cref="ResourceLimitsValidator.ResolveSetValuesLimit"/>. Not merely tidiness: on SQL Server
    /// <c>FETCH NEXT 0 ROWS ONLY</c> is msg 10744, an outright error rather than an empty result.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_NonPositive_Row_Limit_Is_Refused_Rather_Than_Compiled(int rowLimit)
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(rowLimit: rowLimit));

        Assert.Contains("ResolveSetValuesLimit", ex.Message, StringComparison.Ordinal);
    }
}
