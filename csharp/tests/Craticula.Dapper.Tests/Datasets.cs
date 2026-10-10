namespace Craticula.Dapper.Tests;

/// <summary>
/// The inline datasets this suite's cases are seeded with — one method per case, keyed by LOGICAL
/// colId, with a comment on every row saying why that row is there. Own INTERNAL copy of
/// <c>Craticula.EntityFrameworkCore.Tests.Datasets</c>'s own discipline: a row on each side of every
/// boundary, values differing only in case wherever SEM-1 is in play, and a null in every nullable
/// column that a case's own filter/sort touches — what turns "the adapter returned the same rows as the
/// oracle" into evidence rather than a coincidence an implementation ignoring the filter would also
/// satisfy.
///
/// <para>Written inline rather than as <c>dataset.json</c> files: that format belongs to the
/// execution-equality tier (#1655), and a row whose purpose is "this is the one exactly ON the boundary" is only
/// self-explanatory next to the reasoning that chose it.</para>
/// </summary>
internal static class Datasets
{
    private static Dictionary<string, object?> Row(params (string Field, object? Value)[] cells)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in cells)
        {
            row[field] = value;
        }

        return row;
    }

    /// <summary>operators/001: <c>status equals 'active' OR status equals 'approved'</c> on a
    /// case-insensitive string.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> StatusAndId() =>
    [
        Row(("id", 1L), ("status", "active")),   // exact match
        Row(("id", 2L), ("status", "ACTIVE")),   // SEM-1: matches only if the comparison folds case
        Row(("id", 3L), ("status", "Approved")), // the OR's second arm, also case-folded
        Row(("id", 4L), ("status", "pending")),  // rejected: neither arm
        Row(("id", 5L), ("status", null)),       // rejected: eq is not null-inclusive (SEM-2)
        Row(("id", 6L), ("status", "")),         // rejected: '' is not 'active'
        Row(("id", 7L), ("status", "activex"))   // rejected: eq, not startsWith — the extra character matters
    ];

    /// <summary>filter-tree/001: four columns ANDed — <c>customerName contains 'acme'</c>,
    /// <c>amount &gt; 100</c>, <c>status equals 'active'</c>, <c>isActive true</c>.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Orders() =>
    [
        Row(("id", 1L), ("amount", 200.0), ("customerName", "ACME Corp"), ("isActive", true), ("status", "active")),
        Row(("id", 2L), ("amount", 100.0), ("customerName", "Acme"), ("isActive", true), ("status", "active")),   // amount is ON 100: `>` rejects, `>=` would not
        Row(("id", 3L), ("amount", 200.0), ("customerName", "Beta"), ("isActive", true), ("status", "active")),   // rejected on the name
        Row(("id", 4L), ("amount", 200.0), ("customerName", "Acme"), ("isActive", false), ("status", "active")),  // rejected on AR-6's unary `true`
        Row(("id", 5L), ("amount", 200.0), ("customerName", "Acme"), ("isActive", true), ("status", "closed")),   // rejected on the status
        Row(("id", 6L), ("amount", null), ("customerName", "Acme"), ("isActive", true), ("status", "active")),    // rejected: NULL amount, three-valued `>`
        Row(("id", 7L), ("amount", 200.0), ("customerName", null), ("isActive", true), ("status", "active")),     // rejected: NULL name
        Row(("id", 8L), ("amount", 200.0), ("customerName", "Acme"), ("isActive", null), ("status", "active")),   // rejected: NULL boolean
        Row(("id", 9L), ("amount", 300.5), ("customerName", "Global Acme Ltd"), ("isActive", true), ("status", "active")), // `contains` is unanchored
        Row(("id", 10L), ("amount", 200.0), ("customerName", "Acme"), ("isActive", true), ("status", null))   // rejected: NULL status — eq is not null-inclusive
    ];

    /// <summary>
    /// sort-paging/007: <c>department asc</c>, <c>age desc</c>, <c>hireDate asc</c>, plus SEM-6's
    /// tiebreaker. Two departments spelled in two cases each, two rows that tie on all three keys, and a
    /// null in each of the three sorted columns — the hardest ordering in the suite, exercised at chunk
    /// sizes from 1 upward against the export path.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Employees() =>
    [
        // 'sales' / 'Sales' / 'Engineering' / 'engineering': under NOCASE the two departments are two
        // groups (E before S); under BINARY all four spellings interleave differently, which is what
        // makes the collation observable in the ORDER rather than only in a WHERE clause.
        Row(("id", 1L), ("department", "sales"), ("age", 30L), ("hireDate", new DateOnly(2020, 1, 1))),
        Row(("id", 2L), ("department", "Sales"), ("age", 30L), ("hireDate", new DateOnly(2019, 1, 1))),
        Row(("id", 3L), ("department", "sales"), ("age", null), ("hireDate", new DateOnly(2021, 1, 1))),   // age NULL, and age is DESC -> NULLS FIRST within its department
        Row(("id", 4L), ("department", "Engineering"), ("age", 40L), ("hireDate", new DateOnly(2018, 1, 1))),
        Row(("id", 5L), ("department", null), ("age", 50L), ("hireDate", new DateOnly(2017, 1, 1))),       // department NULL -> ASC NULLS LAST -> last row overall
        Row(("id", 6L), ("department", "engineering"), ("age", 40L), ("hireDate", null)),                  // hireDate NULL -> ASC NULLS LAST within its bucket
        Row(("id", 7L), ("department", "Engineering"), ("age", 40L), ("hireDate", new DateOnly(2018, 1, 1))), // ties id 4 on all three keys -> tiebreaker
        Row(("id", 8L), ("department", "sales"), ("age", 30L), ("hireDate", new DateOnly(2020, 1, 1)))    // ties id 1 on all three keys -> tiebreaker
    ];

    /// <summary>
    /// The row IDs <see cref="Employees"/> resolves to under
    /// <c>department asc, age desc, hireDate asc, id asc</c> — hand-computed once, reused by every chunk
    /// size the export test theory covers.
    ///
    /// <para>Engineering (case-insensitive) sorts before sales: within it, 4/6/7 tie on department and
    /// age (40); hireDate ASC NULLS LAST puts 6 (null) last and orders 4 before 7 by hireDate tying and
    /// falling to the id tiebreaker — <c>4, 7, 6</c>. Within sales: age DESC NULLS FIRST puts 3 (null
    /// age) first; the remaining 1/2/8 tie on age (30), hireDate ASC orders 2 (2019) before the
    /// 2020-dated 1/8, which then split on the id tiebreaker — <c>3, 2, 1, 8</c>. The null department
    /// (5) sorts last of all under ASC NULLS LAST.</para>
    /// </summary>
    public static readonly long[] EmployeesOrderedIds = [4, 7, 6, 3, 2, 1, 8, 5];

    /// <summary>
    /// page-export/008's seven-column <c>orders</c> schema, for the export tests. Every cell here is a
    /// claim about the CLR type it must arrive as: <c>19.99</c> keeps its scale only if it is a
    /// <see cref="decimal"/>, <c>2024-01-01</c> is ISO only if it is a <see cref="DateOnly"/> — proving
    /// <see cref="DapperValueReader"/>'s materialization rather than merely proving row selection.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> ExportableOrders() =>
    [
        Row(("id", 1L), ("orderDate", new DateOnly(2024, 1, 1)), ("customerName", "Acme"),
            ("totalAmount", 19.99m), ("status", "shipped"), ("region", "EMEA"), ("notes", null)),
        Row(("id", 2L), ("orderDate", null), ("customerName", null),
            ("totalAmount", 100.55m), ("status", "pending"), ("region", null), ("notes", "second")),
        Row(("id", 3L), ("orderDate", new DateOnly(2024, 3, 1)), ("customerName", "Beta, Inc"),
            ("totalAmount", null), ("status", "shipped"), ("region", "APAC"), ("notes", null)),
        Row(("id", 4L), ("orderDate", new DateOnly(2024, 4, 1)), ("customerName", "Zed"),
            ("totalAmount", -5.25m), ("status", null), ("region", "EMEA"), ("notes", null)),
        Row(("id", 5L), ("orderDate", new DateOnly(2024, 5, 2)), ("customerName", "Yves"),
            ("totalAmount", 0.75m), ("status", "shipped"), ("region", "EMEA"), ("notes", "ok"))
    ];

    /// <summary>
    /// sort-paging/011: 35 rows for a <c>startRow 20 / endRow 40</c> window over
    /// <c>lastName asc</c> + tiebreaker.
    ///
    /// <para>The names are constructed so the sorted order is derivable rather than eyeballed:
    /// <c>Name01</c>…<c>Name15</c>, then <c>Name21</c>…<c>Name35</c>, then the five null names (ids
    /// 16–20, placed last by SEM-3). The CASE alternates on parity, so under SQLite's default BINARY
    /// collation every <c>NAME…</c> would sort before every <c>name…</c> and the page would hold an
    /// entirely different set of ids — the collation is what decides the page's contents, not just its
    /// order. 35 rows against a 20-row window also makes the last page SHORT (15 rows).</para>
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> PagedLastNames()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>(35);
        for (var id = 1; id <= 35; id++)
        {
            var name = id is >= 16 and <= 20
                ? null
                : id % 2 == 0 ? $"NAME{id:D2}" : $"name{id:D2}";

            rows.Add(Row(("id", (long)id), ("lastName", name)));
        }

        return rows;
    }
}
