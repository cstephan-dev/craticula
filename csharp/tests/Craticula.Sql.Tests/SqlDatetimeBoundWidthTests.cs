using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Sql.Tests;

/// <summary>
/// SEM-4 (SQLite) / #2243: SQLite compares a datetime as TEXT, and <c>.</c> sorts before <c>Z</c>, so
/// the bound must carry the host's fixed three-digit fraction or a stored <c>...:00.001Z</c> sorts
/// before a bound spelled <c>...:00Z</c> and <c>gt</c> drops it. Every other dialect keeps the
/// whole-second form. Driven through the public <see cref="SqlQueryBuilder.Build"/> path.
/// </summary>
public class SqlDatetimeBoundWidthTests
{
    private static readonly GridSchema Schema = new()
    {
        Source = "items",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = new FieldConfig
            {
                Type = FieldType.Int64,
                Physical = new PhysicalMapping { Sql = new SqlMapping { Column = "id" } }
            },
            ["createdAt"] = new FieldConfig
            {
                Type = FieldType.Datetime,
                Physical = new PhysicalMapping { Sql = new SqlMapping { Column = "created_at" } }
            }
        }
    };

    private static object? Bound(ISqlDialect dialect, string instant)
    {
        var query = SqlQueryBuilder.Build(
            new SqlQueryRequest
            {
                Schema = Schema,
                Filter = new ComparisonNode
                {
                    Field = "createdAt",
                    Op = ComparisonOperator.Gt,
                    Value = new DatetimeValue { Value = instant, Zone = "UTC" }
                },
                Page = new RangePage { StartRow = 0, EndRow = 100 }
            },
            dialect);

        return query.Parameters[0];
    }

    [Theory]
    // A whole second is padded to the stored width.
    [InlineData("2026-01-01T00:00:00Z", "2026-01-01T00:00:00.000Z")]
    // A whole-millisecond bound is already the stored width.
    [InlineData("2026-01-01T00:00:00.001Z", "2026-01-01T00:00:00.001Z")]
    // An offset spelling is the same instant, padded the same way.
    [InlineData("2026-01-01T02:00:00+02:00", "2026-01-01T00:00:00.000Z")]
    // A short fraction is padded to three digits.
    [InlineData("2026-01-01T00:00:00.5Z", "2026-01-01T00:00:00.500Z")]
    // A sub-millisecond bound keeps its full precision on SQLite too: never truncated.
    [InlineData("2026-01-01T00:00:00.0005Z", "2026-01-01T00:00:00.0005Z")]
    [InlineData("2026-01-01T00:00:00.1234567Z", "2026-01-01T00:00:00.1234567Z")]
    public void SQLite_Binds_A_Fixed_Three_Digit_Fraction_Unless_Finer(string value, string expected) =>
        Assert.Equal(expected, Bound(SqliteDialect.Instance, value));

    [Theory]
    [InlineData("2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z")]
    [InlineData("2026-01-01T00:00:00.001Z", "2026-01-01T00:00:00.001Z")]
    [InlineData("2026-01-01T00:00:00.5Z", "2026-01-01T00:00:00.5Z")]
    [InlineData("2026-01-01T00:00:00.0005Z", "2026-01-01T00:00:00.0005Z")]
    public void The_Other_Dialects_Keep_The_Whole_Second_Form(string value, string expected)
    {
        foreach (var dialect in new ISqlDialect[]
                 {
                     PostgresDialect.Instance, MssqlDialect.Instance, MySqlDialect.Instance, OracleDialect.Instance
                 })
        {
            Assert.False(dialect.DatetimeBoundsAreFixedWidth, dialect.Name);
            Assert.Equal(expected, Bound(dialect, value));
        }
    }
}
