using Craticula.Core.Ir;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Fixture-driven coverage against the real <c>spec/fixtures/sort-paging/</c> suite (12 cases,
/// SEM-3/SEM-6/AR-9/AG-3). None of these fixtures' own <c>sortModel</c> carries the SEM-6 stableKey
/// tiebreaker — appending it is <see cref="Craticula.Core.Schema.SemanticsResolver.AppendStableKeyTiebreaker"/>'s
/// job, a documented gap with no consuming pipeline yet (see <see cref="AgGridParsedRequest"/>'s own
/// doc comment); this parser only maps <c>sortModel</c> as given, one <see cref="SortItem"/> per
/// entry.
/// </summary>
public class AgGridRequestParserSortPagingFixtureTests
{
    private static AgGridParsedRequest Parse(string fixtureDir) =>
        AgGridRequestParser.Parse(File.ReadAllText(RepoPaths.Fixture("sort-paging", fixtureDir, "agGridRequest.json")));

    [Theory]
    [InlineData("001-single-key-string-asc-nulls-last-default", "lastName", SortDirection.Asc, NullsPosition.Last)]
    [InlineData("002-single-key-int64-asc-nulls-last-default", "age", SortDirection.Asc, NullsPosition.Last)]
    [InlineData("003-single-key-date-asc-nulls-last-default", "hireDate", SortDirection.Asc, NullsPosition.Last)]
    [InlineData("004-single-key-string-desc-nulls-first-default", "lastName", SortDirection.Desc, NullsPosition.First)]
    [InlineData("005-single-key-int64-desc-nulls-first-default", "age", SortDirection.Desc, NullsPosition.First)]
    [InlineData("008-stablekey-tiebreaker-appended-decimal-primary", "salary", SortDirection.Asc, NullsPosition.Last)]
    [InlineData("009-stablekey-tiebreaker-appended-boolean-primary", "isActive", SortDirection.Desc, NullsPosition.First)]
    public void Single_Key_Fixtures_Get_The_Adapter_Pinned_Default_NullsPosition(
        string fixtureDir, string field, SortDirection direction, NullsPosition expectedNullsPosition)
    {
        var sort = Parse(fixtureDir).Sort!;

        Assert.Single(sort);
        Assert.Equal(field, sort[0].Field);
        Assert.Equal(direction, sort[0].Direction);
        Assert.Equal(expectedNullsPosition, sort[0].NullsPosition);
    }

    [Fact]
    public void Fixture006_Multi_Key_Two_Fields_Preserves_Array_Order()
    {
        var sort = Parse("006-multi-key-two-fields-order-preserved").Sort!;

        Assert.Equal(2, sort.Count);
        Assert.Equal("department", sort[0].Field);
        Assert.Equal(SortDirection.Asc, sort[0].Direction);
        Assert.Equal(NullsPosition.Last, sort[0].NullsPosition);
        Assert.Equal("salary", sort[1].Field);
        Assert.Equal(SortDirection.Desc, sort[1].Direction);
        Assert.Equal(NullsPosition.First, sort[1].NullsPosition);
    }

    [Fact]
    public void Fixture007_Multi_Key_Three_Fields_Preserves_Array_Order()
    {
        var sort = Parse("007-multi-key-three-fields-order-preserved").Sort!;

        Assert.Equal(3, sort.Count);
        Assert.Equal(["department", "age", "hireDate"], sort.Select(s => s.Field));
        Assert.Equal([SortDirection.Asc, SortDirection.Desc, SortDirection.Asc], sort.Select(s => s.Direction));
    }

    [Fact]
    public void Fixture010_SortModel_Already_Ending_In_StableKey_Is_Passed_Through_As_Is()
    {
        var sort = Parse("010-stablekey-tiebreaker-not-appended-sort-ends-in-key").Sort!;

        Assert.Equal(2, sort.Count);
        Assert.Equal("lastName", sort[0].Field);
        Assert.Equal("id", sort[1].Field);
    }

    [Fact]
    public void Fixture011_Page_Range_At_A_Nonzero_Offset()
    {
        var parsed = Parse("011-page-range-with-tiebreaker");

        var page = Assert.IsType<RangePage>(parsed.Page);
        Assert.Equal(20, page.StartRow);
        Assert.Equal(40, page.EndRow);
    }

    [Fact]
    public void Fixture012_ExportScope_All_Is_Out_Of_Scope_Parser_Still_Resolves_RangePage_From_The_Literal_Numbers()
    {
        // This fixture's own point (per its description.md) is that EXP-1's exportScope:"all"
        // extension makes a *compiler* resolve page to $defs/nonePage, ignoring startRow/endRow
        // entirely. That resolution is out of this story's scope (the plan's page-parsing rule is
        // startRow/endRow-only) -- this parser has no exportScope handling at all, so it reports the
        // RangePage the literal startRow/endRow numbers describe. Documented gap, not a bug: a future
        // EXP-1/EXP-2 story must apply the override on top of this parser's output.
        var parsed = Parse("012-page-none-unpaginated-export");

        var page = Assert.IsType<RangePage>(parsed.Page);
        Assert.Equal(0, page.StartRow);
        Assert.Equal(100, page.EndRow);

        Assert.Single(parsed.Sort!);
        Assert.Equal("createdAt", parsed.Sort![0].Field);
        Assert.Equal(SortDirection.Desc, parsed.Sort[0].Direction);
        Assert.Equal(NullsPosition.First, parsed.Sort[0].NullsPosition);
    }
}
