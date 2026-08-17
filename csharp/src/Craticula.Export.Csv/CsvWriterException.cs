namespace Craticula.Export.Csv;

/// <summary>
/// An export the CSV writer refuses to produce: a writer configuration that cannot yield an
/// unambiguously parseable file (EXP-5's decimal-separator/delimiter collision, a delimiter that is
/// itself a quoting character), or a row that does not match the
/// <see cref="Craticula.Core.Export.ExportColumn"/> list it is being written against.
///
/// <para><b>Why this exists rather than a warning.</b> EXP-5 says the writer "MUST warn/fail". Both
/// are allowed by the requirement; this module fails, for the same reason every other refusal in
/// this repo is an exception rather than a log line: the writer has no logging abstraction (AR-3
/// keeps it dependency-free), a warning would be emitted into a stream nobody is reading while a
/// syntactically valid but semantically wrong file is handed to the user, and the failure mode it
/// guards is silent data corruption — <c>1234,56</c> written into a <c>,</c>-delimited file is not a
/// malformed file, it is a file that parses cleanly into the wrong number of columns. A host that
/// genuinely wants a warning can catch this and continue with a corrected
/// <see cref="CsvWriterOptions"/>; a host that gets a warning it did not read cannot recover the
/// file.</para>
///
/// <para>Every collision-class check runs in <see cref="CsvWriterOptions.Validate"/>, i.e. BEFORE a
/// single byte is written and before the row stream is touched, so a rejected configuration never
/// leaves a half-written file behind.</para>
/// </summary>
public sealed class CsvWriterException : Exception
{
    public CsvWriterException(string message) : base(message)
    {
    }

    public CsvWriterException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
