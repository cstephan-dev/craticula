using System.Data;
using Craticula.Core.Schema;

namespace Craticula.Dapper.Tests;

/// <summary>
/// SEM-4's result side (Bug #2511): a <c>datetime</c> cell leaves the adapter as an absolute UTC instant. SQL Server's
/// <c>datetime2</c> and MySQL's <c>DATETIME</c> come back from the driver as a <see cref="DateTime"/> of kind
/// <see cref="DateTimeKind.Unspecified"/>, which a JSON serialiser writes without the trailing <c>Z</c>.
/// </summary>
public class DapperValueReaderDatetimeTests
{
    private static readonly DateTime Wall = new(2026, 3, 1, 14, 39, 26, 589);

    private static object? Read(object value)
    {
        using var table = new DataTable();
        var column = table.Columns.Add("at", value.GetType());
        if (value is DateTime { Kind: var kind })
        {
            // A DataTable rewrites the kind of a stored DateTime to its column's mode: pick the mode that keeps the one under test.
            column.DateTimeMode = kind switch
            {
                DateTimeKind.Utc => DataSetDateTime.Utc,
                DateTimeKind.Local => DataSetDateTime.Local,
                _ => DataSetDateTime.Unspecified
            };
        }

        table.Rows.Add(value);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        return DapperValueReader.Read(reader, 0, FieldType.Datetime);
    }

    [Fact]
    public void A_Zoneless_Value_Is_Read_As_UTC()
    {
        var read = Assert.IsType<DateTime>(Read(DateTime.SpecifyKind(Wall, DateTimeKind.Unspecified)));

        Assert.Equal(DateTimeKind.Utc, read.Kind);
        Assert.Equal(Wall, read); // the same wall-clock reading, now said to be UTC
        Assert.EndsWith("Z", read.ToString("O"));
    }

    [Fact]
    public void A_UTC_Value_Is_Unchanged()
    {
        var utc = DateTime.SpecifyKind(Wall, DateTimeKind.Utc);

        var read = Assert.IsType<DateTime>(Read(utc));

        Assert.Equal(DateTimeKind.Utc, read.Kind);
        Assert.Equal(utc, read);
    }

    [Fact]
    public void A_Local_Value_Is_Converted_To_The_Same_Instant_In_UTC()
    {
        var local = DateTime.SpecifyKind(Wall, DateTimeKind.Local);

        var read = Assert.IsType<DateTime>(Read(local));

        Assert.Equal(DateTimeKind.Utc, read.Kind);
        Assert.Equal(local.ToUniversalTime(), read);
    }

    [Fact]
    public void An_Offset_Value_Is_Converted_To_Its_UTC_Instant()
    {
        var offset = new DateTimeOffset(Wall, TimeSpan.FromHours(2));

        var read = Assert.IsType<DateTime>(Read(offset));

        Assert.Equal(DateTimeKind.Utc, read.Kind);
        Assert.Equal(offset.UtcDateTime, read);
    }

    [Fact]
    public void An_ISO_Text_Instant_Is_Read_As_UTC()
    {
        var read = Assert.IsType<DateTime>(Read("2026-03-01T14:39:26.589Z"));

        Assert.Equal(DateTimeKind.Utc, read.Kind);
        Assert.Equal(Wall, read);
    }
}
