using Microsoft.EntityFrameworkCore;

namespace Craticula.EntityFrameworkCore;

/// <summary>
/// The <b>grid</b> entry point: one page of rows for an AG Grid SSRM block, optionally with the
/// filtered total.
///
/// <para><b>Why this is a separate type from <see cref="EfCoreExportStream"/> rather than a mode of one
/// entry point.</b> The two answer the same IR against the same table and differ in exactly one thing
/// that matters: this one may use <c>OFFSET</c> and the export path may not (EXP-3). A single method
/// with a flag would put that distinction in a boolean somebody can pass wrongly, and the symptom —
/// an export that OFFSET-walks — is not an error but a download that gets quadratically slower and
/// still returns the right bytes on the small dataset a test would use. Two types make the rule
/// structural: this one calls <c>ApplyRowWindow</c>, that one has no offset to give it, and a test
/// reads its source to prove <c>Skip</c> never appears there.</para>
///
/// <para>It is also the shape of the difference in the request: a grid page is defined BY its
/// <see cref="Craticula.Core.Ir.RangePage"/>, while an export's page is EXP-2's
/// <see cref="Craticula.Core.Ir.NonePage"/> and its chunking is the adapter's own concern rather than
/// the client's.</para>
/// </summary>
public static class EfCoreGridQuery
{
    /// <summary>
    /// Compiles and executes one grid page: filter, order, row window, projection.
    ///
    /// <para>The clause order is the order a reader of <c>SqlQueryBuilder.Build</c> would expect, and
    /// two of the four steps are not interchangeable. The row window must come after the ordering (a
    /// <c>Skip</c>/<c>Take</c> over an unordered query returns an arbitrary page — which is what SEM-6's
    /// tiebreaker exists to prevent, and why <c>ApplyOrdering</c> can never return an empty key list),
    /// and the projection must come last so that a field the request sorts or filters on but does not
    /// project is still available to the clauses that need it.</para>
    /// </summary>
    /// <param name="source">The <c>FROM</c> target — a <c>DbSet</c>, or
    /// <see cref="GridSchemaPropertyBag.Query"/> for a host with no entity classes. Any EF global query
    /// filter already on it composes with everything below, which is AR-4's reason for existing.</param>
    /// <param name="request">The validated IR. Its <see cref="EfCoreQueryRequest.Page"/> is honoured
    /// here, unlike on the export path.</param>
    /// <param name="semantics">The five engine facts SEM-1/SEM-7/SEC-4/SQL-7 need.</param>
    /// <param name="includeRowCount">Whether to also run the count query
    /// (<see cref="EfCoreGridPage.RowCount"/>). Defaults to <see langword="false"/>: it is a second
    /// round trip, and a grid needs the total once per filter change rather than once per block.</param>
    /// <param name="cancellationToken">EXP-7's cancellation hook, observed by both queries.</param>
    public static async Task<EfCoreGridPage> FetchPageAsync<TRow>(
        IQueryable<TRow> source,
        EfCoreQueryRequest request,
        EfCoreProviderSemantics semantics,
        bool includeRowCount = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(semantics);

        var filtered = EfCoreQueryBuilder.ApplyFilter(source, request, semantics);

        // Counted on the FILTERED query with no ordering and no row window — AG Grid's rowCount is "how
        // many rows match", not "how many are on this page". Ordering a count is wasted work every
        // engine would have to plan around, and applying the window would make the answer the page size.
        var rowCount = includeRowCount
            ? await filtered.LongCountAsync(cancellationToken).ConfigureAwait(false)
            : (long?)null;

        var columns = EfCoreQueryBuilder.ResolveProjection(request);

        var page = EfCoreQueryBuilder.ApplyProjection(
            EfCoreQueryBuilder.ApplyRowWindow(
                EfCoreQueryBuilder.ApplyOrdering(filtered, request, semantics),
                request.Page),
            request.Schema,
            columns);

        var rows = new List<IReadOnlyList<object?>>();
        await foreach (var cells in page.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(cells);
        }

        return new EfCoreGridPage
        {
            Columns = columns,
            Rows = rows,
            RowCount = rowCount
        };
    }

    /// <summary>
    /// The filtered row count on its own, for a host that fetches the total on a different cadence from
    /// the blocks — the same query <see cref="FetchPageAsync{TRow}"/> runs when asked, exposed so that
    /// asking for it separately cannot mean asking a different question.
    /// </summary>
    public static Task<long> CountAsync<TRow>(
        IQueryable<TRow> source,
        EfCoreQueryRequest request,
        EfCoreProviderSemantics semantics,
        CancellationToken cancellationToken = default) =>
        EfCoreQueryBuilder.ApplyFilter(source, request, semantics).LongCountAsync(cancellationToken);
}
