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
    // A sub-millisecond bound under gt floors to the millisecond (see the rounding tests below).
    [InlineData("2026-01-01T00:00:00.0005Z", "2026-01-01T00:00:00.000Z")]
    [InlineData("2026-01-01T00:00:00.1234567Z", "2026-01-01T00:00:00.123Z")]
    public void SQLite_Binds_A_Fixed_Three_Digit_Fraction(string value, string expected) =>
        Assert.Equal(expected, Bound(SqliteDialect.Instance, value));

    private static IReadOnlyList<object?> Params(ISqlDialect dialect, ComparisonNode node) =>
        SqlQueryBuilder.Build(
            new SqlQueryRequest { Schema = Schema, Filter = node, Page = new RangePage { StartRow = 0, EndRow = 100 } },
            dialect).Parameters;

    private static ComparisonNode Compare(ComparisonOperator op, string instant) => new()
    {
        Field = "createdAt",
        Op = op,
        Value = new DatetimeValue { Value = instant, Zone = "UTC" }
    };

    // SEM-4 (SQLite): stored values are millisecond-aligned, so a sub-millisecond bound rounds by operator.
    // 00.0005Z lies between the stored .000 and .001: gt/lte floor to .000, gte/lt ceil to .001.
    [Theory]
    [InlineData(ComparisonOperator.Gt, "2026-01-01T00:00:00.0005Z", "2026-01-01T00:00:00.000Z")]
    [InlineData(ComparisonOperator.Lte, "2026-01-01T00:00:00.0005Z", "2026-01-01T00:00:00.000Z")]
    [InlineData(ComparisonOperator.Gte, "2026-01-01T00:00:00.0005Z", "2026-01-01T00:00:00.001Z")]
    [InlineData(ComparisonOperator.Lt, "2026-01-01T00:00:00.0005Z", "2026-01-01T00:00:00.001Z")]
    // Ceiling carries into the next second and day.
    [InlineData(ComparisonOperator.Gte, "2025-12-31T23:59:59.9995Z", "2026-01-01T00:00:00.000Z")]
    [InlineData(ComparisonOperator.Lt, "2025-12-31T23:59:59.9995Z", "2026-01-01T00:00:00.000Z")]
    [InlineData(ComparisonOperator.Gt, "2025-12-31T23:59:59.9995Z", "2025-12-31T23:59:59.999Z")]
    // An offset form is the same instant: 02:00:00.0005+02:00 is 00:00:00.0005Z.
    [InlineData(ComparisonOperator.Gt, "2026-01-01T02:00:00.0005+02:00", "2026-01-01T00:00:00.000Z")]
    [InlineData(ComparisonOperator.Lte, "2026-01-01T02:00:00.0005+02:00", "2026-01-01T00:00:00.000Z")]
    [InlineData(ComparisonOperator.Gte, "2026-01-01T02:00:00.0005+02:00", "2026-01-01T00:00:00.001Z")]
    [InlineData(ComparisonOperator.Lt, "2026-01-01T02:00:00.0005+02:00", "2026-01-01T00:00:00.001Z")]
    // An aligned bound is unchanged under every operator.
    [InlineData(ComparisonOperator.Gt, "2026-01-01T00:00:00.001Z", "2026-01-01T00:00:00.001Z")]
    [InlineData(ComparisonOperator.Gte, "2026-01-01T00:00:00.001Z", "2026-01-01T00:00:00.001Z")]
    [InlineData(ComparisonOperator.Lt, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00.000Z")]
    [InlineData(ComparisonOperator.Lte, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00.000Z")]
    // eq and neq keep the full value: a finer value never equals a stored one.
    [InlineData(ComparisonOperator.Eq, "2026-01-01T00:00:00.0005Z", "2026-01-01T00:00:00.0005Z")]
    [InlineData(ComparisonOperator.Neq, "2026-01-01T00:00:00.0005Z", "2026-01-01T00:00:00.0005Z")]
    [InlineData(ComparisonOperator.Eq, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00.000Z")]
    public void SQLite_Rounds_A_Sub_Millisecond_Bound_By_Operator(ComparisonOperator op, string value, string expected) =>
        Assert.Equal(expected, Params(SqliteDialect.Instance, Compare(op, value))[0]);

    [Fact]
    public void SQLite_Keeps_The_Full_Value_In_Set_Membership()
    {
        foreach (var op in new[] { ComparisonOperator.In, ComparisonOperator.NotIn })
        {
            var node = new ComparisonNode
            {
                Field = "createdAt",
                Op = op,
                Values = [
                    new DatetimeValue { Value = "2026-01-01T00:00:00.0005Z", Zone = "UTC" },
                    new DatetimeValue { Value = "2026-01-01T00:00:00Z", Zone = "UTC" }]
            };

            var parameters = Params(SqliteDialect.Instance, node);
            Assert.Equal("2026-01-01T00:00:00.0005Z", parameters[0]);
            Assert.Equal("2026-01-01T00:00:00.000Z", parameters[1]);
        }
    }

    // inRange: the from-bound is gt (floor) when exclusive and gte (ceiling) when inclusive; the to-bound is lte (floor)
    // when inclusive and lt (ceiling) when exclusive.
    [Theory]
    [InlineData(false, false, "2026-01-01T00:00:00.000Z", "2026-01-01T00:00:01.001Z")]
    [InlineData(false, true, "2026-01-01T00:00:00.000Z", "2026-01-01T00:00:01.000Z")]
    [InlineData(true, false, "2026-01-01T00:00:00.001Z", "2026-01-01T00:00:01.001Z")]
    [InlineData(true, true, "2026-01-01T00:00:00.001Z", "2026-01-01T00:00:01.000Z")]
    public void SQLite_Rounds_Each_InRange_Bound_By_Its_Own_Inclusivity(bool fromInclusive, bool toInclusive, string from, string to)
    {
        var node = new ComparisonNode
        {
            Field = "createdAt",
            Op = ComparisonOperator.InRange,
            From = new DatetimeValue { Value = "2026-01-01T00:00:00.0005Z", Zone = "UTC" },
            To = new DatetimeValue { Value = "2026-01-01T00:00:01.0005Z", Zone = "UTC" },
            FromInclusive = fromInclusive,
            ToInclusive = toInclusive
        };

        var parameters = Params(SqliteDialect.Instance, node);
        Assert.Equal(from, parameters[0]);
        Assert.Equal(to, parameters[1]);

        // The other dialects bind both bounds as written, under every inclusivity.
        Assert.Equal(["2026-01-01T00:00:00.0005Z", "2026-01-01T00:00:01.0005Z"], Params(PostgresDialect.Instance, node).Take(2));
    }

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
