using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.EntityFrameworkCore.Tests;

/// <summary>
/// Bug #2805: two ways to configure this adapter run without error and return wrong rows, so each is documented on
/// <see cref="EfCoreProviderSemantics"/> and in the README, and each is pinned here — the wrong answer without the obligation
/// and the right one with it. The adapter does not guard or work around either (the host owns the mapping and the collation),
/// so these tests are the evidence that the documented obligation is the whole fix.
/// <list type="number">
/// <item><description><b>SQLite instants.</b> A host stores <c>yyyy-MM-ddTHH:mm:ss.fffZ</c> text (SEM-4's SQLite arm). EF's own SQLite
/// mapping binds a <see cref="DateTime"/> as <c>yyyy-MM-dd HH:mm:ss</c>, and on the bound's own date <c>'T'</c> sorts after <c>' '</c>.
/// A host maps every datetime to and from the stored text.</description></item>
/// <item><description><b><c>PatternMatchFoldsCase</c>.</b> <see langword="true"/> emits a bare <c>LIKE</c>, which follows the column's
/// collation; it is right only when every searchable column's collation folds case. SQLite's <c>LIKE</c> always folds ASCII, so the
/// wrong rows cannot be reproduced on this engine (the demo measured them on a binary-collated SQL Server column: 0 rows
/// against 1,933); the statement EF sends is what is pinned.</description></item>
/// </list>
/// </summary>
public class EfCoreHostObligationTests
{
    private static readonly EfCoreProviderSemantics Sqlite = EfCoreProviderSemantics.Sqlite;

    private static DateTime At(int hour, int minute, int second = 0) => new(2026, 2, 1, hour, minute, second, DateTimeKind.Utc);

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> SameDayRows() =>
    [
        new Dictionary<string, object?> { ["id"] = 1L, ["createdAt"] = At(8, 0) },
        new Dictionary<string, object?> { ["id"] = 2L, ["createdAt"] = At(9, 30) },
        new Dictionary<string, object?> { ["id"] = 3L, ["createdAt"] = At(12, 0) }, // the bound, not at midnight
        new Dictionary<string, object?> { ["id"] = 4L, ["createdAt"] = At(14, 45) },
        new Dictionary<string, object?> { ["id"] = 5L, ["createdAt"] = At(18, 0) },
        new Dictionary<string, object?> { ["id"] = 6L, ["createdAt"] = null }
    ];

    private static async Task<List<long>> FilteredIdsAsync(bool mapped)
    {
        // operators/024: createdAt <= 2026-02-01T00:00:00Z; the bound is replaced by a non-midnight one below.
        var fixture = FixtureRequest.Load("operators", FixtureRequest.ResolveDirectory("operators", "024"));
        var filter = new ComparisonNode
        {
            Field = "createdAt",
            Op = ComparisonOperator.Lte,
            Value = new DatetimeValue { Value = "2026-02-01T12:00:00Z", Zone = "UTC" }
        };
        var request = fixture.Ef with { Filter = filter, Page = new RangePage { StartRow = 0, EndRow = 100 }, Sort = null };

        using var database = SqliteFixtureDatabase.Create(fixture.Schema, SameDayRows(), storedInstantText: true, mapStoredInstants: mapped);
        var page = await EfCoreGridQuery.FetchPageAsync(database.Rows(), request, Sqlite);

        var index = page.Columns.ToList().IndexOf("id");
        return [.. page.Rows.Select(row => (long)row[index]!).Order()];
    }

    [Fact]
    public async Task A_Non_Midnight_Bound_On_Sqlite_Needs_The_Stored_Text_Mapping()
    {
        // With the host mapping, EF binds '2026-02-01T12:00:00.000Z', the stored form: lte includes the row ON the bound.
        Assert.Equal([1L, 2L, 3L], await FilteredIdsAsync(mapped: true));

        // Without it EF binds '2026-02-01 12:00:00'; on that date every stored 'T…' value is greater, so only the rows
        // of earlier dates would pass: here none, and the row ON the bound is lost with the two before it.
        Assert.Empty(await FilteredIdsAsync(mapped: false));
    }

    private static async Task<(List<long> Ids, bool Threw)> ExportIdsAsync(bool mapped, int chunkSize)
    {
        var fixture = FixtureRequest.Load("sort-paging", FixtureRequest.ResolveDirectory("sort-paging", "012"));
        var request = fixture.Ef with
        {
            Page = new NonePage(),
            Sort = [new SortItem { Field = "createdAt", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }]
        };

        using var database = SqliteFixtureDatabase.Create(fixture.Schema, SameDayRows(), storedInstantText: true, mapStoredInstants: mapped);
        var ids = new List<long>();
        try
        {
            await foreach (var row in EfCoreExportStream.StreamAsync(
                               database.Rows(), request, Sqlite, new EfCoreExportOptions { ChunkSize = chunkSize }))
            {
                ids.Add((long)row[0]!);
                if (ids.Count > 50)
                {
                    break; // the unmapped keyset can walk without end: 50 rows of six is divergence enough
                }
            }
        }
        catch (EfCoreCompilationException)
        {
            return (ids, true);
        }

        return (ids, false);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_Datetime_Sorted_Export_On_Sqlite_Needs_The_Stored_Text_Mapping(int chunkSize)
    {
        var (withMapping, threw) = await ExportIdsAsync(mapped: true, chunkSize);
        Assert.False(threw);
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], withMapping);

        // Without it the keyset boundary binds in EF's own form and compares wrong against the stored text: rows repeat or
        // drop at the chunk boundaries (the demo measured 20,060 and 19,947 rows for 20,000), or the duplicate-key check stops it.
        var (withoutMapping, stopped) = await ExportIdsAsync(mapped: false, chunkSize);
        Assert.True(stopped || !withoutMapping.SequenceEqual([1L, 2L, 3L, 4L, 5L, 6L]));
    }

    [Fact]
    public void Pattern_Match_Folds_Case_True_Sends_A_Bare_Like_Whose_Folding_Is_The_Columns_Collation()
    {
        var request = new EfCoreQueryRequest
        {
            Schema = new GridSchema
            {
                Source = "items",
                StableKey = "id",
                FieldOrder = ["id", "name"],
                Fields = new Dictionary<string, FieldConfig>(StringComparer.Ordinal)
                {
                    ["id"] = new() { Type = FieldType.Int64, Sortable = true },
                    ["name"] = new() { Type = FieldType.String, Sortable = true }
                }
            },
            Filter = new ComparisonNode { Field = "name", Op = ComparisonOperator.Contains, Value = new StringValue { Value = "Germ" } },
            Page = new NonePage()
        };

        string Sql(EfCoreProviderSemantics semantics)
        {
            using var database = SqliteFixtureDatabase.Create(request.Schema, []);
            return SqliteFixtureDatabase.GeneratedSql(EfCoreQueryBuilder.ApplyFilter(database.Rows(), request, semantics));
        }

        // true: the column's own collation decides, so a binary-collated column matches exact case only.
        var bare = Sql(Sqlite with { PatternMatchFoldsCase = true });
        Assert.Contains("LIKE", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("lower(", bare, StringComparison.Ordinal);
        Assert.Contains("'%Germ%'", bare, StringComparison.Ordinal);

        // false: both sides are lowered, right for every column whatever its collation.
        var lowered = Sql(Sqlite with { PatternMatchFoldsCase = false });
        Assert.Contains("lower(", lowered, StringComparison.Ordinal);
        Assert.Contains("'%germ%'", lowered, StringComparison.Ordinal);
    }
}
