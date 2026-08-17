using Craticula.Core.Export;

namespace Craticula.Export.Csv;

/// <summary>
/// EXP-4/EXP-5's writer configuration: everything about the emitted file that the grid's wire request
/// does NOT carry. The wire contract (EXP-1) exposes no delimiter, no BOM toggle and no header-row
/// toggle — <c>page-export/013</c> and <c>/015</c> say so in as many words — so these are host
/// configuration, chosen once where the export endpoint is wired up.
///
/// <para>The one member that <em>is</em> derived from the request is the delimiter, via
/// <see cref="ForFormat"/>: <c>exportFormat: "tsv"</c> is what turns
/// <see cref="ExportFormat.Tsv"/> into a tab. That is the whole of the format field's effect on
/// anything (<c>page-export/012</c> and <c>/025</c> pin that it reaches no compiler), and it is a
/// default rather than a lock — a host may still override the delimiter afterwards.</para>
/// </summary>
public sealed record CsvWriterOptions
{
    /// <summary>EXP-4's default: comma, header row on, BOM on, plus EXP-6's hardening on. The nine
    /// fixtures that configure nothing (<c>page-export/012</c>, <c>/014</c>, <c>/016</c>-<c>/020</c>,
    /// <c>/022</c> and <c>/025</c>) are byte-compared against exactly this instance, which is what makes
    /// "the default" an asserted fact rather than a sentence in a doc comment.</summary>
    public static CsvWriterOptions Default { get; } = new();

    /// <summary>
    /// EXP-4's <b>first-class <c>;</c> preset</b>, verbatim: "Excel in French and most European
    /// locales expects <c>;</c> and misparses <c>,</c>". Named rather than left to each host to spell
    /// <c>Delimiter = ';'</c> because the requirement asks for a preset, and because it is the
    /// configuration EXP-5's collision guard exists for — a host that reaches for this one is
    /// precisely the host that may also want <see cref="DecimalSeparator"/> <c>,</c>, which is legal
    /// here (the delimiter is no longer <c>,</c>) and rejected under the default.
    ///
    /// <para>It changes the delimiter and <b>nothing else</b>: no decimal separator, no date pattern.
    /// EXP-4 makes the preset a delimiter preset, and EXP-5 keeps number/date formatting on the
    /// invariant culture until a per-field override says otherwise. Pairing the two here would make
    /// <c>page-export/013</c>'s expected bytes depend on a coupling the TRD does not state.
    /// <c>page-export/026</c> is where an override does say otherwise: this same preset, over a schema
    /// declaring <c>,</c> on the one decimal column and <c>dd/MM/yyyy</c> on the date one — the whole
    /// European locale, assembled from the two places EXP-4 and EXP-5 each put their half.</para>
    /// </summary>
    public static CsvWriterOptions SemicolonPreset { get; } = new() { Delimiter = ';' };

    /// <summary>The field separator. Comma by default (EXP-4); <c>;</c> via
    /// <see cref="SemicolonPreset"/>; tab for <see cref="ExportFormat.Tsv"/> via
    /// <see cref="ForFormat"/>. Any other single character is allowed, subject to
    /// <see cref="Validate"/>.</summary>
    public char Delimiter { get; init; } = ',';

    /// <summary>EXP-4's "header row on by default". When off, the first record emitted is the first
    /// data row and the file carries no column names at all.</summary>
    public bool IncludeHeaderRow { get; init; } = true;

    /// <summary>EXP-4's "Optional UTF-8 BOM, default <b>on</b> (Excel misreads UTF-8 without it)".
    /// The three bytes <c>EF BB BF</c> are written before the header row and before anything else;
    /// <c>page-export/014</c> and <c>/015</c> are the same rows with this on and off, and their two
    /// expected files differ by exactly those three bytes.</summary>
    public bool WriteByteOrderMark { get; init; } = true;

    /// <summary>
    /// EXP-5's decimal separator, defaulting to the invariant culture's <c>.</c>. Applied to the
    /// fractional part of every real number the writer formats (see
    /// <see cref="CsvValueFormatter"/>), never to an integral one — swapping a separator that is not
    /// there is what would make a thousands separator appear, and this writer emits none.
    ///
    /// <para>EXP-5 also allows a <em>per-field</em> override in schema config, and since #1653 the
    /// schema carries one (<see cref="Craticula.Core.Schema.FormatOptions.DecimalSeparator"/>, reaching
    /// the writer on <see cref="ExportColumn.Format"/>). This member is therefore the <b>file-wide
    /// default a field overrides</b>, not the only separator there is — which is why
    /// <see cref="Validate(IReadOnlyList{ExportColumn})"/> exists beside <see cref="Validate()"/>: the
    /// collision this requirement names is a property of a delimiter and <em>whichever</em> separator
    /// applies to a cell.</para>
    /// </summary>
    public char DecimalSeparator { get; init; } = '.';

