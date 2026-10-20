using System.Text;
using System.Text.Json;

namespace Demo.Api.Tests;

/// <summary>
/// What the EF Core routes (<c>ef-sqlserver</c>, <c>ef-sqlite</c>) are held to against <c>postgres</c>: exact equality, numbers by value
/// (12877.0 equals 12877), except keys ending <c>_avg</c>, which every provider compares with a relative 1e-6 (PostgreSQL averages a
/// decimal with sixteen digits, SQL Server with six). Measured on 2026-10-20: no key needs a rule beyond that on either EF route, because
/// EF averages an integer column as a float; the Dapper SQL Server route's integer average (<c>quantity_avg</c>) does not occur here.
/// </summary>
internal static class EfParity
{
    public static bool Same(JsonElement a, JsonElement b, string key = "")
    {
        if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
        {
            var left = a.EnumerateObject().ToArray();
            return left.Length == b.EnumerateObject().Count()
                && left.All(property => b.TryGetProperty(property.Name, out var other) && Same(property.Value, other, property.Name));
        }

        if (a.ValueKind == JsonValueKind.Array && b.ValueKind == JsonValueKind.Array)
        {
            return a.GetArrayLength() == b.GetArrayLength()
                && a.EnumerateArray().Zip(b.EnumerateArray()).All(pair => Same(pair.First, pair.Second, key));
        }

        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
        {
            if (key.EndsWith("_avg", StringComparison.Ordinal))
            {
                var x = a.GetDouble();
                var y = b.GetDouble();
                return Math.Abs(x - y) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y)));
            }

            return a.GetDecimal() == b.GetDecimal();
        }

        return a.ValueKind == b.ValueKind && a.GetRawText() == b.GetRawText();
    }

    /// <summary>Two CSV tables are the same when every cell is the same text, or both cells are the same number (SQLite writes <c>22932.99</c>
    /// where PostgreSQL writes <c>22932.9900</c>, the way smoke.sh compares them).</summary>
    public static bool CsvSame(List<List<string>> a, List<List<string>> b) =>
        a.Count == b.Count
        && a.Zip(b).All(rows => rows.First.Count == rows.Second.Count
            && rows.First.Zip(rows.Second).All(cells => cells.First == cells.Second
                || (decimal.TryParse(cells.First, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var x)
                    && decimal.TryParse(cells.Second, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var y)
                    && x == y)));

    public static List<List<string>> ParseCsv(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes[3..]).Replace("\r\n", "\n");
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(',').ToList())];
    }
}
