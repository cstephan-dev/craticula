namespace Craticula.Core.Ir;

/// <summary>
/// A <see cref="RangePage"/> violates an absolute shape invariant <c>ir.schema.json</c> states
/// (<c>startRow &gt;= 0</c>, <c>endRow &gt;= startRow</c>) — distinct from
/// <see cref="Craticula.Core.Schema.ResourceLimitExceededException"/>, which reports a
/// well-formed-but-too-large page, not a malformed one (a negative or inverted range has no
/// coherent "size" to compare against a limit in the first place).
/// </summary>
public sealed class PageSpecValidationException(string message) : Exception(message);