    /// <summary>
    /// EXP-6's "default on, configurable off". When on, a field whose first character is <c>=</c>,
    /// <c>+</c>, <c>-</c>, <c>@</c>, tab or CR is prefixed with <c>'</c> before RFC 4180 quoting — see
    /// <see cref="FormulaInjectionHardening"/> for what the rule protects against, and
    /// <see cref="CsvWriter"/> for the fields it applies to (client-supplied text and headers, never a
    /// compiler-formatted number or date).
    ///
    /// <para><b>#1651 shipped the writer without this member on purpose, and the reason is worth
    /// keeping:</b> the hardening did not exist yet, so an option defaulting to <see langword="true"/>
    /// would have promised a protection nothing performed. An option that lies is worse than an absent
    /// one — a host reading the surface would have concluded the export was hardened and stopped
    /// looking. #1652 adds the behaviour and the toggle in the same change, which is the only ordering
    /// in which the default is honest.</para>
    ///
    /// <para>Off is a legitimate configuration, not a foot-gun: an export consumed by a parser rather
    /// than by a spreadsheet wants the raw value, and the prefix is not reversible (see
    /// <see cref="FormulaInjectionHardening"/>). It is off by <em>choice</em> and on by default, which
    /// is the direction EXP-6 specifies. No conformance fixture turns it off — the six hostile-value
    /// cases (<c>page-export/016</c>-<c>020</c>, <c>/022</c>) carry no <c>writerConfig.json</c> at all,
    /// so they grade the default; the disabled path is unit-tested, exactly like the legal <c>;</c>+
    /// <c>,</c> pairing EXP-5 allows.</para>
    /// </summary>
    public bool HardenFormulaInjection { get; init; } = true;

    /// <summary>
    /// The delimiter <see cref="ExportFormat"/> asks for, as a starting point a host may still refine
    /// (e.g. a French host exporting CSV takes <see cref="SemicolonPreset"/> instead of
    /// <see cref="Default"/>).
    ///
    /// <para><b>This method is the ONLY place <c>exportFormat</c> becomes a delimiter, and an adapter
    /// wiring an export endpoint (#1622) must go through it.</b> The writer takes a resolved
    /// <see cref="ExportColumn"/> list and these options — it is never handed the parsed request, so
    /// it cannot apply the format on the caller's behalf and will not notice that the caller forgot.
    /// A host that passes <see cref="Default"/> for a <c>"tsv"</c> request gets a comma-delimited file
    /// under a <c>.tsv</c> name, and every conformance fixture still passes, because the mistake is
    /// outside this module. Start from <c>ForFormat(export.Format)</c> and refine with <c>with</c>;
    /// never start from <see cref="Default"/> unless the format is known to be
    /// <see cref="ExportFormat.Csv"/>.</para>
    ///
    /// <para><b>Craticula's TSV is RFC 4180 with a tab delimiter</b>, not IANA
    /// <c>text/tab-separated-values</c> — that media type has no quoting mechanism at all and simply
    /// forbids a tab inside a field, which would force this writer either to reject rows or to
    /// silently mangle them. EXP-4 asks for one rule set ("RFC 4180: ... quoting/escaping of
    /// delimiter, quote, and newline characters") over a <em>configurable</em> delimiter, so a field
    /// containing a tab is quoted under TSV exactly as a field containing a comma is under CSV.
    /// <c>page-export/025</c> is the byte oracle for that choice.</para>
    /// </summary>
    public static CsvWriterOptions ForFormat(ExportFormat format) => format switch
    {
        ExportFormat.Csv => Default,
        ExportFormat.Tsv => Default with { Delimiter = '\t' },
        _ => throw new CsvWriterException($"Unsupported export format '{format}'.")
    };

