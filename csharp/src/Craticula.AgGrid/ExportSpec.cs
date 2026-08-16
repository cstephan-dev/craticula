namespace Craticula.AgGrid;

/// <summary>EXP-1: the two delimiter-separated formats the wire contract admits. The choice never
/// reaches a compiler — <c>page-export/004</c> and <c>/005</c> exist precisely to pin that the same
/// request compiles identically under either — it is carried through for the CSV writer
/// (EXP-4, #1651/#1652), which is what turns it into a delimiter.</summary>
public enum ExportFormat
{
    Csv,
    Tsv
}

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
/// EXP-1: one column of an export, in the order it must be emitted.
/// </summary>
/// <param name="ColId">The logical field name, resolved against the <see cref="Craticula.Core.Schema.GridSchema"/>
/// exactly like any other colId — this is what the compilers project.</param>
/// <param name="Header">The header text the CSV writer must put in the header row: the wire's own
/// <c>header</c> override when it supplied one, else the schema's
/// <see cref="Craticula.Core.Schema.FieldConfig.Label"/>, else the <paramref name="ColId"/> itself
/// (EXP-1's "headers from schema labels"). <b>Never reaches a compiler</b> — no compiled artifact in
/// the suite contains a header string, which is exactly why <c>page-export/008</c>'s three custom
/// headers are invisible in all three of its arms.
///
/// <para><b>EXP-6's formula-injection hardening covers the header row too, not only the values.</b>
/// A wire-supplied <c>header</c> is arbitrary client text carried verbatim to the writer, so a header
/// beginning <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or CR must be prefixed with <c>'</c> exactly
/// as a cell value is — the row it lands in is a spreadsheet row like any other. Stated here because
/// #1650 is where the header became a first-class resolved value and #1651/#1652 will read it.</para></param>
public sealed record ExportColumn(string ColId, string Header);

/// <summary>
/// EXP-1's wire-contract extension, already resolved: what a request means when it carries
/// <c>exportFormat</c>/<c>exportScope</c>/<c>exportColumns</c> beside the ordinary AG Grid SSRM
/// fields. Built by <see cref="AgGridRequestParser.Parse"/>; <see langword="null"/> on
/// <see cref="AgGridParsedRequest.Export"/> means the request is an ordinary row fetch.
///
/// <para><b>Resolved, not echoed.</b> <see cref="Columns"/> is always the final, ordered list the
/// export emits — the wire's own <c>exportColumns</c> when it supplied one, otherwise every field the
/// schema declares <see cref="Craticula.Core.Schema.FieldConfig.Exportable"/>, in declaration order
/// (EXP-1(b)). Resolving it here rather than in each compiler is what keeps the SQL and Mongo
/// projections from being able to disagree about the default, and is why
/// <see cref="AgGridParseOptions.Schema"/> is required for an export request.</para>
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
