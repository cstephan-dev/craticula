namespace Craticula.EntityFrameworkCore;

/// <summary>
/// SEM-4's result side for this adapter (Bug #2798). EF Core materialises a cell from the host's model, so the kind of a
/// <see cref="DateTime"/> is whatever the provider gave it: unspecified for SQL Server's <c>datetime2</c>, MySQL's
/// <c>DATETIME</c> and SQLite's text, local or UTC elsewhere. The adapter has no reader to fix that, so it applies the
/// spec's table to each cell of a page or an export row after the query ran, and a host needs no UTC value converter
/// for the way out. The comparison side stays the host's obligation (the values stored are UTC readings).
/// <list type="bullet">
/// <item><description>a UTC kind: unchanged</description></item>
/// <item><description>a local kind (a driver converted a zoned value): the same instant in UTC</description></item>
/// <item><description>an unspecified kind (no zone): the wall-clock reading as UTC</description></item>
/// <item><description>a <see cref="DateTimeOffset"/>: the same instant at offset zero</description></item>
/// </list>
/// Everything else, including <see cref="DateOnly"/> (a local date has no zone), passes through untouched.
/// </summary>
internal static class EfCoreValueNormalizer
{
    public static object? Normalize(object? cell) => cell switch
    {
        DateTime { Kind: DateTimeKind.Utc } => cell,
        DateTime { Kind: DateTimeKind.Local } value => value.ToUniversalTime(),
        DateTime value => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        DateTimeOffset value => value.ToUniversalTime(),
        _ => cell
    };

    /// <summary>Normalises the first <paramref name="count"/> cells in place; the cells after them (an export's keyset keys) are the
    /// database's own values and feed the next chunk's predicate, so they stay as they were read.</summary>
    public static void NormalizeInPlace(object?[] cells, int count)
    {
        for (var index = 0; index < count; index++)
        {
            cells[index] = Normalize(cells[index]);
        }
    }
}
