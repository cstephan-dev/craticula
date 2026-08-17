using System.Globalization;

namespace Craticula.Export.Csv.Tests;

/// <summary>
/// EXP-5's "invariant culture by default", per CLR type. The fixtures grade the three types their
/// schema declares (<c>int64</c>, <c>date</c>, <c>decimal</c>, plus strings); everything an adapter
/// can hand the writer is graded here, because a column type appearing in no fixture is exactly the
/// one whose formatting drifts.
/// </summary>
public class CsvValueFormatterTests
{
    [Fact]
    public void A_Null_Is_The_Empty_String()
    {
        Assert.Equal(string.Empty, CsvValueFormatter.Format(null, '.'));
    }

    /// <summary>
    /// The single most consequential rule in this file: a <c>decimal</c> keeps the scale it was read
    /// with. That trailing zero is the column's declared precision — <c>NUMERIC(10,2)</c> — and once
    /// the writer drops it no downstream layer can put it back.
    /// </summary>
    [Theory]
    [InlineData("1234.50", "1234.50")]
    [InlineData("1234.5", "1234.5")]
    [InlineData("0.00", "0.00")]
    [InlineData("-25.75", "-25.75")]
    [InlineData("1000000", "1000000")]
    public void A_Decimal_Keeps_Its_Own_Scale(string literal, string expected)
    {
        var value = decimal.Parse(literal, CultureInfo.InvariantCulture);

        Assert.Equal(expected, CsvValueFormatter.Format(value, '.'));
    }

    [Fact]
    public void The_Decimal_Separator_Replaces_The_Invariant_Point_And_Nothing_Else()
    {
        Assert.Equal("1234,50", CsvValueFormatter.Format(1234.50m, ','));
        Assert.Equal("1234,5", CsvValueFormatter.Format(1234.5d, ','));

        // An integral value has no separator to swap, and the writer emits no thousands separator —
        // so a "1234" must not come out as "1.234" or acquire a stray comma.
        Assert.Equal("1234", CsvValueFormatter.Format(1234L, ','));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(-42, "-42")]
    [InlineData(9_007_199_254_740_993L, "9007199254740993")]
    public void An_Integer_Is_Written_Bare(long value, string expected)
    {
        Assert.Equal(expected, CsvValueFormatter.Format(value, '.'));
    }

    [Fact]
    public void Dates_And_Times_Are_ISO_8601()
    {
        Assert.Equal("2026-01-05", CsvValueFormatter.Format(new DateOnly(2026, 1, 5), '.'));
        Assert.Equal("2026-01-05T14:30:00", CsvValueFormatter.Format(new DateTime(2026, 1, 5, 14, 30, 0), '.'));
        Assert.Equal(
            "2026-01-05T14:30:00.1234567",
            CsvValueFormatter.Format(new DateTime(2026, 1, 5, 14, 30, 0).AddTicks(1234567), '.'));
        Assert.Equal(
            "2026-01-05T14:30:00+02:00",
            CsvValueFormatter.Format(new DateTimeOffset(2026, 1, 5, 14, 30, 0, TimeSpan.FromHours(2)), '.'));
        Assert.Equal("14:30:00", CsvValueFormatter.Format(new TimeOnly(14, 30), '.'));
    }

    [Fact]
    public void A_Boolean_Is_Lowercase_true_Or_false()
    {
        // The JSON/wire spelling, not .NET's "True"/"False": what comes out of an export should be
        // reinjectable as a filter value.
        Assert.Equal("true", CsvValueFormatter.Format(true, '.'));
        Assert.Equal("false", CsvValueFormatter.Format(false, '.'));
    }

    /// <summary>
    /// The rule that makes an export reproducible: the bytes must not depend on the culture of the
    /// thread the request happened to run on. <c>fr-FR</c> would render <c>1234,5</c> and
    /// <c>05/01/2026</c> — the first of which silently splits a comma-delimited row in two.
    /// </summary>
    [Fact]
    public void Ignores_The_Ambient_Culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

            Assert.Equal("1234.5", CsvValueFormatter.Format(1234.5m, '.'));
            Assert.Equal("2026-01-05", CsvValueFormatter.Format(new DateOnly(2026, 1, 5), '.'));
            Assert.Equal("true", CsvValueFormatter.Format(true, '.'));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// A type the writer has no opinion about still exports. Refusing would fail a whole download
    /// over one cell nobody was looking at, and an adapter is free to hand back a driver-specific
    /// wrapper (an ObjectId, an enum) that this module has never heard of.
    /// </summary>
    [Fact]
    public void An_Unknown_Type_Falls_Back_To_Its_Own_Rendering()
    {
        Assert.Equal("Csv", CsvValueFormatter.Format(Craticula.Core.Export.ExportFormat.Csv, '.'));
        Assert.Equal(
            "8a1b1c2d-0000-0000-0000-000000000000",
            CsvValueFormatter.Format(Guid.Parse("8a1b1c2d-0000-0000-0000-000000000000"), '.'));
    }

    [Fact]
    public void A_String_Passes_Through_Untouched()
    {
        // Including one that looks like a number in another culture: the writer formats values, it
        // does not re-parse text an adapter already produced.
        Assert.Equal("1.234,56", CsvValueFormatter.Format("1.234,56", ','));
    }
}
