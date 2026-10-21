using Craticula.Core.Ir;
using Demo.Api.Errors;

namespace Demo.Api.Grid;

/// <summary>
/// The grouped request shapes OpenSearch cannot answer the way PostgreSQL does, found by measuring every grouped shape the page can
/// send against PostgreSQL (docs/feat/2501/validation.md, "Grouped shapes"). Each check is structural and runs before the engine is
/// asked, so the answer is the same on every route (rows, query view) and never depends on matching an error text.
/// <para>A shape is listed here only when the baseline answers it and OpenSearch cannot: a request that every provider refuses (a sort on
/// a column the grouped level does not produce, two aggregates under one column id) is left to the compiler and answers
/// <c>compilation-failed</c> on OpenSearch as everywhere else. Adding an entry needs the evidence in validation.md.</para>
/// </summary>
internal static class OpenSearchShapes
{
    /// <summary>Throws <see cref="UnsupportedShapeException"/> for a grouped request OpenSearch cannot answer; does nothing otherwise.</summary>
    public static void ThrowIfUnsupported(PreparedRows request, string engine = "OpenSearch")
    {
        if (request.Parsed.Group is not { } group)
        {
            return;
        }

        // Ordering the groups by an aggregate. A composite aggregation lists groups in key order only (OSE-11(c)); sorting the first page would
        //    answer "the first groups by key, rearranged", not "the top groups". Counted so that an ambiguous sort key (two aggregates under one
        //    column id) stays the compiler's refusal that every provider gives.
        var groupColumn = group.GroupKeys.Count < group.GroupCols.Count ? group.GroupCols[group.GroupKeys.Count] : null;
        if (groupColumn is null)
        {
            return;
        }

        var aggregations = request.Parsed.Aggregations ?? [];
        foreach (var sort in request.Parsed.Sort ?? [])
        {
            if (sort.Field != groupColumn && aggregations.Count(aggregation => aggregation.ColumnId == sort.Field) == 1)
            {
                throw new UnsupportedShapeException(
                    $"{engine} cannot order groups by an aggregate ('{sort.Field}'): groups come back in the order of their own '{groupColumn}' value only, " +
                    $"so the result would be the first groups by '{groupColumn}', not the top ones. Sort by '{groupColumn}', or use another provider for this ordering.");
            }
        }
    }
}
