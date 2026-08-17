using System.Globalization;

namespace Craticula.Export.Csv;

/// <summary>
/// EXP-5's "invariant culture by default", made explicit for every CLR value an execution adapter
/// can hand the writer. Nothing here consults <see cref="CultureInfo.CurrentCulture"/>: an export's
/// bytes must not depend on the thread the request happened to run on, which is the same reason
/// SEM-4 forbids resolving a date against the server process's local timezone.
///
/// <para><b>The formatting is driven by the CLR type of the value, not by the schema's declared
/// <c>FieldType</c>.</b> The adapter has already turned a database value into a CLR one and it is
/// the only party that knows how (a <c>NUMERIC</c> may arrive as a <see cref="decimal"/>, a
/// MongoDB <c>Decimal128</c> may arrive as a string); asking the writer to re-derive the type from
/// the schema would let the two disagree, with the schema winning over the actual value. Per-field
/// <em>overrides</em> (EXP-5's "date pattern, decimal separator" in schema config) are a different
/// thing and are not implemented: the schema carries no such field yet (#1631). The default patterns
/// below are ISO 8601, which is what "invariant" means for a date.</para>
/// </summary>
public static class CsvValueFormatter
{
    /// <summary>ISO 8601 calendar date — the invariant rendering of a <see cref="DateOnly"/>, and the
    /// one every fixture's <c>date</c>-typed column is pinned to.</summary>
    private const string DatePattern = "yyyy-MM-dd";

    /// <summary>ISO 8601 local date-time, seconds always present and fractional seconds only when the
    /// value has any (<c>F</c>, not <c>f</c>) — so a whole-second timestamp does not carry seven
    /// zeros no consumer asked for.</summary>
    private const string DateTimePattern = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF";

    /// <summary>As above, plus the offset: a <see cref="DateTimeOffset"/> that dropped its offset
    /// would be a different instant.</summary>
    private const string DateTimeOffsetPattern = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz";

    private const string TimePattern = "HH:mm:ss.FFFFFFF";

    /// <summary>
    /// The text of one cell, before any RFC 4180 quoting. Returns the empty string for
    /// <see langword="null"/>: a CSV file has no null, and quoting an empty field would claim the
    /// opposite (that a value was present and happened to be empty), which is a distinction no CSV
    /// reader can carry anyway.
    /// </summary>
    public static string Format(object? value, char decimalSeparator) => value switch
    {
        null => string.Empty,

        // Ordered by what an adapter actually yields, most common first. `string` first also means
        // the common case never reaches the IFormattable probe at the bottom.
        string text => text,

        // Integral types carry no fractional part, so the decimal separator never applies to them —
        // running the swap anyway would be harmless today and wrong the day someone adds a thousands
        // separator to this method.
        bool flag => flag ? "true" : "false",
        byte or sbyte or short or ushort or int or uint or long or ulong
            => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),

        // AR-7's `decimal` type: ToString(InvariantCulture) preserves the value's own scale, so a
        // 1234.50 read out of a NUMERIC(10,2) column stays "1234.50" rather than becoming "1234.5".
        // That trailing zero is data — it is the column's declared precision — and no other layer
        // can put it back.
        decimal number => ApplyDecimalSeparator(number.ToString(CultureInfo.InvariantCulture), decimalSeparator),
        double number => ApplyDecimalSeparator(number.ToString(CultureInfo.InvariantCulture), decimalSeparator),
        float number => ApplyDecimalSeparator(number.ToString(CultureInfo.InvariantCulture), decimalSeparator),

        DateOnly date => date.ToString(DatePattern, CultureInfo.InvariantCulture),
        DateTime timestamp => timestamp.ToString(DateTimePattern, CultureInfo.InvariantCulture),
        DateTimeOffset timestamp => timestamp.ToString(DateTimeOffsetPattern, CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString(TimePattern, CultureInfo.InvariantCulture),

        // Everything else that knows how to render itself culture-independently does so; anything
        // left (an enum, a driver's own wrapper type) falls back to ToString(). Deliberately NOT a
        // rejection: refusing to export a column because the writer has no opinion about its type
        // would fail an export over a cell nobody was looking at.
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>Swaps the invariant culture's <c>.</c> for the configured separator. Operates on the
    /// already-formatted text rather than on a <see cref="NumberFormatInfo"/> clone so that the
    /// scale-preserving <c>ToString</c> above stays the single source of the digits.</summary>
    private static string ApplyDecimalSeparator(string formatted, char decimalSeparator) =>
        decimalSeparator == '.' ? formatted : formatted.Replace('.', decimalSeparator);
}
