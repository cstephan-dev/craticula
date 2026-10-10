using System.Globalization;
using System.Text.Json;
using Craticula.Core.Schema;

namespace Craticula.Integration.Tests.Oracle;

/// <summary>
/// XEQ-3's comparison rule, once, for every backend: the logical column list compared exactly, then the rows
/// in order, then each cell by its type (<c>spec/semantics.md</c> XEQ-3). A harness maps an engine's rows onto
/// the logical columns first (physical names, <c>_id</c>, hit metadata) and hands them over as CLR values:
/// <c>long</c>/<c>int</c> for int64, <c>double</c>/<c>float</c> for double, <c>decimal</c> or numeric/string for
/// decimal, <c>bool</c>, <c>string</c>, <see cref="DateOnly"/>/<see cref="DateTime"/>/<see cref="DateTimeOffset"/>
/// or an ISO string for dates, and <c>null</c> (or <see cref="DBNull"/>) for a missing value.
/// </summary>
public static class RowComparer
{
    /// <summary>Relative tolerance for doubles (XEQ-3).</summary>
    public const double DoubleTolerance = 1e-9;

    /// <summary>Relative tolerance for an <c>avg</c> aggregate column (XEQ-3): engines differ in an average's scale.</summary>
    public const double AverageTolerance = 1e-6;

    /// <summary>The mismatches between <paramref name="expected"/> and an engine's <paramref name="actual"/> rows; empty means equal.</summary>
    public static IReadOnlyList<string> Compare(
        ExpectedResult expected,
        IReadOnlyList<FieldType> columnTypes,
        IReadOnlyList<string> actualColumns,
        IReadOnlyList<IReadOnlyList<object?>> actualRows,
        IReadOnlySet<string>? averageColumns = null)
    {
        var problems = new List<string>();
        if (!expected.Columns.SequenceEqual(actualColumns, StringComparer.Ordinal))
        {
            problems.Add($"columns: expected [{string.Join(", ", expected.Columns)}], got [{string.Join(", ", actualColumns)}]");
            return problems;
        }

        if (expected.Rows.Count != actualRows.Count)
        {
            problems.Add($"row count: expected {expected.Rows.Count}, got {actualRows.Count}");
        }

        for (var row = 0; row < Math.Min(expected.Rows.Count, actualRows.Count); row++)
        {
            if (actualRows[row].Count != expected.Columns.Count)
            {
                problems.Add($"row {row}: {actualRows[row].Count} cells for {expected.Columns.Count} columns");
                continue;
            }

            for (var column = 0; column < expected.Columns.Count; column++)
            {
                var problem = CellProblem(
                    columnTypes[column],
                    expected.Rows[row][column],
                    actualRows[row][column],
                    averageColumns is not null && averageColumns.Contains(expected.Columns[column]) ? AverageTolerance : DoubleTolerance);
                if (problem is not null)
                {
                    problems.Add($"row {row}, column '{expected.Columns[column]}': {problem}");
                }
            }
        }

        return problems;
    }

    /// <summary>Why <paramref name="actual"/> is not <paramref name="expected"/> under XEQ-3 for <paramref name="type"/>, or null when equal.</summary>
    public static string? CellProblem(FieldType type, JsonElement expected, object? actual, double tolerance = DoubleTolerance)
    {
        var expectedNull = expected.ValueKind == JsonValueKind.Null;
        var actualNull = actual is null or DBNull;
        if (expectedNull || actualNull)
        {
            return expectedNull && actualNull
                ? null
                : $"expected {Describe(expected)}, got {(actualNull ? "null" : Convert.ToString(actual, CultureInfo.InvariantCulture))}";
        }

        var equal = type switch
        {
            FieldType.Int64 => ToInt64(actual!) == expected.GetInt64(),
            FieldType.Boolean => ToBoolean(actual!) == expected.GetBoolean(),
            FieldType.String => string.Equals(Convert.ToString(actual, CultureInfo.InvariantCulture), expected.GetString(), StringComparison.Ordinal),
            FieldType.Decimal => ToDecimal(actual!) == ToDecimal(expected),
            FieldType.Double => WithinTolerance(ToDouble(actual!), ToDouble(expected), tolerance),
            FieldType.Date => ToDate(actual!) == DateOnly.ParseExact(expected.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            FieldType.Datetime => ToInstant(actual!) == ToInstant(expected.GetString()!),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown FieldType.")
        };

        return equal ? null : $"expected {Describe(expected)}, got {Convert.ToString(actual, CultureInfo.InvariantCulture)}";
    }

    /// <summary>|a − b| ≤ tolerance · max(|a|, |b|) (1e-9 by default), and exact when either side is 0 (XEQ-3).</summary>
    public static bool WithinTolerance(double a, double b, double tolerance = DoubleTolerance) =>
        a == b || (a != 0 && b != 0 && Math.Abs(a - b) <= tolerance * Math.Max(Math.Abs(a), Math.Abs(b)));

    private static string Describe(JsonElement element) => element.ValueKind == JsonValueKind.Null ? "null" : element.GetRawText();

    private static long ToInt64(object value) =>
        value is string text ? long.Parse(text, CultureInfo.InvariantCulture) : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static bool ToBoolean(object value) =>
        value is long or int or short or byte ? Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0 : Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    private static double ToDouble(object value) => Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static double ToDouble(JsonElement element) => element.GetDouble();

    private static decimal ToDecimal(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? decimal.Parse(element.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture)
            : element.GetDecimal();

    private static decimal ToDecimal(object value) =>
        value is string text
            ? decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
            : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static DateOnly ToDate(object value) => value switch
    {
        DateOnly date => date,
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        DateTimeOffset offset => DateOnly.FromDateTime(offset.UtcDateTime),
        string text => DateOnly.Parse(text[..10], CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"Cannot read {value.GetType().Name} as a date.")
    };

    private static DateTimeOffset ToInstant(object value) => value switch
    {
        DateTimeOffset offset => offset,
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, dateTime.Kind == DateTimeKind.Local ? DateTimeKind.Local : DateTimeKind.Utc)),
        string text => ToInstant(text),
        _ => throw new InvalidOperationException($"Cannot read {value.GetType().Name} as an instant.")
    };

    // A datetime without an offset is an absolute UTC instant (SEM-4); AssumeUniversal says so.
    private static DateTimeOffset ToInstant(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
