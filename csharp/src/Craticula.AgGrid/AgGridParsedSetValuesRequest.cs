using Craticula.Core.Ir;

namespace Craticula.AgGrid;

/// <summary>
/// BM-3: the output of <see cref="AgGridRequestParser.ParseSetValues"/> — a set-filter
/// distinct-values request turned into candidate IR, but not yet merged/scoped/allowlisted/limited/
/// coerced. Feeds <see cref="Craticula.Core.Schema.GridFilterPipeline.ProcessSetValues"/>, exactly as
/// <see cref="AgGridParsedRequest"/> feeds <see cref="Craticula.Core.Schema.GridFilterPipeline.Process"/>.
///
/// <para><b>A separate record because it is a separate request.</b> The <c>setValuesRequest</c>
/// envelope is not an <c>IServerSideGetRowsRequest</c> and shares none of its fields — no
/// <c>startRow</c>/<c>endRow</c>, no <c>sortModel</c>, no <c>rowGroupCols</c> — so representing it as
/// an <see cref="AgGridParsedRequest"/> would mean a required <see cref="AgGridParsedRequest.Page"/>
/// nobody sent. See <c>spec/fixtures/group-aggregation/README.md</c> for the envelope's shape and why
/// it is Craticula's own rather than AG Grid's.</para>
/// </summary>
public sealed record AgGridParsedSetValuesRequest
{
    /// <summary>The envelope's <c>colId</c>, <c>searchPrefix</c> and requested <c>limit</c>.</summary>
    public required SetValuesSpec SetValues { get; init; }

    /// <summary>The filter tree built from the envelope's own <c>filterModel</c> — the grid's current
    /// filters, <b>including</b> any on <see cref="SetValuesSpec.ColId"/> itself. Dropping that one is
    /// GRP-6's own-field exclusion and is the pipeline's job, not the adapter's: it is a semantic rule
    /// every backend shares, and an adapter that pre-applied it would leave the rule unenforced for
    /// any host building the IR directly.</summary>
    public FilterNode? NativeFilter { get; init; }

    /// <summary>The <c>customFilters</c> (OQ-11) extension fragment, deserialized directly as IR and
    /// not yet merged with <see cref="NativeFilter"/> — same boundary as
    /// <see cref="AgGridParsedRequest.CustomFilters"/>.</summary>
    public FilterNode? CustomFilters { get; init; }
}
