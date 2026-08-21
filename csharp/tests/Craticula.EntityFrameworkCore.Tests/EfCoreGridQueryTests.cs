using Craticula.Core.Ir;
using Microsoft.EntityFrameworkCore;

namespace Craticula.EntityFrameworkCore.Tests;

/// <summary>
/// The grid entry point's own contract: what a page carries, and what the optional count means.
///
/// <para>The ROWS a page contains are asserted by <see cref="EfCoreExecutionEqualityTests"/> (ungrouped
/// pages) and <see cref="EfCoreGroupedExecutionEqualityTests"/> (grouped/leaf pages, #1888), each against
/// the SQL compiler and against a hand-computed sequence. What is left for here is the envelope — which
/// is the part #1645 had to invent, and therefore the part with no oracle behind it at all
/// (see <see cref="EfCoreGridPage"/> on why it is deliberately not OQ-8's shared response type).</para>
/// </summary>
public class EfCoreGridQueryTests
{
    private static readonly EfCoreProviderSemantics Sqlite = EfCoreProviderSemantics.Sqlite;

    private static FixtureRequest Paged() =>
        FixtureRequest.Load("sort-paging", FixtureRequest.ResolveDirectory("sort-paging", "011"));

    /// <summary>
    /// AG Grid SSRM's <c>rowCount</c> is "how many rows match the filter", NOT "how many are on this
    /// page" — so the count query drops the row window (and the ordering, which a count does not need).
    /// The distinction is only visible on a request whose page is smaller than its result, which is why
    /// <c>sort-paging/011</c> (35 rows, a 20-row window landing 15) is the fixture used.
    /// </summary>
    [Fact]
    public async Task The_Row_Count_Is_The_Filtered_Total_And_Not_The_Page_Size()
    {
        var fixture = Paged();
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.PagedLastNames());

        var page = await EfCoreGridQuery.FetchPageAsync(
            database.Rows(), fixture.Ef, Sqlite, includeRowCount: true);

        Assert.Equal(15, page.Rows.Count);
        Assert.Equal(35, page.RowCount);
    }

    /// <summary>
    /// <see langword="null"/> and <c>0</c> are different answers, and conflating them empties a grid: AG
    /// Grid treats an unknown total as "keep asking for blocks" and a zero as "there is nothing". So a
    /// caller that did not ask for the count gets <see langword="null"/>, and the count query is not run.
    /// </summary>
    [Fact]
    public async Task An_Unrequested_Row_Count_Is_Null_And_Costs_No_Query()
    {
        var fixture = Paged();
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.PagedLastNames());

        var page = await EfCoreGridQuery.FetchPageAsync(database.Rows(), fixture.Ef, Sqlite);

        Assert.Null(page.RowCount);
        Assert.Single(database.ExecutedCommands);
    }

    /// <summary>The separate count entry point answers the same question as the flag, so a host that
    /// fetches the total on a different cadence from its blocks cannot be asking something subtly
    /// different.</summary>
    [Fact]
    public async Task The_Standalone_Count_Agrees_With_The_Pages_Own()
    {
        var fixture = Paged();
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.PagedLastNames());

        var page = await EfCoreGridQuery.FetchPageAsync(
            database.Rows(), fixture.Ef, Sqlite, includeRowCount: true);
        var standalone = await EfCoreGridQuery.CountAsync(database.Rows(), fixture.Ef, Sqlite);

        Assert.Equal(page.RowCount, standalone);
    }

    /// <summary>
    /// EXP-1(b): a grid page with no explicit projection returns every declared field in GRP-3's
    /// declaration order, under its LOGICAL colId — not the physical column names <c>SELECT *</c> would
    /// hand back. <c>sort-paging/011</c>'s <c>lastName</c> lives in <c>last_name</c>, so the two differ
    /// and the assertion is not vacuous.
    /// </summary>
    [Fact]
    public async Task A_Page_Names_Its_Columns_By_Logical_ColId_In_Declaration_Order()
    {
        var fixture = Paged();
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.PagedLastNames());

        var page = await EfCoreGridQuery.FetchPageAsync(database.Rows(), fixture.Ef, Sqlite);

        Assert.Equal(fixture.Schema.FieldOrder, page.Columns);
        Assert.Contains("lastName", page.Columns);
        Assert.Equal("last_name", SqliteFixtureDatabase.PhysicalColumn(fixture.Schema, "lastName"));

        foreach (var row in page.Rows)
        {
            Assert.Equal(page.Columns.Count, row.Count);
        }
    }

    /// <summary>
    /// EXP-1(b) again, from the other side: an explicit projection is honoured in the order it was
    /// asked for, and it may be a SUBSET that omits the stable key the ordering still needs.
    /// </summary>
    [Fact]
    public async Task An_Explicit_Projection_Is_Honoured_In_Request_Order()
    {
        var fixture = FixtureRequest.Load("page-export", FixtureRequest.ResolveDirectory("page-export", "008"));
        var request = fixture.Ef with { Page = new RangePage { StartRow = 0, EndRow = 3 } };

        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.ExportableOrders());
        var page = await EfCoreGridQuery.FetchPageAsync(database.Rows(), request, Sqlite);

        Assert.Equal(["customerName", "totalAmount", "orderDate"], page.Columns);
        Assert.Equal(3, page.Rows.Count);
        Assert.All(page.Rows, row => Assert.Equal(3, row.Count));
    }

    /// <summary>
    /// AR-4's stated reason for existing, exercised: a filter the host has already put on its own
    /// <see cref="IQueryable{T}"/> — EF's <c>HasQueryFilter</c> in production, a plain <c>Where</c> here,
    /// which is what the global filter compiles to — composes with everything this adapter adds, and the
    /// adapter never sees it. A compiled <c>{sql, params[]}</c> artifact could only have been edited.
    /// </summary>
    [Fact]
    public async Task A_Filter_Already_On_The_Queryable_Composes_And_Is_Never_Seen()
    {
        var fixture = Paged();
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, Datasets.PagedLastNames());

        // Stand-in for tenancy/soft-delete: only the first ten rows are visible to this caller at all.
        var tenanted = database.Rows().Where(row => EF.Property<long?>(row, "id") <= 10L);

        var page = await EfCoreGridQuery.FetchPageAsync(
            database.Rows(), fixture.Ef, Sqlite, includeRowCount: true);
        var scoped = await EfCoreGridQuery.FetchPageAsync(
            tenanted, fixture.Ef, Sqlite, includeRowCount: true);

        Assert.Equal(35, page.RowCount);
        Assert.Equal(10, scoped.RowCount);

        // The window is rows 20..40 of a 10-row result, so the page is empty — which is the point: the
        // host's own predicate reached the count AND the paging, not just the rows.
        Assert.Empty(scoped.Rows);
    }
}
