using Craticula.Core.Export;

namespace Craticula.AgGrid;

/// <summary>
/// EXP-1: whether the export streams the whole filtered+sorted result or exactly the rows the grid
/// is currently showing. <see cref="All"/> is the wire default and maps to EXP-2's
/// <c>page: { mode: "none" }</c>, discarding <c>startRow</c>/<c>endRow</c> entirely.
/// </summary>
public enum ExportScope
{
    /// <summary>"Export everything the current filter and sort select." Any <c>startRow</c>/
    /// <c>endRow</c> on the request is discarded — <c>page-export/006</c> sends
    /// <c>startRow: 200, endRow: 250</c> and compiles with no row-range clause at all.</summary>
    All,

    /// <summary>"Export exactly what I can see." The request's own <c>startRow</c>/<c>endRow</c> is
    /// honored unchanged (<c>page-export/007</c>).</summary>
    Page
}

/// <summary>
/// EXP-1's wire-contract extension, already resolved: what a request means when it carries
/// <c>exportFormat</c>/<c>exportScope</c>/<c>exportColumns</c> beside the ordinary AG Grid SSRM
/// fields. Built by <see cref="AgGridRequestParser.Parse"/>; <see langword="null"/> on
/// <see cref="AgGridParsedRequest.Export"/> means the request is an ordinary row fetch.
///
/// <para><b>Resolved, not echoed.</b> <see cref="Columns"/> is always the final, ordered list the
/// export emits — the wire's own <c>exportColumns</c> when it supplied one, otherwise every field the
/// schema declares <see cref="Craticula.Core.Schema.FieldConfig.Exportable"/>, in declaration order
/// (EXP-1(b)) — and each entry carries its resolved header and its
/// <see cref="Craticula.Core.Schema.FieldConfig.Format"/> (EXP-5). Resolving it here rather than in each
/// compiler is what keeps the SQL and Mongo projections from being able to disagree about the default,
/// and is why <see cref="AgGridParseOptions.Schema"/> is required for an export request.</para>
///
/// <para><b>This type is the wire half only.</b> What it resolves <em>to</em> —
/// <see cref="Craticula.Core.Export.ExportColumn"/> and
/// <see cref="Craticula.Core.Export.ExportFormat"/> — lives in <c>Craticula.Core</c>, because those
/// two are the hand-off to the writer (EXP-4) and a writer must not have to reference a grid adapter
/// to read them (AR-3: never sideways between adapters). What stays here is what only a wire request
/// can mean: <see cref="Scope"/>, and the fact that these three members were spelled on an AG Grid
/// SSRM request at all.</para>
/// </summary>
public sealed record ExportSpec
{
    /// <summary>The requested <c>exportFormat</c>. Compilation-inert; see <see cref="ExportFormat"/>.</summary>
    public required ExportFormat Format { get; init; }

    /// <summary>The requested <c>exportScope</c>, defaulting to <see cref="ExportScope.All"/> when the
    /// wire omits it. This is the one export field that changes the compiled artifact, via
    /// <see cref="AgGridParsedRequest.Page"/>.</summary>
    public required ExportScope Scope { get; init; }

    /// <summary>EXP-1(b)'s resolved projection — never empty, never null. See the type's own remarks for
    /// why the default expansion happens here.</summary>
    public required IReadOnlyList<ExportColumn> Columns { get; init; }

    /// <summary>The <see cref="Columns"/> reduced to the colIds a compiler projects, in the same
    /// order. The compilers take this list (as <c>SqlQueryRequest.Projection</c> /
    /// <c>MongoQueryRequest.Projection</c>) rather than the whole spec, so neither of them has to
    /// reference this AG-Grid-specific type — the same layering that keeps
    /// <c>Craticula.Core</c> free of AG Grid's wire shapes.</summary>
    public IReadOnlyList<string> ProjectionColumns => [.. Columns.Select(column => column.ColId)];
}
