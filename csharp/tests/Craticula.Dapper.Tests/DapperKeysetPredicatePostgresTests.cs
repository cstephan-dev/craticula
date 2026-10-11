using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.Sql;

namespace Craticula.Dapper.Tests;

/// <summary>
/// SQL-8 (#2320) on the export path, offline: the keyset boundary <see cref="DapperKeysetPredicate"/> builds
/// carries decimal, date and datetime values as AR-7 strings, so compiled for Postgres its placeholders must be
/// cast to the column type, or a real server refuses <c>numeric &gt; text</c> on the second chunk of an export.
/// </summary>
public class DapperKeysetPredicatePostgresTests
{
    private static GridSchema Schema() => new()
    {
        Source = "items",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = Field(FieldType.Int64, "id"),
            ["cost"] = Field(FieldType.Decimal, "unit_cost"),
            ["day"] = Field(FieldType.Date, "birth_date"),
            ["at"] = Field(FieldType.Datetime, "created_at")
        }
    };

    private static FieldConfig Field(FieldType type, string column) => new()
    {
        Type = type,
        Physical = new PhysicalMapping { Sql = new SqlMapping { Column = column } }
    };

    private static string Compile(string key, object lastValue, object lastId)
    {
        SortItem[] keys =
        [
            new() { Field = key, Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last },
            new() { Field = "id", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }
        ];

        var boundary = DapperKeysetPredicate.Build(keys, [lastValue, lastId]);

        return SqlQueryBuilder.Build(
            new SqlQueryRequest
            {
                Schema = Schema(),
                Filter = boundary,
                Sort = keys,
                Page = new RangePage { StartRow = 0, EndRow = 100 }
            },
            PostgresDialect.Instance).Sql;
    }

    [Fact]
    public void A_Decimal_Sort_Key_Boundary_Casts_Its_Placeholders_To_Numeric()
    {
        var sql = Compile("cost", 19.99m, 7L);

        Assert.Contains("\"unit_cost\" > $1::numeric", sql, StringComparison.Ordinal);
        Assert.Contains("\"unit_cost\" = $", sql, StringComparison.Ordinal);
        Assert.Matches("\"unit_cost\" = \\$\\d+::numeric", sql);
        // The int64 stable key stays uncast.
        Assert.Matches("\"id\" > \\$\\d+[^:]", sql);
    }

    [Fact]
    public void A_Date_Sort_Key_Boundary_Casts_Its_Placeholders_To_Date()
    {
        var sql = Compile("day", new DateOnly(2024, 3, 1), 7L);

        Assert.Contains("\"birth_date\" > $1::date", sql, StringComparison.Ordinal);
        Assert.Matches("\"birth_date\" = \\$\\d+::date", sql);
    }

    [Fact]
    public void A_Datetime_Sort_Key_Boundary_Casts_Its_Placeholders_To_Timestamptz()
    {
        var sql = Compile("at", new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc), 7L);

        Assert.Contains("\"created_at\" > $1::timestamptz", sql, StringComparison.Ordinal);
        Assert.Matches("\"created_at\" = \\$\\d+::timestamptz", sql);
    }
}
