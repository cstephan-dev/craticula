using Craticula.Core.Ir;
using Craticula.Core.Schema;

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
    public void An_Offset_Value_Becomes_The_Same_Instant_As_A_Utc_DateTime()
    {
        var raw = new DateTimeOffset(Instant.AddHours(2).Ticks, TimeSpan.FromHours(2));

        var result = Assert.IsType<DateTime>(EfCoreValueNormalizer.Normalize(raw));

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(Instant, result);
    }

    [Fact]
    public void Other_Values_Pass_Through_Untouched()
    {
        Assert.Null(EfCoreValueNormalizer.Normalize(null));
        Assert.Equal("x", EfCoreValueNormalizer.Normalize("x"));
        Assert.Equal(new DateOnly(2024, 10, 1), EfCoreValueNormalizer.Normalize(new DateOnly(2024, 10, 1)));
    }

    private static readonly GridSchema EventSchema = new()
    {
        Source = "ev", StableKey = "id", FieldOrder = ["id", "region", "at"],
        Fields = new Dictionary<string, FieldConfig>(StringComparer.Ordinal)
        {
            ["id"] = new() { Type = FieldType.Int64, Sortable = true },
            ["region"] = new() { Type = FieldType.String, Sortable = true },
            ["at"] = new() { Type = FieldType.Datetime, Sortable = true }
        }
    };

    private static readonly DateTime Later = new(2024, 10, 2, 1, 0, 0, 1, DateTimeKind.Utc);

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> EventRows() =>
    [
        new Dictionary<string, object?> { ["id"] = 1L, ["region"] = "A", ["at"] = Instant },
        new Dictionary<string, object?> { ["id"] = 2L, ["region"] = "A", ["at"] = Later },
        new Dictionary<string, object?> { ["id"] = 3L, ["region"] = "B", ["at"] = Later },
        new Dictionary<string, object?> { ["id"] = 4L, ["region"] = "B", ["at"] = null },
        new Dictionary<string, object?> { ["id"] = 5L, ["region"] = "B", ["at"] = Instant }
    ];

    [Fact]
    public async Task A_Grouped_Level_Delivers_Min_And_Max_Of_A_Datetime_As_Utc()
    {
        using var database = SqliteFixtureDatabase.Create(EventSchema, EventRows());
        var request = new EfCoreQueryRequest
        {
            Schema = EventSchema,
            Page = new RangePage { StartRow = 0, EndRow = 10 },
            Group = new GroupSpec { GroupCols = ["region"], GroupKeys = [] },
            Aggregations =
            [
                new Aggregation { Field = "at", Func = AggregationFunc.Min },
                new Aggregation { Field = "at", Func = AggregationFunc.Max }
            ]
        };

        var page = await EfCoreGridQuery.FetchPageAsync(database.Rows(), request, Sqlite);

        var first = page.Rows[0];
        Assert.Equal(Instant, Assert.IsType<DateTime>(first[1]));
        Assert.Equal(Later, Assert.IsType<DateTime>(first[2]));
        Assert.All(page.Rows.SelectMany(row => row.Skip(1)), cell => Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTime>(cell).Kind));
    }

    /// <summary>The keyset keys are not the output cells: normalising the output must leave the boundary as read, so a chunked export on a
    /// datetime key still emits every row once, in order, at chunk sizes that put the boundary inside a tie.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_Chunked_Export_Sorted_On_A_Datetime_Emits_Every_Row_Once_As_Utc(int chunkSize)
    {
        using var database = SqliteFixtureDatabase.Create(EventSchema, EventRows());
        var request = new EfCoreQueryRequest
        {
            Schema = EventSchema,
            Page = new NonePage(),
            Sort = [new SortItem { Field = "at", Direction = SortDirection.Desc, NullsPosition = NullsPosition.First }]
        };

        var ids = new List<long>();
        await foreach (var row in EfCoreExportStream.StreamAsync(database.Rows(), request, Sqlite, new EfCoreExportOptions { ChunkSize = chunkSize }))
        {
            ids.Add((long)row[0]!);
            if (row[2] is DateTime value)
            {
                Assert.Equal(DateTimeKind.Utc, value.Kind);
            }
        }

        Assert.Equal([4L, 2L, 3L, 1L, 5L], ids);
    }
}
