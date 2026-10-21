using Craticula.Core.Ir;

namespace Craticula.EntityFrameworkCore.Tests;

/// <summary>
/// Bug #2798 / SEM-4's result side: a <c>datetime</c> cell leaves the adapter as a UTC instant, whatever kind the provider
/// materialised. EF's SQLite provider reads a zone-less stored instant as an unspecified kind, the same shape SQL Server's
/// <c>datetime2</c> and MySQL's <c>DATETIME</c> give, so the page and the export are proven on it; the conversion table
/// itself is proven on the three kinds directly.
/// </summary>
public class EfCoreDatetimeKindTests
{
    private static readonly EfCoreProviderSemantics Sqlite = EfCoreProviderSemantics.Sqlite;

    private static readonly DateTime Instant = new(2024, 10, 1, 14, 39, 26, 589, DateTimeKind.Utc);

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Dataset() =>
    [
        new Dictionary<string, object?> { ["id"] = 1L, ["createdAt"] = Instant },
        new Dictionary<string, object?> { ["id"] = 2L, ["createdAt"] = null }
    ];

    private static FixtureRequest Fixture() =>
        FixtureRequest.Load("sort-paging", FixtureRequest.ResolveDirectory("sort-paging", "012"));

    [Fact]
    public async Task A_Page_Delivers_Datetime_Cells_As_Utc()
    {
        var fixture = Fixture();
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Dataset());
        var request = fixture.Ef with { Page = new RangePage { StartRow = 0, EndRow = 10 } };

        var page = await EfCoreGridQuery.FetchPageAsync(database.Rows(), request, Sqlite);

        var index = page.Columns.ToList().IndexOf("createdAt");
        var cells = page.Rows.Select(row => row[index]).ToList();
        var value = Assert.IsType<DateTime>(Assert.Single(cells, c => c is not null));
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(Instant, value);
        Assert.Contains(cells, c => c is null);
    }

    [Fact]
    public async Task An_Export_Delivers_Datetime_Cells_As_Utc()
    {
        var fixture = Fixture();
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Dataset());
        var request = fixture.Ef with { Page = new NonePage() };

        var dates = new List<DateTime>();
        await foreach (var row in EfCoreExportStream.StreamAsync(database.Rows(), request, Sqlite))
        {
            var cell = row[1];
            if (cell is DateTime value)
            {
                dates.Add(value);
            }
        }

        var single = Assert.Single(dates);
        Assert.Equal(DateTimeKind.Utc, single.Kind);
        Assert.Equal(Instant, single);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Every_Kind_Becomes_Utc_Per_The_Sem4_Table(DateTimeKind kind)
    {
        // Utc: unchanged. Unspecified: the wall-clock reading as UTC. Local: the same instant converted to UTC.
        var raw = kind switch
        {
            DateTimeKind.Utc => Instant,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(Instant, DateTimeKind.Unspecified),
            _ => Instant.ToLocalTime()
        };

        var result = Assert.IsType<DateTime>(EfCoreValueNormalizer.Normalize(raw));

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(Instant, result);
    }

    [Fact]
    public void An_Offset_Value_Becomes_The_Same_Instant_At_Offset_Zero()
    {
        var raw = new DateTimeOffset(Instant.AddHours(2).Ticks, TimeSpan.FromHours(2));

        var result = Assert.IsType<DateTimeOffset>(EfCoreValueNormalizer.Normalize(raw));

        Assert.Equal(TimeSpan.Zero, result.Offset);
        Assert.Equal(new DateTimeOffset(Instant), result);
    }

    [Fact]
    public void Other_Values_Pass_Through_Untouched()
    {
        Assert.Null(EfCoreValueNormalizer.Normalize(null));
        Assert.Equal("x", EfCoreValueNormalizer.Normalize("x"));
        Assert.Equal(new DateOnly(2024, 10, 1), EfCoreValueNormalizer.Normalize(new DateOnly(2024, 10, 1)));
    }
}
