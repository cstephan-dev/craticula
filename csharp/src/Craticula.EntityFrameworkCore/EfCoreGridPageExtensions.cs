using Craticula.Core.Grid;

namespace Craticula.EntityFrameworkCore;

/// <summary>
/// Converts this package's own <see cref="EfCoreGridPage"/> into #1649's shared
/// <see cref="GridPageResult"/> — additive glue only, added alongside the existing, unchanged
/// <see cref="EfCoreGridQuery.FetchPageAsync{TRow}"/> entry point rather than in place of it. A host
/// that wants AG Grid's wire shape calls this, then <c>Craticula.AgGrid.LoadSuccessParamsMapper.Map</c>.
///
/// <para>EF Core only ever implements AG-3's <b>Exact</b> strategy (<see cref="EfCoreGridPage.RowCount"/>
/// — an opt-in <c>COUNT</c> round trip, never a probe row), so <see cref="GridPageResult.IsLastPage"/> is
/// always <see langword="null"/> here and <see cref="GridPageResult.RowCountIsApproximate"/> is always
/// <see langword="false"/> — this package's own <c>RowCount</c> is always an exact
/// <c>COUNT(*)</c>.</para>
/// </summary>
public static class EfCoreGridPageExtensions
{
    /// <summary>Maps <paramref name="page"/> straight across into the shared envelope — see this type's
    /// own remarks for why every member is a direct carry-over except <see cref="GridPageResult.IsLastPage"/>,
    /// which this backend has no way to answer.</summary>
    public static GridPageResult ToGridPageResult(this EfCoreGridPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new GridPageResult
        {
            Columns = page.Columns,
            Rows = page.Rows,
            RowCount = page.RowCount,
            IsLastPage = null,
            RowCountIsApproximate = false
        };
    }
}
