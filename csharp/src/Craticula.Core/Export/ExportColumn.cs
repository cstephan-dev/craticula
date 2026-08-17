using Craticula.Core.Schema;

namespace Craticula.Core.Export;

/// <summary>
/// EXP-1: one column of an export, in the order it must be emitted — the colId a compiler projects,
/// the header text a writer puts in the header row, and EXP-5's per-field format overrides the writer
/// renders its cells with.
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
/// beginning <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or CR must be prefixed with <c>'</c> — the row
/// it lands in is a spreadsheet row like any other. It applies <b>unconditionally</b>: #1652 narrowed
/// EXP-6's scope for cell <em>values</em>, sparing the ones a writer formatted from a number or a date,
/// and a header is never one of those — it is client text by construction, so there is nothing for the
/// narrowing to exclude here. Stated because #1650 is where the header became a first-class resolved
/// value and #1651/#1652 read it; what the cell rule is belongs to the writer and to the TRD's
/// EXP-6.</para></param>
/// <param name="Format">EXP-5's per-field overrides for this column — the field's own
/// <see cref="FieldConfig.Format"/>, carried through unchanged. <see langword="null"/> (the default)
/// means the writer's invariant-culture rendering, so no existing construction site moves and a column
/// that declares nothing is byte-identical to what #1651/#1652 emitted.
///
/// <para><b>Why the override travels on the column rather than reaching the writer some other way.</b>
/// The writer is handed a column list and a <c>CsvWriterOptions</c> and nothing else — deliberately, so
/// that it holds no grid vocabulary and cannot re-derive a decision the adapter already made (see
/// <c>CsvWriter</c>'s remarks) — so a <em>per-field</em> rule has no other channel to it. #1652
/// deferred this exact contract question, having no need for it then and declining to add a
/// <c>FieldType</c> here "for a hypothetical"; EXP-5's second half is what forces it, and this is the
/// narrower member: not the schema's declared type, which would let the schema win over the actual
/// value, but only the rendering the host asked for. What it is <b>not</b> is a merge — a
/// <see cref="Header"/> combines a wire override with the schema's
/// <see cref="FieldConfig.Label"/>, whereas EXP-5 puts a format in schema config alone, so the
/// resolved value here <em>is</em> the schema's record.</para>
///
/// <para><b>It reaches no compiler either</b>, for <see cref="Header"/>'s reason:
/// <c>page-export/026</c> is <c>013</c>'s request, row stream and writer config with these two
/// overrides added to its schema, and its compiled arm is byte-identical to <c>013</c>'s while its
/// bytes are not.</para></param>
/// <remarks>
/// <b>Why this lives in <c>Craticula.Core</c> and not beside the wire adapter that resolves it.</b>
/// It is the hand-off type between EXP-1(b)'s resolution (a grid adapter's job) and EXP-4's writer (a
/// format's job), and the two must not be welded to each other: a second grid adapter has to be able
/// to build one without referencing AG Grid, and a second writer (`Export.Xlsx`, roadmap §14) has to
/// be able to consume one without inheriting a grid vocabulary into an independently published
/// package. AR-3's boundary rule is that dependencies point *into* the core and never sideways
/// between adapters, so the type both sides share belongs here, next to
/// <see cref="Craticula.Core.Schema.FieldConfig.Exportable"/>,
/// <see cref="Craticula.Core.Schema.FieldConfig.Label"/> and
/// <see cref="Craticula.Core.Schema.FieldConfig.Format"/>, which are what it is resolved from.
/// The wire-shaped half of EXP-1 — which format was asked for, whether pagination is honored, how
/// <c>exportColumns</c> is spelled on the request — stays in the grid adapter.
///
/// <para>That same reasoning is what settles the type of <see cref="Format"/>: it reuses the schema's
/// own <see cref="FormatOptions"/> rather than a parallel copy in this namespace. A copy would have to
/// be kept in step with a record that lives four files away and carries no compiler behaviour to make a
/// divergence visible — the drift shape this repo has paid for repeatedly — while the namespace
/// boundary it would buy is one this type already crosses in the sentence above, both sides of it
/// inside <c>Craticula.Core</c>. A writer consuming <see cref="Format"/> therefore reads exactly what
/// the host declared.</para>
/// </remarks>
public sealed record ExportColumn(string ColId, string Header, FormatOptions? Format = null);
