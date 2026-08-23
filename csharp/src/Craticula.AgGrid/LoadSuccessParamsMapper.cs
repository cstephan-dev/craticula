using Craticula.Core.Grid;

namespace Craticula.AgGrid;

/// <summary>
/// The one place AG Grid's <c>lastRow</c> — a single numeric wire field — gets derived from whichever of
/// AG-3's two strategies a <see cref="GridPageResult"/> actually carries, and the one place a positional
/// row (every adapter's own internal shape) becomes AG Grid's real, name-keyed wire row. Every prior
/// story (#1645/#1646/#1647/#1648) deliberately stopped one step short of this — see each adapter's own
/// <c>XxxGridPage</c> remarks — so this is written exactly once and reused by all four, which is #1622's
/// definition of done stated as code.
/// </summary>
public static class LoadSuccessParamsMapper
{
    /// <summary>
    /// Maps <paramref name="result"/> into AG Grid's <c>{ rows, lastRow?, groupLevelInfo? }</c> shape.
    /// </summary>
    /// <param name="result">The neutral page an adapter's own <c>ToGridPageResult()</c> extension
    /// produced.</param>
    /// <param name="startRow">The request's own row offset — needed because AG-3's Lazy arithmetic is
    /// <c>startRow + rows.Count</c>, and a <see cref="GridPageResult"/> alone carries no memory of the
    /// request that produced it.</param>
    public static LoadSuccessParams Map(GridPageResult result, long startRow)
    {
        ArgumentNullException.ThrowIfNull(result);

        var rows = new IReadOnlyDictionary<string, object?>[result.Rows.Count];
        for (var rowIndex = 0; rowIndex < result.Rows.Count; rowIndex++)
        {
            var cells = result.Rows[rowIndex];
            var row = new Dictionary<string, object?>(result.Columns.Count, StringComparer.Ordinal);
            for (var columnIndex = 0; columnIndex < result.Columns.Count; columnIndex++)
            {
                row[result.Columns[columnIndex]] = cells[columnIndex];
            }

            rows[rowIndex] = row;
        }

        // AG-3's precedence is EXACTNESS first, presence second — not "RowCount always wins". An exact
        // RowCount wins whenever present (the actual total, not just "was this page short"), and that is
        // still how EF/Dapper/Mongo's own Exact strategy has always worked, since none of them ever
        // report an approximate RowCount at all. But OpenSearch's grouped `cardinality` path can report
        // BOTH signals at once, and when it does, RowCount can be the WEAKER one: it is a HyperLogLog++
        // approximation above ~3,000 distinct group values, while IsLastPage's `startRow + rows.Count`
        // arithmetic (the AG-3 Lazy fetch-one-extra-and-trim probe) is always exact — PROVIDED it is
        // actually grounded in a real page. A grouped level that shrank between requests (the underlying
        // data or filter changed, so the level now has fewer buckets than a stale cached position assumed)
        // can walk out of buckets before ever reaching the requested startRow, coming back as
        // (Rows: [], IsLastPage: true) with startRow > 0 — the arithmetic then computes startRow + 0, which
        // is not "exact", it is simply wrong (the real total is smaller than startRow), and reporting it as
        // exact would be worse than the honest approximate RowCount it would have overridden. Nothing in a
        // GridPageResult tells the two cases apart directly, so the guard is indirect: the derived value
        // is only trusted to override an approximate RowCount when this fetch actually returned rows, or
        // the request was for the very first row (an empty result at startRow == 0 genuinely does mean
        // "zero total, done"). This guard applies ONLY to the override-an-approximate-RowCount path — when
        // RowCount is null there is no approximate fallback to protect, so the derived value remains the
        // only stop signal available even in this same edge case (it may occasionally overestimate, but
        // that is preferable to no signal at all). When RowCount is approximate and IsLastPage is
        // false/unknown, RowCount is still the only signal available — an approximate "keep going" answer
        // beats no answer, so it must still be used rather than regressing to null. Walked out fully:
        //   RowCount exact,       IsLastPage unknown/true/false                    -> RowCount (unaffected)
        //   RowCount approximate, IsLastPage == true, derived value grounded       -> derived (the bug case)
        //   RowCount approximate, IsLastPage == true, derived value NOT grounded   -> RowCount (shrunk level)
        //   RowCount approximate, IsLastPage != true                              -> RowCount (only signal)
        //   RowCount null,        IsLastPage == true                              -> derived (no guard)
        //   RowCount null,        IsLastPage != true                              -> null
        var exactDerivedFromIsLastPage = result.IsLastPage == true ? startRow + result.Rows.Count : (long?)null;
        var derivedValueIsGroundedInData = result.Rows.Count > 0 || startRow == 0;

        long? lastRow;
        bool lastRowIsExact;
        if (result.RowCount is { } rowCount &&
            !(result.RowCountIsApproximate && exactDerivedFromIsLastPage is not null && derivedValueIsGroundedInData))
        {
            lastRow = rowCount;
            lastRowIsExact = !result.RowCountIsApproximate;
        }
        else if (exactDerivedFromIsLastPage is not null)
        {
            lastRow = exactDerivedFromIsLastPage;
            lastRowIsExact = true;
        }
        else
        {
            lastRow = null;
            lastRowIsExact = false; // Nothing exact was derivable at all — a degenerate combination that
                                     // does not occur through any adapter's own ToGridPageResult() today
                                     // (RowCountIsApproximate is only ever true alongside a non-null
                                     // RowCount), but kept honest rather than defaulting to true.
        }

        // GroupLevelInfo must describe the WIRE lastRow that was actually emitted, not just echo the raw
        // RowCount.RowCountIsApproximate flag — once the precedence above lets an exact derived value win
        // over an approximate RowCount, reporting RowCountExact: false for that response would contradict
        // the exact lastRow it is attached to.
        var groupLevelInfo = result.RowCountIsApproximate
            ? new GroupLevelInfo { RowCountExact = lastRowIsExact }
            : null;

        return new LoadSuccessParams { Rows = rows, LastRow = lastRow, GroupLevelInfo = groupLevelInfo };
    }
}
