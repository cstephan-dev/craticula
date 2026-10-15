using System.Globalization;

namespace Craticula.Core.Schema;

/// <summary>
/// EXP-5: per-field format overrides — "invariant culture by default; per-field overrides in schema
/// config (date pattern, decimal separator)". Exactly those two members and no more: the requirement
/// names two, and a third (a thousands separator, a boolean spelling, a culture name) would be this
/// repo inventing a contract every language port then has to reproduce.
///
/// <para><b>Host-declared, never client-influenced</b>, like the <see cref="PhysicalMapping"/> and
/// <see cref="TextOptions"/> beside it: no AG Grid request carries a format, and EXP-5 puts the
/// override in <em>schema config</em>. That is also why nothing merges here — an
/// <see cref="Craticula.Core.Export.ExportColumn"/>'s header combines a wire override with the
/// schema's <see cref="FieldConfig.Label"/>, but a format has only one source, so the resolved value
/// on that type <em>is</em> this record.</para>
///
/// <para><b>It reaches no compiler.</b> A format changes how the CSV/TSV writer renders a value
/// (EXP-4/EXP-5) and nothing about the query that produced it, exactly like
/// <see cref="FieldConfig.Label"/>: <c>spec/fixtures/page-export/026</c> is <c>013</c>'s inputs with
/// these two overrides added to its schema, and its compiled artifact is byte-identical to
/// <c>013</c>'s.</para>
/// </summary>
public sealed record FormatOptions
{
    /// <summary>
    /// EXP-5's date pattern, replacing the writer's ISO 8601 default for this field only. Only
    /// meaningful when <see cref="FieldConfig.Type"/> is <see cref="FieldType.Date"/> or
    /// <see cref="FieldType.Datetime"/>. Absent means the invariant ISO rendering
    /// (<c>CsvValueFormatter</c>'s own constants).
    ///
    /// <para><b>Vocabulary.</b> The pattern is written in the LDML/CLDR field letters every platform's
    /// formatter shares — <c>yyyy</c>, <c>MM</c>, <c>dd</c>, <c>HH</c>, <c>mm</c>, <c>ss</c> and
    /// literal separators — so <c>"dd/MM/yyyy"</c> means the same thing to .NET, Java and a
    /// JavaScript port. A pattern reaching outside that shared subset is accepted by whichever
    /// platform understands it and is <b>not</b> portable; a conformance fixture must therefore stay
    /// inside it, which <c>page-export/026</c>'s <c>dd/MM/yyyy</c> does.</para>
    ///
    /// <para><b>Known limit: <c>z</c> reads state the writer otherwise never consults.</b> Applied to a
    /// value carrying no offset — a <see cref="DateOnly"/>, or a <see cref="DateTime"/> whose
    /// <see cref="DateTimeKind"/> is <see cref="DateTimeKind.Unspecified"/>, the ordinary shape of a SQL
    /// <c>timestamp</c> — <c>z</c>/<c>zz</c>/<c>zzz</c> substitutes <b>the formatting machine's own
    /// offset</b>, so the same rows exported from a developer's laptop and from a UTC CI box differ in
    /// bytes.</para>
    ///
    /// <para><b><c>K</c> is not the same hazard, and an earlier draft of this clause said it was.</b>
    /// Measured, invariant culture: on an <see cref="DateTimeKind.Unspecified"/> value <c>K</c> renders
    /// the <b>empty string</b> — machine-independently — and it yields an offset only for a
    /// <see cref="DateTimeKind.Local"/> value (<see cref="DateTimeKind.Utc"/> gives <c>Z</c>). Naming
    /// both together misdirects a language port in both directions at once: it would guard the
    /// specifier that is safe on the case named, and leave open the one that is not.</para>
    ///
    /// <para>Neither is rejected. Whether a value carries an offset is known only from its runtime type,
    /// which a schema cannot see, and no probe can settle it — the probe's own answer depends on the
    /// validating machine's zone, so a guard built that way passes locally and fails on CI. A purely
    /// <em>syntactic</em> refusal of an unescaped <c>z</c> would be machine-independent and is a
    /// defensible narrowing, given the portable-subset rule above already excludes it; it is not applied
    /// because a column an adapter yields as a <see cref="DateTimeOffset"/> renders an offset
    /// deterministically, and that is a legitimate use this record cannot distinguish from the hazardous
    /// one. A conformance fixture must stay inside the portable subset regardless, which is the rule that
    /// actually protects the suite.</para>
    /// </summary>
    public string? DatePattern { get; init; }

