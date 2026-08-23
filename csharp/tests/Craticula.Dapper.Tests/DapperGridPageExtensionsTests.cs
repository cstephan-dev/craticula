using Craticula.AgGrid;
using Craticula.Core.Grid;
using Craticula.Sql;

namespace Craticula.Dapper.Tests;

/// <summary>
/// #1649's glue, proven end to end against this adapter's own real fixture-driven data:
/// <see cref="DapperGridPageExtensions.ToGridPageResult"/> plus the shared
/// <see cref="LoadSuccessParamsMapper.Map"/> produce the DoD's literal <c>{ rows, lastRow?,
/// groupLevelInfo? }</c> shape from a real <see cref="DapperGridPage"/>, not a hand-built stand-in.
/// </summary>
public class DapperGridPageExtensionsTests
{
    private static readonly ISqlDialect Sqlite = SqliteDialect.Instance;

    private static FixtureRequest Paged() =>
        FixtureRequest.Load("sort-paging", FixtureRequest.ResolveDirectory("sort-paging", "011"));

    [Fact]
    public async Task ToGridPageResult_Plus_The_Shared_Mapper_Produce_AG_Grids_Wire_Shape()
    {
        var fixture = Paged();
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.PagedLastNames());

        var page = await DapperGridQuery.FetchPageAsync(
            database.Connection, fixture.Sql, Sqlite, includeRowCount: true);

        var mapped = LoadSuccessParamsMapper.Map(page.ToGridPageResult(), startRow: 20);

        // AG-3's Exact strategy: the known total (35) is the wire lastRow directly, and this backend
        // never populates groupLevelInfo (RowCountIsApproximate is always false here).
        Assert.Equal(35, mapped.LastRow);
        Assert.Null(mapped.GroupLevelInfo);

        Assert.Equal(page.Rows.Count, mapped.Rows.Count);
        var lastNameIndex = page.Columns.ToList().IndexOf("lastName");
        Assert.Equal(page.Rows[0][lastNameIndex], mapped.Rows[0]["lastName"]);
    }
}
