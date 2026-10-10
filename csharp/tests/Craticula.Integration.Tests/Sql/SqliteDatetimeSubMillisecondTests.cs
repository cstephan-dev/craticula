using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.Sql;
using Microsoft.Data.Sqlite;

namespace Craticula.Integration.Tests.Containers.Sql;

/// <summary>
/// SEM-4 (SQLite) / #2243, sub-millisecond bounds, executed on a real in-memory SQLite (no container). Stored values are
/// exactly <c>.fff</c>; the compiler rounds a finer bound by operator (gt/lte floor, gte/lt ceil). The expected rows are
/// derived by hand from the chronological order, never from the engine:
/// rows <c>...59.999Z</c> (1), <c>....000Z</c> (2), <c>....001Z</c> (3), <c>....002Z</c> (4); bound <c>00.0005Z</c> lies
/// between rows 2 and 3.
/// </summary>
public class SqliteDatetimeSubMillisecondTests
{
    private static readonly GridSchema Schema = new()
    {
        Source = "items",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = new FieldConfig { Type = FieldType.Int64, Physical = new PhysicalMapping { Sql = new SqlMapping { Column = "id" } } },
            ["createdAt"] = new FieldConfig { Type = FieldType.Datetime, Physical = new PhysicalMapping { Sql = new SqlMapping { Column = "created_at" } } }
        }
    };

    private static List<long> Run(ComparisonNode node)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE \"items\" (\"id\" INTEGER, \"created_at\" TEXT)";
            create.ExecuteNonQuery();
        }

        var seeds = new[]
        {
            (1L, "2025-12-31T23:59:59.999Z"), (2L, "2026-01-01T00:00:00.000Z"),
            (3L, "2026-01-01T00:00:00.001Z"), (4L, "2026-01-01T00:00:00.002Z")
        };
        foreach (var (id, at) in seeds)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO \"items\" VALUES (?1, ?2)";
            insert.Parameters.Add(new SqliteParameter("?1", id));
            insert.Parameters.Add(new SqliteParameter("?2", at));
            insert.ExecuteNonQuery();
        }

        var query = SqlQueryBuilder.Build(
            new SqlQueryRequest
            {
                Schema = Schema,
                Filter = node,
                Sort = [new SortItem { Field = "id", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }],
                Page = new RangePage { StartRow = 0, EndRow = 100 }
            },
            SqliteDialect.Instance);

        using var command = connection.CreateCommand();
        var sql = new System.Text.StringBuilder();
        var ordinal = 0;
        foreach (var character in query.Sql)
        {
            sql.Append(character);
            if (character == '?')
            {
                sql.Append(++ordinal);
            }
        }

        command.CommandText = sql.ToString();
        for (var index = 0; index < query.Parameters.Count; index++)
        {
            command.Parameters.Add(new SqliteParameter($"?{index + 1}", query.Parameters[index] ?? DBNull.Value));
        }

        var ids = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private static ComparisonNode Compare(ComparisonOperator op, string instant) => new()
    {
        Field = "createdAt",
        Op = op,
        Value = new DatetimeValue { Value = instant, Zone = "UTC" }
    };

    [Theory]
    // x > 00.0005: rows 3 and 4 (the engine's text order would also return 2)
    [InlineData(ComparisonOperator.Gt, "2026-01-01T00:00:00.0005Z", new[] { 3L, 4L })]
    [InlineData(ComparisonOperator.Gte, "2026-01-01T00:00:00.0005Z", new[] { 3L, 4L })]
    [InlineData(ComparisonOperator.Lt, "2026-01-01T00:00:00.0005Z", new[] { 1L, 2L })]
    [InlineData(ComparisonOperator.Lte, "2026-01-01T00:00:00.0005Z", new[] { 1L, 2L })]
    // A full-precision value never equals a stored one.
    [InlineData(ComparisonOperator.Eq, "2026-01-01T00:00:00.0005Z", new long[0])]
    // Same instant written with an offset.
    [InlineData(ComparisonOperator.Gt, "2026-01-01T02:00:00.0005+02:00", new[] { 3L, 4L })]
    // Between rows 1 and 2 across a second boundary: 59.9995Z.
    [InlineData(ComparisonOperator.Gt, "2025-12-31T23:59:59.9995Z", new[] { 2L, 3L, 4L })]
    [InlineData(ComparisonOperator.Lt, "2025-12-31T23:59:59.9995Z", new[] { 1L })]
    // An aligned bound is unchanged.
    [InlineData(ComparisonOperator.Gt, "2026-01-01T00:00:00.001Z", new[] { 4L })]
    [InlineData(ComparisonOperator.Gte, "2026-01-01T00:00:00.001Z", new[] { 3L, 4L })]
    [InlineData(ComparisonOperator.Eq, "2026-01-01T00:00:00Z", new[] { 2L })]
    public void A_Sub_Millisecond_Bound_Returns_The_Chronologically_Correct_Rows(ComparisonOperator op, string bound, long[] expected) =>
        Assert.Equal(expected, Run(Compare(op, bound)));

    [Theory]
    // from 00.0005 exclusive/inclusive both mean s >= .001; to 00.0015 inclusive/exclusive both mean s <= .001
    [InlineData(false, false, new[] { 3L })]
    [InlineData(true, true, new[] { 3L })]
    [InlineData(false, true, new[] { 3L })]
    [InlineData(true, false, new[] { 3L })]
    public void A_Sub_Millisecond_InRange_Returns_The_Chronologically_Correct_Rows(bool fromInclusive, bool toInclusive, long[] expected) =>
        Assert.Equal(expected, Run(new ComparisonNode
        {
            Field = "createdAt",
            Op = ComparisonOperator.InRange,
            From = new DatetimeValue { Value = "2026-01-01T00:00:00.0005Z", Zone = "UTC" },
            To = new DatetimeValue { Value = "2026-01-01T00:00:00.0015Z", Zone = "UTC" },
            FromInclusive = fromInclusive,
            ToInclusive = toInclusive
        }));
}
