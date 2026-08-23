using Craticula.AgGrid;
using Craticula.Core.Grid;
using Craticula.Core.Ir;
using Craticula.Mongo;

namespace Craticula.MongoDB.Driver.Tests;

/// <summary>
/// #1649's glue, proven end to end against this adapter's own real fixture-driven data (a real MongoDB
/// instance — see <see cref="MongoFixtureDatabase"/>'s own remarks):
/// <see cref="MongoGridPageExtensions.ToGridPageResult"/> plus the shared
/// <see cref="LoadSuccessParamsMapper.Map"/> produce the DoD's literal <c>{ rows, lastRow?,
/// groupLevelInfo? }</c> shape from a real <see cref="MongoGridPage"/>, not a hand-built stand-in.
/// </summary>
public sealed class MongoGridPageExtensionsTests
{
    private static FixtureRequest ExportableOrdersFixture() =>
        FixtureRequest.Load("page-export", FixtureRequest.ResolveDirectory("page-export", "008"));

    [Fact]
    public async Task ToGridPageResult_Plus_The_Shared_Mapper_Produce_AG_Grids_Wire_Shape()
    {
        var fixture = ExportableOrdersFixture();
        await using var database = await MongoFixtureDatabase.CreateAsync(fixture.Schema, Datasets.ExportableOrders());

        var request = new MongoQueryRequest
        {
            Schema = database.Schema,
            Filter = null,
            Sort = null,
            Page = new RangePage { StartRow = 0, EndRow = 100 },
            Group = null,
            Aggregations = null,
            Projection = null
        };

        var page = await MongoGridQuery.FetchPageAsync(database.Database, request, includeRowCount: true);
        var mapped = LoadSuccessParamsMapper.Map(page.ToGridPageResult(), startRow: 0);

        // AG-3's Exact strategy: the known total (5) is the wire lastRow directly, and this backend
        // never populates groupLevelInfo (RowCountIsApproximate is always false here).
        Assert.Equal(5, mapped.LastRow);
        Assert.Null(mapped.GroupLevelInfo);

        Assert.Equal(page.Rows.Count, mapped.Rows.Count);
        var nameIndex = page.Columns.ToList().IndexOf("customerName");
        Assert.Equal(page.Rows[0][nameIndex], mapped.Rows[0]["customerName"]);
    }
}
