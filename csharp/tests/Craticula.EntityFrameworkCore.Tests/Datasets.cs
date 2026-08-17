namespace Craticula.EntityFrameworkCore.Tests;

/// <summary>
/// The inline datasets <see cref="EfCoreExecutionEqualityTests"/>' cases are seeded with — one method per
/// case, keyed by LOGICAL colId, with a comment on every row saying why that row is there.
///
/// <para><b>Each dataset is built around its fixture's own filter values, not around plausible-looking
/// data.</b> A row on each side of every boundary, a null in every nullable column, and — wherever SEM-1
/// is in play — values differing only in case. That is what turns "both paths returned the same rows"
/// into evidence: a dataset all of whose rows match, or none of which does, would be satisfied by an
/// implementation that ignored the filter entirely, which is the failure mode this repo has paid for
/// before. The guards in that class hold every dataset here to those properties.</para>
///
/// <para>Written inline rather than as <c>dataset.json</c> files: that name is reserved for #1655's
/// execution tier, and a row whose purpose is "this is the one exactly ON the boundary" is only
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

    /// <summary>operators/026: <c>productName notContains 'gizmo'</c>, case-insensitive.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> ProductNames() =>
    [
        Row(("id", 1L), ("productName", "Gizmo Pro")),   // rejected only if the pattern folds case
        Row(("id", 2L), ("productName", "gizmo")),       // rejected: exact substring
        Row(("id", 3L), ("productName", "Widget")),      // returned
        Row(("id", 4L), ("productName", null)),          // returned — SEM-2's null-inclusive negation
        Row(("id", 5L), ("productName", "")),            // returned: the empty string contains nothing
        Row(("id", 6L), ("productName", "GIZMOS R US")), // rejected: unanchored, upper case
        Row(("id", 7L), ("productName", "100% gadget"))  // returned, and carries a literal % (see the escaping unit test)
    ];

    /// <summary>operators/029: <c>status blank</c> on a TEXT field — SEM-2's two-armed condition.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> BlankStatuses() =>
    [
        Row(("id", 1L), ("status", "active")), // rejected
        Row(("id", 2L), ("status", null)),     // returned: the NULL arm
        Row(("id", 3L), ("status", "")),       // returned: the empty-string arm, which non-text types do not have
        Row(("id", 4L), ("status", " ")),      // rejected: whitespace is a value; blank is not "trim then test"
        Row(("id", 5L), ("status", "ACTIVE"))  // rejected
    ];

    /// <summary>operators/035: <c>status in ['active', 'pending']</c>, case-insensitive.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> SetStatuses() =>
    [
        Row(("id", 1L), ("status", "active")),  // matches under any implementation
        Row(("id", 2L), ("status", "PENDING")), // matches only if the collation is on the COLUMN side
        Row(("id", 3L), ("status", "Active")),  // same, on the first list element
        Row(("id", 4L), ("status", "closed")),  // rejected: not in the list
        Row(("id", 5L), ("status", null)),      // rejected: `in` is not null-inclusive
        Row(("id", 6L), ("status", ""))         // rejected
    ];

    /// <summary>operators/039 and operators/042 — the SAME dataset under inclusive/inclusive and
    /// exclusive/exclusive bounds of 18..65, which is what makes the pair discriminating.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Ages() =>
    [
        Row(("id", 1L), ("age", 17L)),  // below the lower bound under either flag
        Row(("id", 2L), ("age", 18L)),  // ON the lower bound: in for /039, out for /042
        Row(("id", 3L), ("age", 19L)),  // strictly inside
        Row(("id", 4L), ("age", 64L)),  // strictly inside
        Row(("id", 5L), ("age", 65L)),  // ON the upper bound: in for /039, out for /042
        Row(("id", 6L), ("age", 66L)),  // above the upper bound under either flag
        Row(("id", 7L), ("age", null))  // rejected by both: a range comparison is not null-inclusive
    ];

    /// <summary>dialect-parity/023: <c>lastName equals 'smith'</c> (insensitive by default) AND
    /// <c>employeeCode equals 'AB-100'</c> (SC-3 <c>sensitive</c>) in one request.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> MixedCaseSensitivity() =>
    [
        Row(("id", 1L), ("lastName", "Smith"), ("employeeCode", "AB-100")),  // both arms match
        Row(("id", 2L), ("lastName", "SMITH"), ("employeeCode", "AB-100")),  // matches only if lastName folds case
        Row(("id", 3L), ("lastName", "smith"), ("employeeCode", "ab-100")),  // rejected ONLY if employeeCode does not
        Row(("id", 4L), ("lastName", "Smythe"), ("employeeCode", "AB-100")), // rejected on the name
        Row(("id", 5L), ("lastName", "Smith"), ("employeeCode", "AB-101")),  // rejected on the code
        Row(("id", 6L), ("lastName", null), ("employeeCode", "AB-100")),     // rejected: null name
        Row(("id", 7L), ("lastName", "Smith"), ("employeeCode", null))       // rejected: null code
    ];

    /// <summary>filter-tree/001: four columns ANDed —
    /// <c>customerName contains 'acme'</c>, <c>amount &gt; 100</c>, <c>status equals 'active'</c>,
    /// <c>isActive true</c>.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Orders() =>
    [
        Row(("id", 1L), ("amount", 200.0), ("customerName", "ACME Corp"), ("isActive", true), ("status", "active")),
        Row(("id", 2L), ("amount", 200.0), ("customerName", "acme"), ("isActive", true), ("status", "ACTIVE")),
        Row(("id", 3L), ("amount", 100.0), ("customerName", "Acme"), ("isActive", true), ("status", "active")),   // amount is ON 100: `>` rejects, `>=` would not
        Row(("id", 4L), ("amount", 200.0), ("customerName", "Beta"), ("isActive", true), ("status", "active")),   // rejected on the name
        Row(("id", 5L), ("amount", 200.0), ("customerName", "Acme"), ("isActive", false), ("status", "active")),  // rejected on AR-6's unary `true`
        Row(("id", 6L), ("amount", 200.0), ("customerName", "Acme"), ("isActive", true), ("status", "closed")),   // rejected on the status
        Row(("id", 7L), ("amount", null), ("customerName", "Acme"), ("isActive", true), ("status", "active")),    // rejected: NULL amount, three-valued `>`
        Row(("id", 8L), ("amount", 200.0), ("customerName", null), ("isActive", true), ("status", "active")),     // rejected: NULL name
        Row(("id", 9L), ("amount", 200.0), ("customerName", "Acme"), ("isActive", null), ("status", "active")),   // rejected: NULL boolean
        Row(("id", 10L), ("amount", 300.5), ("customerName", "Global Acme Ltd"), ("isActive", true), ("status", "active")), // `contains` is unanchored
        Row(("id", 11L), ("amount", 200.0), ("customerName", "Acme"), ("isActive", true), ("status", null))   // rejected: NULL status — eq is not null-inclusive, and this is the row that gives all four filtered columns a null
    ];

    /// <summary>filter-tree/010: <c>NOT (status equals 'archived')</c> — SEM-2's tree-level fan-out.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> ArchivedStatuses() =>
    [
        Row(("id", 1L), ("status", "archived")), // negated away
        Row(("id", 2L), ("status", "ARCHIVED")), // negated away ONLY if the negated comparison folds case
        Row(("id", 3L), ("status", "active")),   // returned
        Row(("id", 4L), ("status", null)),       // returned — the `OR status IS NULL` disjunct is the whole point
        Row(("id", 5L), ("status", ""))          // returned
    ];

    /// <summary>
    /// sort-paging/007: <c>department asc</c>, <c>age desc</c>, <c>hireDate asc</c>, plus SEM-6's
    /// tiebreaker. Two departments spelled in two cases each, two rows that tie on all three keys, and a
    /// null in each of the three sorted columns.
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
    /// page-export/008's seven-column <c>orders</c> schema, for the export tests. Not a parity case: the
    /// values are chosen for what they make the CSV WRITER do, so that "the adapter yields native CLR
    /// values" is asserted through bytes rather than through a type check.
    ///
    /// <para>Every cell here is a claim about the CLR type it arrives as: <c>19.99</c> keeps its scale
    /// only if it is a <see cref="decimal"/>, <c>2024-01-01</c> is ISO only if it is a
    /// <see cref="DateOnly"/>, and <c>=cmd</c> is prefixed while <c>-5.25</c> beside it is not — EXP-6's
    /// scope being decided by the value's type, which is #1652's narrowing and the one thing
    /// <c>CsvWriter</c> says an adapter can break without breaking the file's shape.</para>
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> ExportableOrders() =>
    [
        Row(("id", 1L), ("orderDate", new DateOnly(2024, 1, 1)), ("customerName", "Acme"),
            ("totalAmount", 19.99m), ("status", "shipped"), ("region", "EMEA"), ("notes", null)),
        Row(("id", 2L), ("orderDate", null), ("customerName", null),
            ("totalAmount", 100.55m), ("status", "pending"), ("region", null), ("notes", "second")),
        Row(("id", 3L), ("orderDate", new DateOnly(2024, 3, 1)), ("customerName", "Beta, Inc"), // the delimiter, inside a value
            ("totalAmount", null), ("status", "shipped"), ("region", "APAC"), ("notes", null)),
        Row(("id", 4L), ("orderDate", new DateOnly(2024, 4, 1)), ("customerName", "=cmd"),      // EXP-6: a string that must be prefixed
            ("totalAmount", -5.25m), ("status", null), ("region", "EMEA"), ("notes", null)),    // ...beside a negative decimal that must NOT be
        Row(("id", 5L), ("orderDate", new DateOnly(2024, 5, 2)), ("customerName", "Zed"),
            ("totalAmount", 0.75m), ("status", "shipped"), ("region", "EMEA"), ("notes", "ok"))
    ];

    /// <summary>sort-paging/009: <c>isActive desc</c> — SEM-3's null placement on a boolean key.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> ActiveFlags() =>
    [
        Row(("id", 1L), ("isActive", true)),
        Row(("id", 2L), ("isActive", false)),
        Row(("id", 3L), ("isActive", null)),  // DESC -> NULLS FIRST, so ids 3 and 6 lead
        Row(("id", 4L), ("isActive", true)),
        Row(("id", 5L), ("isActive", false)),
        Row(("id", 6L), ("isActive", null))
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
    /// order. 35 rows against a 20-row window also makes the last page SHORT (15 rows), which is where
    /// an inclusive-endRow reading shows up.</para>
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