    /// <summary>
    /// EXP-5's collision guard for the <b>file-wide</b> separator, plus the two structural
    /// impossibilities that would make the output unparseable by anything. Public because a host wiring
    /// an export endpoint should be able to reject a bad configuration at startup rather than on the
    /// first user's download.
    ///
    /// <para><b><see cref="Validate(IReadOnlyList{ExportColumn})"/> is the overload
    /// <see cref="CsvWriter"/> calls</b>, because since #1653 a field can override the separator and
    /// that override collides in exactly the same way. This one remains for a host validating its
    /// options alone; it is a strict subset of the other.</para>
    /// </summary>
    /// <exception cref="CsvWriterException">The configuration cannot produce an unambiguous file.</exception>
    public void Validate()
    {
        // EXP-5, verbatim: "The writer MUST warn/fail if the decimal separator equals the delimiter
        // (the classic `;`-locale collision)." The failure it prevents is not a malformed file but a
        // well-formed WRONG one, and precisely: this writer's own RFC 4180 quoting sees the separator
        // inside the number and emits "1234,56" as a QUOTED field, so the row does not shift for a
        // reader that honours quotes — the number simply arrives as quoted text a spreadsheet will not
        // sum, in the one column an export exists to carry a quantity in. A reader that splits on the
        // delimiter without honouring quotes does shift, from that cell to the end of the row. Neither
        // consumer sees a malformed file; both get a wrong one, which is why this fails rather than
        // warning. See CsvWriterException for warn-vs-fail.
        if (DecimalSeparator == Delimiter)
        {
            throw new CsvWriterException(
                $"EXP-5: the decimal separator and the delimiter are both '{Describe(Delimiter)}'. " +
                "Every fractional number would be emitted as a quoted string where a number belongs, " +
                "and would split its row in any reader that ignores the quotes. " +
                "Use CsvWriterOptions.SemicolonPreset (or any other delimiter) with a ',' decimal " +
                "separator, or keep the invariant '.' separator with a ',' delimiter.");
        }

        // Not EXP-5's rule, but the same class of guarantee and equally silent when violated: a
        // delimiter that IS the quote character (or a line ending) makes every quoted field
        // ambiguous, so no reader could recover the original values. Rejected up front rather than
        // producing a file whose corruption only shows up in the consumer.
        if (Delimiter is '"' or '\r' or '\n')
        {
            throw new CsvWriterException(
                $"Delimiter '{Describe(Delimiter)}' is one of the characters RFC 4180 quoting is " +
                "built on ('\"', CR, LF); no reader could tell a delimiter from an escape.");
        }

        if (DecimalSeparator is '"' or '\r' or '\n')
        {
            throw new CsvWriterException(
                $"Decimal separator '{Describe(DecimalSeparator)}' is one of the characters RFC 4180 " +
                "quoting is built on ('\"', CR, LF).");
        }
    }

    /// <summary>
    /// EXP-5's collision guard over <b>every separator that can apply to a cell</b>: this
    /// configuration's own, and each column's <see cref="Craticula.Core.Schema.FormatOptions.DecimalSeparator"/>
    /// override. Called by <see cref="CsvWriter"/> before the first byte is written and before the row
    /// stream is enumerated, so a rejected export costs nothing and leaves nothing behind.
    ///
    /// <para><b>Why the per-field half lives here and not in the schema or in the writer's body.</b>
    /// EXP-5 says the writer MUST warn/fail on the collision; it does not say "only for the default",
    /// and a field-declared <c>,</c> under a <c>,</c> delimiter is the identical defect — a well-formed
    /// WRONG file, which is the whole reason that guard exists. It cannot be checked where the override
    /// is declared: a <c>GridSchema</c> does not know which delimiter (or which
    /// <see cref="ExportFormat"/>) a host will export with, and the same schema is legal under <c>;</c>
    /// and illegal under <c>,</c>. It belongs on this type rather than inside
    /// <see cref="CsvWriter.Write"/> because the two halves are one rule and a reader should meet them
    /// together — and because a per-field format comes from host-declared schema config, so a host
    /// holding a schema can run this at startup exactly as it can run <see cref="Validate()"/>, which
    /// is what that method's existence is an argument for.</para>
    /// </summary>
    /// <param name="columns">EXP-1(b)'s resolved projection. An empty list is not this method's
    /// business — <see cref="CsvWriter"/> refuses it for EXP-1(b)'s reason, not EXP-5's.</param>
    /// <exception cref="CsvWriterException">The configuration, or one column's override, cannot produce
    /// an unambiguous file.</exception>
    public void Validate(IReadOnlyList<ExportColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        Validate();

        foreach (var column in columns)
        {
            if (column.Format?.DecimalSeparator is not { } separator)
            {
                continue;
            }

            // The field is named, not just the character: a host reading "the decimal separator and
            // the delimiter are both ','" over a schema with twenty fields has no way to know which
            // declaration to fix, and the whole point of failing before the first byte is that the
            // message is the only thing the host gets.
            if (separator == Delimiter)
            {
                throw new CsvWriterException(
                    $"EXP-5: field '{column.ColId}' declares a decimal separator " +
                    $"'{Describe(separator)}', which is also the delimiter. Every fractional number in " +
                    "that column would be emitted as a quoted string where a number belongs, and would " +
                    "split its row in any reader that ignores the quotes. Change the field's " +
                    "format.decimalSeparator in the GridSchema, or export with another delimiter.");
            }

            if (separator is '"' or '\r' or '\n')
            {
                throw new CsvWriterException(
                    $"EXP-5: field '{column.ColId}' declares a decimal separator " +
                    $"'{Describe(separator)}', one of the characters RFC 4180 quoting is built on " +
                    "('\"', CR, LF).");
            }
        }
    }

    private static string Describe(char character) => character switch
    {
        '\t' => "\\t",
        '\r' => "\\r",
        '\n' => "\\n",
        _ => character.ToString()
    };
}
