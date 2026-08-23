using Craticula.AgGrid;
using Craticula.Core.Grid;
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.Search;

namespace Craticula.OpenSearch.Client.Tests;

/// <summary>
/// #1649's glue, tested as a PURE unit — no live cluster needed. Unlike <see cref="OpenSearchGridQueryTests"/>
/// (which proves the query path against a real index), the mapping <see cref="OpenSearchGridPageExtensions.ToGridPageResult"/>
/// does is pure, and its two inputs — an <see cref="OpenSearchGridPage"/> and the
/// <see cref="SearchQueryRequest"/> that produced it — are fully hand-constructible without executing
/// anything against a real cluster. See that extension's own remarks for why it needs the request at
/// all: telling a grouped level's approximate <c>cardinality</c>-based <c>RowCount</c> apart from an
/// ungrouped exact <c>track_total_hits</c> one is not reliably derivable from
/// <see cref="OpenSearchGridPage"/> alone.
/// </summary>
public sealed class OpenSearchGridPageExtensionsTests
{
    private static FieldConfig Field(FieldType type) => new() { Type = type };

    private static readonly GridSchema Schema = new()
    {
        Source = "sales",
        StableKey = "id",
        FieldOrder = ["id", "region", "amount"],
        Fields = new Dictionary<string, FieldConfig>(StringComparer.Ordinal)
        {
            ["id"] = Field(FieldType.Int64),
            ["region"] = Field(FieldType.String),
            ["amount"] = Field(FieldType.Decimal)
        }
    };

    /// <summary>The grouped <c>cardinality</c> path: <c>RowCountIsApproximate</c> becomes
    /// <see langword="true"/>, and the shared mapper turns that into a populated
    /// <c>groupLevelInfo</c>. <c>LastRow</c> (AG-3's Lazy signal) is deliberately <see langword="false"/>
    /// here so this stays a pure test of the approximate-<c>RowCount</c> propagation, undisturbed by
    /// <c>LoadSuccessParamsMapper</c>'s own precedence between an approximate <c>RowCount</c> and an
    /// exact <c>IsLastPage</c>-derived total — that precedence has its own dedicated coverage in
    /// <c>Craticula.AgGrid.Tests.LoadSuccessParamsMapperTests</c>.</summary>
    [Fact]
    public void A_Grouped_Fetchs_Approximate_RowCount_Sets_RowCountIsApproximate_And_GroupLevelInfo()
    {
        var request = new SearchQueryRequest
        {
            Schema = Schema,
            Page = new RangePage { StartRow = 0, EndRow = 20 },
            Group = new GroupSpec { GroupCols = ["region"], GroupKeys = [] },
            Aggregations = [new Aggregation { Field = "amount", Func = AggregationFunc.Sum }]
        };

        var page = new OpenSearchGridPage
        {
            Columns = ["region", "amount_sum"],
            Rows = [["EMEA", 100m], ["APAC", 50m]],
            RowCount = 2,
            LastRow = false
        };

        var result = page.ToGridPageResult(request);
        Assert.True(result.RowCountIsApproximate);

        var mapped = LoadSuccessParamsMapper.Map(result, startRow: 0);
        Assert.NotNull(mapped.GroupLevelInfo);
        Assert.False(mapped.GroupLevelInfo!.RowCountExact);
        Assert.Equal(2, mapped.LastRow);
    }

    /// <summary>The ungrouped <c>track_total_hits</c> path: always exact when non-null (a
    /// capped/inexact total is already mapped to <see langword="null"/> by <c>CountRowsAsync</c>'s own
    /// #1648 fix), so <c>RowCountIsApproximate</c> stays <see langword="false"/> and the shared mapper
    /// leaves <c>groupLevelInfo</c> <see langword="null"/>.</summary>
    [Fact]
    public void An_Ungrouped_Fetchs_Exact_RowCount_Leaves_RowCountIsApproximate_False_And_GroupLevelInfo_Null()
    {
        var request = new SearchQueryRequest
        {
            Schema = Schema,
            Page = new RangePage { StartRow = 0, EndRow = 20 },
            Group = null,
            Aggregations = null
        };

        var page = new OpenSearchGridPage
        {
            Columns = Schema.FieldOrder,
            Rows = [[1L, "EMEA", 100m], [2L, "APAC", 50m]],
            RowCount = 2,
            LastRow = true
        };

        var result = page.ToGridPageResult(request);
        Assert.False(result.RowCountIsApproximate);

        var mapped = LoadSuccessParamsMapper.Map(result, startRow: 0);
        Assert.Null(mapped.GroupLevelInfo);
        Assert.Equal(2, mapped.LastRow);
    }

    /// <summary>GRP-1's drill-down leaf (every group key already consumed) projects raw rows, not
    /// buckets — <see cref="GroupingPlan.GroupColumn"/> is <see langword="null"/> for it, exactly like
    /// an ordinary ungrouped request, so its <c>RowCount</c> (when present) is never flagged
    /// approximate.</summary>
    [Fact]
    public void A_Drill_Down_Leafs_RowCount_Is_Not_Flagged_Approximate()
    {
        var request = new SearchQueryRequest
        {
            Schema = Schema,
            Page = new RangePage { StartRow = 0, EndRow = 20 },
            Group = new GroupSpec { GroupCols = ["region"], GroupKeys = [new StringValue { Value = "EMEA" }] },
            Aggregations = null
        };

        var page = new OpenSearchGridPage
        {
            Columns = Schema.FieldOrder,
            Rows = [[1L, "EMEA", 100m]],
            RowCount = 1,
            LastRow = true
        };

        var result = page.ToGridPageResult(request);
        Assert.False(result.RowCountIsApproximate);
    }

    /// <summary>A grouped fetch that never asked for a count (<c>includeRowCount: false</c>, so
    /// <c>RowCount</c> is <see langword="null"/>) never ran the <c>cardinality</c> aggregation in the
    /// first place — there is no approximate total to flag.</summary>
    [Fact]
    public void A_Grouped_Fetch_With_No_Requested_RowCount_Is_Not_Flagged_Approximate()
    {
        var request = new SearchQueryRequest
        {
            Schema = Schema,
            Page = new RangePage { StartRow = 0, EndRow = 20 },
            Group = new GroupSpec { GroupCols = ["region"], GroupKeys = [] },
            Aggregations = [new Aggregation { Field = "amount", Func = AggregationFunc.Sum }]
        };

        var page = new OpenSearchGridPage
        {
            Columns = ["region", "amount_sum"],
            Rows = [["EMEA", 100m]],
            RowCount = null,
            LastRow = true
        };

        var result = page.ToGridPageResult(request);
        Assert.False(result.RowCountIsApproximate);
        Assert.Null(result.RowCount);
    }
}
