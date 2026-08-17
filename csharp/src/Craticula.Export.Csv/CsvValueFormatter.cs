using System.Globalization;
using Craticula.Core.Schema;

namespace Craticula.Export.Csv;

/// <summary>
/// EXP-5's "invariant culture by default", made explicit for every CLR value an execution adapter
/// can hand the writer. Nothing here consults <see cref="CultureInfo.CurrentCulture"/>: an export's
/// bytes must not depend on the thread the request happened to run on, which is the same reason
/// SEM-4 forbids resolving a date against the server process's local timezone. That holds for an
/// overridden field too — a per-field pattern is applied <em>with</em> the invariant culture, so
/// <c>dd/MM/yyyy</c> emits <c>05/01/2026</c> on a <c>de-DE</c> thread and not <c>05.01.2026</c>.
///
/// <para><b>The formatting is driven by the CLR type of the value, not by the schema's declared
/// <c>FieldType</c>.</b> The adapter has already turned a database value into a CLR one and it is
/// the only party that knows how (a <c>NUMERIC</c> may arrive as a <see cref="decimal"/>, a
/// MongoDB <c>Decimal128</c> may arrive as a string); asking the writer to re-derive the type from
/// the schema would let the two disagree, with the schema winning over the actual value.</para>
///
/// <para><b>EXP-5's other half — per-field <em>overrides</em> — is the second parameter, and it does
/// not change that.</b> A <see cref="FormatOptions"/> says how the host wants this column rendered
/// (a date pattern, a decimal separator); which arm below renders it is still the value's own type.
/// The two are independent on purpose: an override that also implied a type would be the schema
/// winning over the value through a side door. A column that declares nothing renders exactly as it
/// did before #1653 — the default patterns below are ISO 8601, which is what "invariant" means for a
/// date.</para>
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
    /// <param name="value">The cell, as the adapter yielded it.</param>
    /// <param name="decimalSeparator">The writer-wide default (<see cref="CsvWriterOptions.DecimalSeparator"/>),
    /// used for every field <paramref name="format"/> does not override.</param>
    /// <param name="format">EXP-5's per-field overrides for this column
    /// (<see cref="Craticula.Core.Export.ExportColumn.Format"/>), or <see langword="null"/> for none.
    /// Optional so that a caller with no per-field notion at all — every call site before #1653, and
    /// every unit test of the invariant defaults — reads as the absence of an override rather than as
    /// one that happens to be empty.</param>
    public static string Format(object? value, char decimalSeparator, FormatOptions? format = null) => value switch
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
        decimal number => ApplyDecimalSeparator(
            number.ToString(CultureInfo.InvariantCulture), Separator(decimalSeparator, format)),
        double number => ApplyDecimalSeparator(
            number.ToString(CultureInfo.InvariantCulture), Separator(decimalSeparator, format)),
        float number => ApplyDecimalSeparator(
            number.ToString(CultureInfo.InvariantCulture), Separator(decimalSeparator, format)),

        // A DateOnly is lifted to midnight when — and only when — a pattern was declared. A host's
        // pattern may legitimately name time specifiers (a `datetime` field's pattern is the natural
        // place for them) and DateOnly.ToString throws FormatException on those, which would surface
        // at the first date cell: mid-stream, after the header row and an arbitrary number of rows
        // have already reached the client. Lifting makes every pattern GridSchema.Parse accepted
        // total over the arms below, which is what makes that check's two probes — a DateTime and a
        // DateTimeOffset, the only two types a pattern now reaches — exactly sufficient rather than
        // merely indicative (FormatOptions.IsValid).
        DateOnly date => format?.DatePattern is { } datePattern
            ? date.ToDateTime(TimeOnly.MinValue).ToString(datePattern, CultureInfo.InvariantCulture)
            : date.ToString(DatePattern, CultureInfo.InvariantCulture),
        DateTime timestamp => timestamp.ToString(
            format?.DatePattern ?? DateTimePattern, CultureInfo.InvariantCulture),
        DateTimeOffset timestamp => timestamp.ToString(
            format?.DatePattern ?? DateTimeOffsetPattern, CultureInfo.InvariantCulture),

        // Deliberately NOT overridable: EXP-5 names a DATE pattern, and a date pattern cannot render a
        // time of day without inventing a date to go with it. No FieldType produces a TimeOnly either,
        // so an override here would describe a column the schema cannot declare.
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

    /// <summary>The field's own separator when it declares one, else the writer-wide default. An
    /// override REPLACES the default rather than being combined with it — there is nothing to combine,
    /// and a fallback chain of two characters is the shape a reader can predict.</summary>
    private static char Separator(char decimalSeparator, FormatOptions? format) =>
        format?.DecimalSeparator ?? decimalSeparator;
}
