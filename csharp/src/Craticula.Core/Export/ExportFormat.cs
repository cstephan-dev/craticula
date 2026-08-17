namespace Craticula.Core.Export;

/// <summary>
/// EXP-1: the two delimiter-separated formats the export contract admits. The choice <b>never reaches
/// a compiler</b> — <c>page-export/012</c> and <c>/025</c> are the same request but for this field and
/// compile to a byte-identical artifact — it is carried through to the CSV/TSV writer (EXP-4), which
/// is the one place that turns it into a delimiter.
///
/// <para>Here rather than beside a grid adapter for the same reason as <see cref="ExportColumn"/>: it
/// is what a wire adapter hands a writer, and neither of those two packages may depend on the other
/// (AR-3).</para>
/// </summary>
public enum ExportFormat
{
    Csv,
    Tsv
}
