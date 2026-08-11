namespace Craticula.Core.Ir;

/// <summary>
/// Structural validity of a <see cref="PageSpec"/> — independent of any configured
/// <see cref="Craticula.Core.Schema.ResourceLimits"/>, so any public entry point that mints a
/// <see cref="PageSpec"/> can call this without needing limits/config context. Only a
/// <see cref="RangePage"/> has a shape to check (<see cref="NonePage"/> is trivially valid).
/// </summary>
public static class PageSpecValidator
{
    /// <summary>
    /// Throws <see cref="PageSpecValidationException"/> when <paramref name="page"/> is a
    /// <see cref="RangePage"/> with a negative <see cref="RangePage.StartRow"/> or an
    /// <see cref="RangePage.EndRow"/> before <see cref="RangePage.StartRow"/> — a shape that was
    /// never a coherent size to begin with (<c>ir.schema.json</c> requires <c>startRow &gt;= 0</c>
    /// and <c>endRow &gt;= startRow</c>).
    /// </summary>
    public static void Validate(PageSpec page)
    {
        if (page is RangePage rangePage && (rangePage.StartRow < 0 || rangePage.EndRow < rangePage.StartRow))
        {
            throw new PageSpecValidationException(
                $"pageSpec.range is malformed: startRow={rangePage.StartRow}, endRow={rangePage.EndRow} " +
                "(ir.schema.json requires startRow >= 0 and endRow >= startRow).");
        }
    }
}
