namespace Craticula.Core.Export;

/// <summary>
/// EXP-1: one column of an export, in the order it must be emitted — the colId a compiler projects
/// and the header text a writer puts in the header row.
/// </summary>
/// <param name="ColId">The logical field name, resolved against the
/// <see cref="Craticula.Core.Schema.GridSchema"/> exactly like any other colId — this is what the
/// compilers project.</param>
/// <param name="Header">The header text the CSV writer must put in the header row: the wire adapter's
/// own <c>header</c> override when it supplied one, else the schema's
/// <see cref="Craticula.Core.Schema.FieldConfig.Label"/>, else the <paramref name="ColId"/> itself
/// (EXP-1's "headers from schema labels"). <b>Never reaches a compiler</b> — no compiled artifact in
/// the suite contains a header string, which is exactly why <c>page-export/008</c>'s three custom
/// headers are invisible in all three of its arms.
///
/// <para><b>EXP-6's formula-injection hardening covers the header row too, not only the values.</b>
/// A wire-supplied <c>header</c> is arbitrary client text carried verbatim to the writer, so a header
/// beginning <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or CR must be prefixed with <c>'</c> exactly
/// as a cell value is — the row it lands in is a spreadsheet row like any other. Stated here because
/// #1650 is where the header became a first-class resolved value and #1651/#1652 read it.</para></param>
/// <remarks>
/// <b>Why this lives in <c>Craticula.Core</c> and not beside the wire adapter that resolves it.</b>
/// It is the hand-off type between EXP-1(b)'s resolution (a grid adapter's job) and EXP-4's writer (a
/// format's job), and the two must not be welded to each other: a second grid adapter has to be able
/// to build one without referencing AG Grid, and a second writer (`Export.Xlsx`, roadmap §14) has to
/// be able to consume one without inheriting a grid vocabulary into an independently published
/// package. AR-3's boundary rule is that dependencies point *into* the core and never sideways
/// between adapters, so the type both sides share belongs here, next to
/// <see cref="Craticula.Core.Schema.FieldConfig.Exportable"/> and
/// <see cref="Craticula.Core.Schema.FieldConfig.Label"/>, which are what it is resolved from.
/// The wire-shaped half of EXP-1 — which format was asked for, whether pagination is honored, how
/// <c>exportColumns</c> is spelled on the request — stays in the grid adapter.
/// </remarks>
public sealed record ExportColumn(string ColId, string Header);
