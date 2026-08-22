using System.Data;
using System.Globalization;
using Craticula.Core.Schema;

namespace Craticula.Dapper;

/// <summary>
/// #1622/#1652/#1653's native-CLR-values obligation, implemented for real: reads one cell through the
/// schema's DECLARED <see cref="FieldType"/> rather than trusting whatever CLR type the underlying
/// ADO.NET provider happened to hand back for it.
///
/// <para><b>Why this cannot be skipped.</b> <c>Craticula.Dapper</c> takes an already-open
/// <see cref="IDbConnection"/> from the host, so the provider behind it is unknown at compile time — a
/// Postgres <c>numeric</c> that exceeds <see cref="decimal"/>'s range can surface as a
/// <see cref="System.Numerics.BigInteger"/>, a timezone-aware column can surface as a
/// <see cref="DateTimeOffset"/> where the schema declares a plain <see cref="FieldType.Datetime"/>, and
/// a range/array wrapper type has no defined shape at all. None of that is a defect in the driver — it
/// is simply richer than the seven-shape vocabulary <c>ir.schema.json</c> declares — but a cell that
/// reaches <c>CsvWriter</c> in the wrong CLR shape produces a spurious formatting artifact (or trips its
/// own <c>IsInHardeningScope</c> default-deny) instead of the value the schema promised. This mirrors
/// the *test-only* <c>Craticula.EntityFrameworkCore.Tests.SqliteFixtureDatabase.ReadDeclared</c> dispatch
/// pattern, but as real production code — no shared helper for this existed anywhere in the repo before
/// this package, since no prior execution adapter reads a raw driver value at all (EF materializes
/// through its own model, never a bare <see cref="IDataRecord"/>).</para>
///
/// <para>Every arm accepts the value unchanged when it already arrived as the target CLR type (the
/// common case for a well-behaved provider) and falls back to conversion/parsing only when it did not —
/// which is also what turns an unconvertible wrapper type (an array, a range) into a loud
/// <see cref="InvalidCastException"/>/<see cref="FormatException"/> rather than a silent wrong
/// answer.</para>
/// </summary>
internal static class DapperValueReader
{
    /// <summary>
    /// Reads the cell at <paramref name="ordinal"/> as <paramref name="type"/>'s declared CLR shape, or
    /// <see langword="null"/> when the column is SQL NULL.
    /// </summary>
    public static object? Read(IDataRecord record, int ordinal, FieldType type)
    {
        if (record.IsDBNull(ordinal))
        {
            return null;
        }

        var raw = record.GetValue(ordinal);

        return type switch
        {
            FieldType.String => ToStringValue(raw),
            FieldType.Int64 => ToInt64(raw),
            FieldType.Decimal => ToDecimal(raw),
            FieldType.Double => ToDouble(raw),
            FieldType.Boolean => ToBoolean(raw),
            FieldType.Date => ToDateOnly(raw),
            FieldType.Datetime => ToDateTime(raw),
            _ => throw new DapperCompilationException($"Unmapped FieldType '{type}'.")
        };
    }

    private static string ToStringValue(object raw) =>
        raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture)
            ?? throw new DapperCompilationException(
                $"Column value '{raw}' ({raw.GetType().Name}) could not be read as a string (FieldType.String).");

    private static long ToInt64(object raw) =>
        raw is long value ? value : Convert.ToInt64(raw, CultureInfo.InvariantCulture);

    /// <summary>
    /// AR-7's <c>decimal</c> shape. A provider whose native numeric type exceeds <see cref="decimal"/>'s
    /// range (a <see cref="System.Numerics.BigInteger"/> for an out-of-range Postgres <c>numeric</c>)
    /// throws <see cref="OverflowException"/> here rather than being silently truncated: the schema
    /// declared <see cref="FieldType.Decimal"/>, and a value this package cannot faithfully represent in
    /// that shape is a loud failure, not a rounding decision this adapter is entitled to make.
    /// </summary>
    private static decimal ToDecimal(object raw) =>
        raw is decimal value ? value : Convert.ToDecimal(raw, CultureInfo.InvariantCulture);

    private static double ToDouble(object raw) =>
        raw is double value ? value : Convert.ToDouble(raw, CultureInfo.InvariantCulture);

    private static bool ToBoolean(object raw) =>
        raw is bool value ? value : Convert.ToBoolean(raw, CultureInfo.InvariantCulture);

    /// <summary>
    /// A local date, never carrying a time-of-day or zone component (AR-7). Accepts a native
    /// <see cref="DateOnly"/>, a <see cref="DateTime"/> (the common case for a DATE column — its
    /// time-of-day component is discarded, since a DATE column has none to begin with), or the
    /// ISO-8601 <c>yyyy-MM-dd</c> text SQLite's TEXT affinity stores it as.
    /// </summary>
    private static DateOnly ToDateOnly(object raw) => raw switch
    {
        DateOnly value => value,
        DateTime value => DateOnly.FromDateTime(value),
        string text => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => throw new DapperCompilationException(
            $"Column value '{raw}' ({raw.GetType().Name}) could not be read as a date (FieldType.Date).")
    };

    /// <summary>
    /// SEM-4's absolute instant. Accepts a <see cref="DateTime"/> (the common case), a
    /// <see cref="DateTimeOffset"/> (a provider that maps a timezone-aware column to it — converted to
    /// its UTC instant, never its offset-local wall-clock reading, matching SEM-4's "the absolute instant
    /// is what a datetimeValue denotes"), or an ISO-8601 instant string (SQLite's TEXT affinity).
    /// </summary>
    private static DateTime ToDateTime(object raw) => raw switch
    {
        DateTime value => value,
        DateTimeOffset value => value.UtcDateTime,
        string text => DateTime.Parse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        _ => throw new DapperCompilationException(
            $"Column value '{raw}' ({raw.GetType().Name}) could not be read as a datetime (FieldType.Datetime).")
    };
}
