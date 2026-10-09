using System.Text.Json;
using Craticula.Core.Schema;
using Craticula.Integration.Tests.Oracle;

namespace Craticula.Integration.Tests.Oracle;

/// <summary>XEQ-3's comparison rule, one case per clause, so a harness bug cannot hide behind a lenient comparer.</summary>
public class RowComparerTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static string? Problem(FieldType type, string expectedJson, object? actual) =>
        RowComparer.CellProblem(type, Json(expectedJson), actual);

    [Fact]
    public void Null_Equals_Only_Null()
    {
        Assert.Null(Problem(FieldType.String, "null", null));
        Assert.Null(Problem(FieldType.String, "null", DBNull.Value));
        Assert.NotNull(Problem(FieldType.String, "null", ""));
        Assert.NotNull(Problem(FieldType.String, "\"\"", null));
        Assert.NotNull(Problem(FieldType.Int64, "0", null));
    }

    [Fact]
    public void Strings_Compare_Ordinally_With_No_Case_Folding()
    {
        Assert.Null(Problem(FieldType.String, "\"ACTIVE\"", "ACTIVE"));
        Assert.NotNull(Problem(FieldType.String, "\"ACTIVE\"", "active"));
        Assert.NotNull(Problem(FieldType.String, "\"a\"", "a "));
    }

    [Fact]
    public void Decimals_Compare_As_Numbers_Not_Scales()
    {
        Assert.Null(Problem(FieldType.Decimal, "\"1.50\"", 1.5m));
        Assert.Null(Problem(FieldType.Decimal, "\"1.5\"", "1.50"));
        Assert.Null(Problem(FieldType.Decimal, "\"0\"", 0.000m));
        Assert.NotNull(Problem(FieldType.Decimal, "\"1.50\"", 1.51m));
    }

    [Fact]
    public void Doubles_Use_A_Relative_Tolerance_And_Are_Exact_Against_Zero()
    {
        Assert.Null(Problem(FieldType.Double, "100.0", 100.0000000001));
        Assert.NotNull(Problem(FieldType.Double, "100.0", 100.001));
        Assert.Null(Problem(FieldType.Double, "0", 0.0));
        Assert.NotNull(Problem(FieldType.Double, "0", 1e-12));
        Assert.True(RowComparer.WithinTolerance(1e12, 1e12 + 100));
    }

    [Fact]
    public void Int64_And_Boolean_Accept_Engine_Spellings()
    {
        Assert.Null(Problem(FieldType.Int64, "42", 42));
        Assert.Null(Problem(FieldType.Int64, "42", 42L));
        Assert.NotNull(Problem(FieldType.Int64, "42", 43L));
        Assert.Null(Problem(FieldType.Boolean, "true", 1L));
        Assert.Null(Problem(FieldType.Boolean, "false", false));
        Assert.NotNull(Problem(FieldType.Boolean, "true", 0L));
    }

    [Fact]
    public void Dates_And_Datetimes_Compare_As_Dates_And_Instants()
    {
        Assert.Null(Problem(FieldType.Date, "\"2026-03-01\"", new DateOnly(2026, 3, 1)));
        Assert.Null(Problem(FieldType.Date, "\"2026-03-01\"", "2026-03-01"));
        Assert.NotNull(Problem(FieldType.Date, "\"2026-03-01\"", new DateOnly(2026, 3, 2)));

        Assert.Null(Problem(FieldType.Datetime, "\"2026-01-01T00:00:00Z\"", "2026-01-01T00:00:00.000Z"));
        Assert.Null(Problem(FieldType.Datetime, "\"2026-01-01T00:00:00.000Z\"", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Null(Problem(FieldType.Datetime, "\"2026-01-01T00:00:00Z\"", new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.FromHours(2))));
        Assert.NotNull(Problem(FieldType.Datetime, "\"2026-01-01T00:00:00Z\"", "2026-01-01T00:00:00.001Z"));
    }

    [Fact]
    public void Columns_Are_Compared_Exactly_Then_Rows_In_Order()
    {
        var expected = new ExpectedResult(["id", "name"], [[Json("1"), Json("\"a\"")], [Json("2"), Json("\"b\"")]]);
        FieldType[] types = [FieldType.Int64, FieldType.String];

        Assert.Empty(RowComparer.Compare(expected, types, ["id", "name"], [[1L, "a"], [2L, "b"]]));
        Assert.NotEmpty(RowComparer.Compare(expected, types, ["name", "id"], [["a", 1L], ["b", 2L]]));
        Assert.NotEmpty(RowComparer.Compare(expected, types, ["id", "name"], [[2L, "b"], [1L, "a"]]));
        Assert.NotEmpty(RowComparer.Compare(expected, types, ["id", "name"], [[1L, "a"]]));
        Assert.NotEmpty(RowComparer.Compare(expected, types, ["id", "name"], [[1L, "a"], [2L]]));
    }
}
