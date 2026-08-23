using Craticula.Core.Grid;
using Craticula.Core.Ir;
using Craticula.Search;

namespace Craticula.OpenSearch.Client;

/// <summary>
/// Converts this package's own <see cref="OpenSearchGridPage"/> into #1649's shared
/// <see cref="GridPageResult"/> — additive glue only, added alongside the existing, unchanged
/// <see cref="OpenSearchGridQuery.FetchPageAsync"/> entry point rather than in place of it. A host that
/// wants AG Grid's wire shape calls this, then <c>Craticula.AgGrid.LoadSuccessParamsMapper.Map</c>.
///
/// <para><b>Why this is the only one of the four extensions that needs the original request.</b> This
/// backend is the only one of the four with something for <see cref="GridPageResult.RowCountIsApproximate"/>
/// to report at all: <see cref="OpenSearchGridQuery"/>'s grouped path answers <c>RowCount</c> via a
/// <c>cardinality</c> aggregation (HyperLogLog++, inexact above roughly 3,000 distinct values — its own
/// <c>CountBucketsAsync</c> doc comment), while its ungrouped path answers the same property via
/// <c>track_total_hits</c>, which is always exact when non-null (a capped/inexact total is already
/// mapped to <see langword="null"/> by <c>CountRowsAsync</c>'s own #1648 fix). Telling those two apart
/// from an <see cref="OpenSearchGridPage"/> instance ALONE is not reliable: that type carries no explicit
/// "was this a grouped level" flag (adding one would touch an already-shipped type — out of scope for
/// this additive story), and <see cref="OpenSearchGridPage.Columns"/>' shape is not a safe substitute —
/// an ordinary schema whose own <c>FieldOrder</c> happens to match a grouped level's
/// <c>[groupColumn, ...aggregation outputs]</c> length/shape cannot be told apart from a real grouped
/// level by inspecting <see cref="OpenSearchGridPage.Columns"/> alone, and guessing from shape risks a
/// silent false positive/negative on real data. Instead this extension takes the SAME
/// <see cref="SearchQueryRequest"/> the host already passed to <see cref="OpenSearchGridQuery.FetchPageAsync"/>
/// (a caller that fetched a page necessarily has it) and reuses <see cref="GroupingPlan.Resolve"/> — the
/// exact GRP-1 dispatch <c>OpenSearchGridQuery.FetchPageAsync</c> itself uses to choose between its
/// grouped and ungrouped fetch paths — reading back the SAME decision rather than re-deriving a new one
/// from an unreliable proxy.</para>
/// </summary>
public static class OpenSearchGridPageExtensions
{
    /// <summary>
    /// Maps <paramref name="page"/> straight across into the shared envelope, resolving
    /// <see cref="GridPageResult.RowCountIsApproximate"/> from <paramref name="request"/> — see this
    /// type's own remarks for why the request is needed.
    /// </summary>
    /// <param name="page">The page <see cref="OpenSearchGridQuery.FetchPageAsync"/> returned.</param>
    /// <param name="request">The SAME request passed to that call.</param>
    public static GridPageResult ToGridPageResult(this OpenSearchGridPage page, SearchQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(request);

        var plan = GroupingPlan.Resolve(request.Group, request.Aggregations);
        var isGroupedLevel = plan is { GroupColumn: not null };

        return new GridPageResult
        {
            Columns = page.Columns,
            Rows = page.Rows,
            RowCount = page.RowCount,
            IsLastPage = page.LastRow,

            // Only meaningful together with an actual RowCount: a grouped fetch that never asked for
            // one (includeRowCount: false, RowCount null) never ran the cardinality aggregation in the
            // first place, so there is no approximate total to flag here either.
            RowCountIsApproximate = isGroupedLevel && page.RowCount is not null
        };
    }
}