    /// <summary>
    /// EXP-5's decimal separator for this field, overriding the writer-wide default (<c>.</c>, the
    /// invariant culture's). Only meaningful when <see cref="FieldConfig.Type"/> is
    /// <see cref="FieldType.Decimal"/> or <see cref="FieldType.Double"/> — an integral value has no
    /// fractional part to separate.
    ///
    /// <para>A separator equal to the writer's active delimiter is <b>EXP-5's collision</b>, and it is
    /// the same defect whether it was declared here or writer-wide: the export is refused before its
    /// first byte, by the writer, which is the only party that knows the delimiter. A schema cannot
    /// check it — it does not know which delimiter (or which format) the host will export with.</para>
    /// </summary>
    public char? DecimalSeparator { get; init; }

    /// <summary>
    /// A <see cref="DatePattern"/> that can actually format a temporal value, checked by
    /// <see cref="GridSchema.Parse"/> and not in this record's constructor, for
    /// <see cref="SqlMapping.IsValid"/>'s reason: System.Text.Json populates init-accessors after
    /// construction.
    ///
    /// <para><b>Why a probe rather than a syntax check.</b> An unusable pattern (<c>"q"</c>) throws
    /// <see cref="FormatException"/> the first time a date cell is rendered — <em>mid-stream</em>, after
    /// the header row and an arbitrary number of data rows have already reached the client, which is the
    /// one failure mode a streaming writer cannot recover from. Formatting a probe value is the cheapest
    /// statement of "this pattern works" that cannot disagree with what the writer will do, since it
    /// calls the same formatter.</para>
    ///
    /// <para><b>Why the probe is exactly sufficient.</b> Two types, and they are the only two the
    /// writer ever applies a pattern to: a <see cref="DateOnly"/> is lifted to midnight first (a
    /// <c>datetime</c> field's pattern may name time specifiers, and <see cref="DateOnly"/> throws on
    /// those), and a <see cref="TimeOnly"/> is never given a <em>date</em> pattern at all. So a pattern
    /// accepted here cannot throw later. An empty or whitespace-only pattern is rejected although it
    /// formats cleanly: <c>""</c> is .NET's alias for the general format (<c>01/05/2026 14:30:45</c>),
    /// so accepting it would silently substitute a shape nobody asked for.</para>
    ///
    /// <para><b>Why this is <see langword="public"/> where <see cref="SqlMapping.IsValid"/> is
    /// internal, which is a deliberate divergence.</b> That one has exactly one caller,
    /// <see cref="GridSchema.Parse"/>, because a physical mapping reaches a compiler only through a
    /// parsed schema. A <see cref="FormatOptions"/> does not: it travels on an
    /// <see cref="Craticula.Core.Export.ExportColumn"/>, which is a public record an execution adapter
    /// or a host may <b>hand-build</b> without any schema having been parsed — and #1622's adapters are
    /// expected to. Left internal, the CSV writer (a different assembly) could not ask this question,
    /// and the mid-stream <see cref="FormatException"/> described above was reachable in exactly that
    /// case: measured, the writer emitted the BOM and the whole header row before throwing. So the
    /// second caller is real, and the pre-flight guard that needs it is
    /// <c>CsvWriterOptions.Validate(IReadOnlyList&lt;ExportColumn&gt;)</c>.</para>
    /// </summary>
    public bool IsValid => DatePattern is null || IsUsableDatePattern(DatePattern);

    /// <summary>An arbitrary, fixed probe instant — the value is irrelevant, only whether the pattern
    /// survives being applied to it.</summary>
    private static readonly DateTime ProbeTimestamp = new(2026, 1, 5, 14, 30, 45, DateTimeKind.Unspecified);

    private static bool IsUsableDatePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        try
        {
            ProbeTimestamp.ToString(pattern, CultureInfo.InvariantCulture);
            new DateTimeOffset(ProbeTimestamp, TimeSpan.Zero).ToString(pattern, CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
