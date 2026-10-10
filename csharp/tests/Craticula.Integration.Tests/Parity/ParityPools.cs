namespace Craticula.Integration.Tests.Parity;

/// <summary>
/// The small value pools both the dataset and the request generator draw from, so a filter bound lands on, next to, or
/// away from stored values often enough to be discriminating. Every pool honours PAR-3's register:
/// strings are ASCII letters and digits only and never empty (SQL-7: Oracle's <c>''</c> is NULL; SEM-7/MDB-11: no pattern
/// metacharacters); doubles are multiples of 0.25 (exact in binary, so equality cannot depend on an engine's float
/// text); datetimes are UTC and millisecond-aligned (XEQ-2's cap; SEM-4's SQLite fixed-width obligation).
/// </summary>
internal static class ParityPools
{
    /// <summary>Duplicates differing only by case (SEM-1: the field is case-insensitive) and a digit suffix for variety.</summary>
    public static readonly string[] Labels =
    [
        "alpha", "Alpha", "ALPHA", "bravo", "Bravo", "charlie", "delta7", "Delta7",
        "echo", "foxtrot", "Golf", "hotel", "india", "juliet"
    ];

    public static readonly long[] Quantities = [-3, 0, 1, 2, 5, 7, 10, 15, 42, 100];

    public static readonly double[] Scores = [-2.0, -0.5, 0.0, 0.25, 0.5, 1.0, 1.75, 2.5, 3.0, 4.25, 6.0];

    public static readonly decimal[] Prices = [-4.50m, 0.00m, 1.50m, 2.25m, 9.99m, 10.00m, 19.99m, 49.95m, 100.00m];

    public static readonly DateOnly[] Dates =
    [
        new(1990, 5, 12), new(1991, 7, 4), new(1999, 12, 31), new(2000, 1, 1), new(2000, 2, 29), new(2010, 10, 10)
    ];

    /// <summary>Whole seconds and the millisecond either side of a boundary (the SQLite text-compare trap of SEM-4).</summary>
    public static readonly DateTime[] Instants =
    [
        Utc(2025, 12, 31, 23, 59, 59, 999),
        Utc(2026, 1, 1, 0, 0, 0, 0),
        Utc(2026, 1, 1, 0, 0, 0, 1),
        Utc(2026, 1, 1, 12, 0, 0, 0),
        Utc(2026, 1, 2, 0, 0, 0, 0),
        Utc(2026, 1, 2, 0, 0, 0, 999),
        Utc(2026, 2, 1, 0, 0, 0, 0),
        Utc(2026, 6, 15, 8, 30, 45, 250)
    ];

    private static DateTime Utc(int y, int mo, int d, int h, int mi, int s, int ms) =>
        new(y, mo, d, h, mi, s, ms, DateTimeKind.Utc);
}
