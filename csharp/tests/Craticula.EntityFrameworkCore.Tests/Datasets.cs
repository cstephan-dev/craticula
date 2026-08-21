namespace Craticula.EntityFrameworkCore.Tests;

/// <summary>
/// The inline datasets two sibling harnesses' cases are seeded with — one method per case, keyed by
/// LOGICAL colId, with a comment on every row saying why that row is there.
/// <see cref="EfCoreExecutionEqualityTests"/> holds the ORIGINAL 12 ungrouped cases;
/// <see cref="EfCoreGroupedExecutionEqualityTests"/> (#1888) added 18 more for the grouped/leaf
/// population under <c>spec/fixtures/group-aggregation/</c>.
///
/// <para><b>Each dataset is built around its fixture's own filter values, not around plausible-looking
/// data</b> — a row on each side of every boundary, values differing only in case wherever SEM-1 is in
/// play, and (a guard <see cref="EfCoreExecutionEqualityTests"/>'s OWN cases hold to unconditionally,
/// via its <c>Every_Dataset_Has_A_Null_In_Every_Column_But_A_Unique_Non_Null_Stable_Key</c>) a null in
/// every nullable column. That is what turns "both paths returned the same rows" into evidence: a
/// dataset all of whose rows match, or none of which does, would be satisfied by an implementation that
/// ignored the filter entirely, which is the failure mode this repo has paid for before.</para>
///
/// <para><b>The grouped population's own discrimination requirement is narrower and per-case rather
/// than blanket-per-column</b> — a null in the AGGREGATED column specifically, an all-null group, a
/// single-row group, or a null group key, whichever a given case claims
/// (<c>EfCoreGroupedExecutionEqualityTests.DiscriminationProperty</c>), checked mechanically against
/// each case's own dataset rather than assumed from this comment. Not every grouped dataset carries a
/// null in every column — that would be the ungrouped harness's rule applied to a population it was
/// never written for.</para>
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

    // ---- #1888: group-aggregation/001-023 (the 18 grouped/leaf cases), for EfCoreGroupedExecutionEqualityTests ---
    //
    // Same discipline as above, adapted for a bucketed result: the "id" column here is the
    // group-aggregation schema's own STRING stableKey (never the leaf-row identifier these fixtures'
    // sibling categories use), and for a GROUPED level (everything but 005/006) it is not part of the
    // projection at all — GRP-3's output is [groupColumn, ...aggregates] — so a row's "id" only labels
    // the comment beside it, EXCEPT in 011, where a null id is the AGG-1 trap itself.

    /// <summary>group-aggregation/001: top-level group by region, sum(amount), no drill-down, no
    /// filter.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> GroupedRegionsTopLevel() =>
    [
        Row(("id", "1"), ("region", "EMEA"), ("amount", 100.00m)),   // EMEA row 1
        Row(("id", "2"), ("region", "EMEA"), ("amount", 200.00m)),   // EMEA row 2 -> EMEA sum = 300.00
        Row(("id", "3"), ("region", "APAC"), ("amount", 50.00m)),    // APAC: a SINGLE-ROW group
        Row(("id", "4"), ("region", null), ("amount", 75.00m)),      // a NULL group key -> its own bucket, must not be dropped
        Row(("id", "5"), ("region", "EMEA"), ("amount", null))       // a NULL in the aggregated column, inside a non-empty bucket: EMEA sum must stay 300.00
    ];

    /// <summary>group-aggregation/002: grouped at segment under the consumed key
    /// <c>region = 'EMEA'</c>, sum(amount).</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> SegmentsUnderEmeaExact() =>
    [
        Row(("id", "1"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", 100.00m)),
        Row(("id", "2"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", 50.00m)),   // Enterprise sum = 150.00
        Row(("id", "3"), ("region", "EMEA"), ("segment", "SMB"), ("amount", 30.00m)),          // SMB: a SINGLE-ROW group
        Row(("id", "4"), ("region", "emea"), ("segment", "Enterprise"), ("amount", 999.00m)),  // GRP-2: lowercase 'emea' must be EXCLUDED -- the drill-down match is exact, unlike an ordinary case-insensitive filter on the same field
        Row(("id", "5"), ("region", "APAC"), ("segment", "Enterprise"), ("amount", 500.00m))    // ordinary exclusion: wrong region entirely
    ];

    /// <summary>group-aggregation/003: grouped at product under the two consumed keys
    /// <c>region = 'EMEA' AND segment = 'Enterprise'</c>, sum(amount) -- SQL-2's "every composite
    /// wraps" needs a row that satisfies exactly one half of the AND to prove it is an AND, not an
    /// OR.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> ProductsUnderEmeaEnterprise() =>
    [
        Row(("id", "1"), ("region", "EMEA"), ("segment", "Enterprise"), ("product", "ProductA"), ("amount", 10.00m)),
        Row(("id", "2"), ("region", "EMEA"), ("segment", "Enterprise"), ("product", "ProductA"), ("amount", 5.00m)),   // ProductA sum = 15.00
        Row(("id", "3"), ("region", "EMEA"), ("segment", "Enterprise"), ("product", "ProductB"), ("amount", 20.00m)), // ProductB: a SINGLE-ROW group
        Row(("id", "4"), ("region", "EMEA"), ("segment", "SMB"), ("product", "ProductA"), ("amount", 999.00m)),        // region matches, segment doesn't -> EXCLUDED (proves the AND, not an OR of either half)
        Row(("id", "5"), ("region", "APAC"), ("segment", "Enterprise"), ("product", "ProductA"), ("amount", 888.00m))  // segment matches, region doesn't -> EXCLUDED (the other half of the same proof)
    ];

    /// <summary>group-aggregation/004: grouped at product under three consumed keys, the third
    /// (<c>fiscalYear = 2024</c>) an <c>int64</c> rather than a string -- the drill-down's typed
    /// equality must be numeric, not textual.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> ProductsUnderEmeaEnterprise2024() =>
    [
        Row(("id", "1"), ("region", "EMEA"), ("segment", "Enterprise"), ("fiscalYear", 2024L), ("product", "ProductA"), ("amount", 7.00m)),
        Row(("id", "2"), ("region", "EMEA"), ("segment", "Enterprise"), ("fiscalYear", 2023L), ("product", "ProductA"), ("amount", 999.00m)), // fiscalYear mismatch -> EXCLUDED
        Row(("id", "3"), ("region", "EMEA"), ("segment", "SMB"), ("fiscalYear", 2024L), ("product", "ProductA"), ("amount", 888.00m)),        // segment mismatch -> EXCLUDED
        Row(("id", "4"), ("region", "APAC"), ("segment", "Enterprise"), ("fiscalYear", 2024L), ("product", "ProductA"), ("amount", 777.00m)), // region mismatch -> EXCLUDED
        Row(("id", "5"), ("region", "EMEA"), ("segment", "Enterprise"), ("fiscalYear", 2024L), ("product", "ProductB"), ("amount", 3.00m))
    ];

    /// <summary>
    /// group-aggregation/005: the drill-down LEAF (both group columns consumed) under
    /// <c>region = 'EMEA' AND segment = 'Enterprise'</c>, full-field projection, sorted by
    /// <c>product asc</c> (SEM-1: an ORDINARY key here, so it collates) plus the appended <c>id</c>
    /// tiebreaker (SEM-6: exempt from SEM-1, renders bare). <c>Apple</c>/<c>APPLE</c> tie under the
    /// collation, which is what makes the tiebreaker load-bearing rather than decorative.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> DrillDownLeafTwoKeys() =>
    [
        Row(("id", "A1"), ("region", "EMEA"), ("segment", "Enterprise"), ("product", "banana"), ("fiscalYear", 2024L), ("amount", 10.00m), ("quantity", 1L)),
        Row(("id", "A2"), ("region", "EMEA"), ("segment", "Enterprise"), ("product", "Apple"), ("fiscalYear", 2024L), ("amount", 20.00m), ("quantity", 2L)),
        Row(("id", "A3"), ("region", "EMEA"), ("segment", "Enterprise"), ("product", "APPLE"), ("fiscalYear", 2023L), ("amount", null), ("quantity", null)), // ties A2 on product under SEM-1's collation; a null amount/quantity besides
        Row(("id", "A4"), ("region", "EMEA"), ("segment", "SMB"), ("product", "Cherry"), ("fiscalYear", 2024L), ("amount", 5.00m), ("quantity", 3L)),         // segment mismatch -> EXCLUDED
        Row(("id", "A5"), ("region", "APAC"), ("segment", "Enterprise"), ("product", "Date"), ("fiscalYear", 2024L), ("amount", 1.00m), ("quantity", 1L)),    // region mismatch -> EXCLUDED
        Row(("id", "A6"), ("region", "emea"), ("segment", "Enterprise"), ("product", "Egg"), ("fiscalYear", 2024L), ("amount", 2.00m), ("quantity", 1L))      // GRP-2: lowercase 'emea' -> EXCLUDED, exact drill-down match
    ];

    /// <summary>
    /// group-aggregation/006: the drill-down LEAF under a single consumed key
    /// (<c>region = 'EMEA'</c>), no <c>sortModel</c> at all -- the compiled order is the appended
    /// <c>id</c> tiebreaker alone, and SEM-6's supersession note is what this dataset pins: the
    /// tiebreaker is exempt from SEM-1 and renders BARE (SQLite's default BINARY collation), under
    /// which uppercase sorts before lowercase -- <c>ids</c> chosen so a case-INSENSITIVE ordering
    /// would produce a different sequence than the correct bare one.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> DrillDownLeafSingleKey() =>
    [
        Row(("id", "b2"), ("region", "EMEA"), ("segment", "X"), ("product", "P1"), ("fiscalYear", 2024L), ("amount", 1.00m), ("quantity", 1L)),
        Row(("id", "B1"), ("region", "EMEA"), ("segment", "Y"), ("product", "P2"), ("fiscalYear", 2024L), ("amount", 2.00m), ("quantity", 2L)),
        Row(("id", "a9"), ("region", "EMEA"), ("segment", "Z"), ("product", "P3"), ("fiscalYear", 2024L), ("amount", 3.00m), ("quantity", 3L)),
        Row(("id", "A10"), ("region", "APAC"), ("segment", "X"), ("product", "P1"), ("fiscalYear", 2024L), ("amount", 4.00m), ("quantity", 4L)), // region mismatch -> EXCLUDED
        Row(("id", "C1"), ("region", "emea"), ("segment", "X"), ("product", "P1"), ("fiscalYear", 2024L), ("amount", 5.00m), ("quantity", 5L))   // GRP-2: lowercase 'emea' -> EXCLUDED
    ];

    /// <summary>group-aggregation/007: grouped at segment under region='EMEA', <c>sum(amount)</c>
    /// (default alias). A null amount sits beside a non-null one in the SAME bucket, so SUM's
    /// standard null-skip (not COALESCE, which only matters for an ALL-null bucket) is what keeps
    /// SMB's sum at 5.00 rather than erroring or nulling out.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> SegmentSums() =>
    [
        Row(("id", "1"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", 10.00m)),
        Row(("id", "2"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", 20.00m)), // Enterprise sum = 30.00
        Row(("id", "3"), ("region", "EMEA"), ("segment", "SMB"), ("amount", 5.00m)),
        Row(("id", "4"), ("region", "EMEA"), ("segment", "SMB"), ("amount", null)),           // SMB sum stays 5.00 -- the null must be skipped, not summed as zero-that-breaks-arithmetic
        Row(("id", "5"), ("region", "APAC"), ("segment", "Enterprise"), ("amount", 999.00m))  // drill-down exclusion
    ];

    /// <summary>group-aggregation/008: grouped at segment under region='EMEA', <c>min(amount)</c>.
    /// The excluded APAC row (1.00) is numerically below every surviving value, so an implementation
    /// that dropped the drill-down would report the WRONG minimum, not merely an extra row.
    ///
    /// <para>Every value compared within one bucket is kept at the SAME digit-count (10.00/50.00, not
    /// e.g. 10.00/100.00) — SQLite's grouped MIN/MAX compares TEXT-affinity <c>amount</c> byte-wise
    /// rather than numerically, which can invert the answer across a digit-count boundary. See
    /// <see cref="SqliteTypeAffinityDivergenceTests.A_Grouped_Min_Max_Comparison_Disagrees_Because_Sqlite_Has_No_Decimal_Type"/>,
    /// which asserts that divergence directly; this case sidesteps it to stay about the drill-down
    /// exclusion it actually pins.</para>
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> SegmentMins() =>
    [
        Row(("id", "1"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", 50.00m)),
        Row(("id", "2"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", 10.00m)), // Enterprise min = 10.00
        Row(("id", "3"), ("region", "EMEA"), ("segment", "SMB"), ("amount", null)),
        Row(("id", "4"), ("region", "EMEA"), ("segment", "SMB"), ("amount", 30.00m)),         // SMB min = 30.00 -- MIN must skip the null, not treat it as the smallest value
        Row(("id", "5"), ("region", "APAC"), ("segment", "Enterprise"), ("amount", 1.00m))    // drill-down exclusion, and numerically the smallest value seeded
    ];

    /// <summary>group-aggregation/009: grouped at segment under region='EMEA', <c>max(amount)</c>.
    /// The excluded APAC row (999.00) is numerically above every surviving value, mirroring
    /// 008's min case in the other direction -- and, like 008, every compared value inside one bucket
    /// stays at one digit-count for the same reason (see 008's own comment and
    /// <see cref="SqliteTypeAffinityDivergenceTests.A_Grouped_Min_Max_Comparison_Disagrees_Because_Sqlite_Has_No_Decimal_Type"/>).</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> SegmentMaxes() =>
    [
        Row(("id", "1"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", 10.00m)),
        Row(("id", "2"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", 90.00m)), // Enterprise max = 90.00
        Row(("id", "3"), ("region", "EMEA"), ("segment", "SMB"), ("amount", null)),
        Row(("id", "4"), ("region", "EMEA"), ("segment", "SMB"), ("amount", 20.00m)),         // SMB max = 20.00 -- MAX must skip the null
        Row(("id", "5"), ("region", "APAC"), ("segment", "Enterprise"), ("amount", 999.00m))  // drill-down exclusion, and numerically the largest value seeded
    ];

    /// <summary>group-aggregation/010: grouped at segment under region='EMEA', <c>avg(amount)</c>.
    /// Enterprise's null must be excluded from BOTH the numerator and the denominator -- an
    /// implementation that averages over every row (treating null as zero) would report 13.33
    /// instead of the correct 20.00.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> SegmentAverages() =>
    [
        Row(("id", "1"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", 10.00m)),
        Row(("id", "2"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", 30.00m)),
        Row(("id", "3"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", null)),   // Enterprise avg = (10+30)/2 = 20.00, NOT (10+30+0)/3
        Row(("id", "4"), ("region", "EMEA"), ("segment", "SMB"), ("amount", 15.00m)),         // SMB: a SINGLE-ROW group, avg = 15.00
        Row(("id", "5"), ("region", "APAC"), ("segment", "Enterprise"), ("amount", 999.00m))  // drill-down exclusion
    ];

    /// <summary>
    /// group-aggregation/011: grouped at segment under region='EMEA', <c>count(id)</c> --
    /// AGG-1's own trap. Enterprise's bucket has TWO rows but only ONE non-null <c>id</c>: a bare
    /// row count (<c>g.Count()</c>/<c>{"$sum":1}</c>) would answer 2, and only the null-safe
    /// non-null-occurrence count answers the correct 1.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> SegmentIdCounts() =>
    [
        Row(("id", "e1"), ("region", "EMEA"), ("segment", "Enterprise")),
        Row(("id", null), ("region", "EMEA"), ("segment", "Enterprise")), // AGG-1's trap: Enterprise has 2 rows, but this id is null -> correct count is 1, not 2
        Row(("id", "e2"), ("region", "EMEA"), ("segment", "SMB")),
        Row(("id", "e3"), ("region", "EMEA"), ("segment", "SMB")),        // SMB: 2 rows, both ids non-null -> count = 2 (the non-trap control case)
        Row(("id", "a1"), ("region", "APAC"), ("segment", "Enterprise")) // drill-down exclusion -- would otherwise inflate Enterprise's count to 2
    ];

    /// <summary>group-aggregation/012: grouped at segment under region='EMEA', two aggregations on
    /// the SAME field (<c>sum</c>/<c>avg</c>, both explicitly aliased per AGG-2) -- the null in
    /// Enterprise's bucket must be skipped identically by both, independently computed.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> SegmentSumAndAverage() =>
    [
        Row(("id", "1"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", 10.00m)),
        Row(("id", "2"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", 30.00m)),
        Row(("id", "3"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", null)),   // Enterprise: sum = 40.00, avg = 20.00 -- both skip this null, independently
        Row(("id", "4"), ("region", "EMEA"), ("segment", "SMB"), ("amount", 25.00m)),         // SMB: a SINGLE-ROW group, sum = avg = 25.00
        Row(("id", "5"), ("region", "APAC"), ("segment", "Enterprise"), ("amount", 999.00m))  // drill-down exclusion
    ];

    /// <summary>
    /// group-aggregation/013: TOP-LEVEL group by region (no drill-down), three aggregations on the
    /// SAME field (<c>sum</c>/<c>min</c>/<c>max</c>, aliased per AGG-2) -- authored specifically to
    /// pin AGG-6 (#1886) through a REAL fixture: APAC's bucket has amount NULL in every row, so its
    /// sum must be 0.00 while its min/max are null, all three computed from the SAME all-null
    /// bucket.
    ///
    /// <para>EMEA's two values are deliberately the SAME digit-count (90.00/50.00, not e.g.
    /// 100.00/50.00): SQLite has no native decimal type, so a MIN/MAX over this TEXT-affinity column
    /// compares its stored values BYTE-WISE rather than numerically, and two values of DIFFERENT
    /// digit-counts can compare in the opposite order from their true numeric one (<c>"100.00" &lt;
    /// "50.00"</c> lexicographically, since <c>'1' &lt; '5'</c>, even though 100 &gt; 50) -- a real,
    /// separately-discovered SQLite/EF divergence for grouped MIN/MAX, out of scope for what THIS case
    /// is pinning (AGG-6). It is NOT skipped quietly: unlike its OWN sidestep here,
    /// <see cref="SqliteTypeAffinityDivergenceTests.A_Grouped_Min_Max_Comparison_Disagrees_Because_Sqlite_Has_No_Decimal_Type"/>
    /// executes it deliberately and asserts the disagreement, the same "assert, never avoid silently"
    /// discipline that file's other two cases already hold to.</para>
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> RegionSumMinMax() =>
    [
        Row(("id", "1"), ("region", "EMEA"), ("amount", 90.00m)),
        Row(("id", "2"), ("region", "EMEA"), ("amount", 50.00m)),   // EMEA: sum = 140.00, min = 50.00, max = 90.00
        Row(("id", "3"), ("region", "APAC"), ("amount", null)),
        Row(("id", "4"), ("region", "APAC"), ("amount", null))      // APAC: an ALL-NULL group -- AGG-6: sum -> 0.00, min/max -> null
    ];

    /// <summary>group-aggregation/014: TOP-LEVEL group by region (no drill-down), <c>sum(amount)</c>
    /// under an EXPLICIT alias (<c>"totalRevenue"</c>) -- the discriminator here is the OUTPUT NAME
    /// itself: an implementation that fell back to the compiler default (<c>amount_sum</c>) would be
    /// caught by the columns assertion, not the values.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> RegionTotalRevenueAlias() =>
    [
        Row(("id", "1"), ("region", "EMEA"), ("amount", 10.00m)),
        Row(("id", "2"), ("region", "EMEA"), ("amount", 20.00m)), // EMEA sum = 30.00
        Row(("id", "3"), ("region", "APAC"), ("amount", 5.00m)),
        Row(("id", "4"), ("region", "APAC"), ("amount", null))    // APAC sum stays 5.00 -- null skipped
    ];

    /// <summary>group-aggregation/015: grouped at segment under region='EMEA', <c>sum(amount)</c>
    /// with NO alias -- the discriminator is the compiler's DEFAULT output name
    /// (<c>amount_sum</c>), contrasted with 014's explicit one.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> SegmentDefaultAliasSum() =>
    [
        Row(("id", "1"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", 40.00m)),
        Row(("id", "2"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", null)), // Enterprise sum stays 40.00
        Row(("id", "3"), ("region", "EMEA"), ("segment", "SMB"), ("amount", 60.00m)),       // SMB: a SINGLE-ROW group
        Row(("id", "4"), ("region", "APAC"), ("segment", "Enterprise"), ("amount", 999.00m)) // drill-down exclusion
    ];

    /// <summary>
    /// group-aggregation/020: TOP-LEVEL group by region (no drill-down), a ROW-LEVEL filter
    /// (<c>amount &gt; 1000</c>, applied BEFORE aggregation) composed with a sort on the aggregate
    /// itself (<c>sum(amount) desc</c>). Three traps in one dataset: a row that fails the filter
    /// inside an otherwise-surviving bucket (id 2), a null amount that fails the filter under SEM-2's
    /// three-valued logic rather than being coalesced (id 6), and a region every one of whose rows
    /// fails the filter -- LATAM must not appear as a bucket AT ALL, which is what tells apart a
    /// pre-aggregation WHERE from a post-aggregation HAVING.
    ///
    /// <para>The two excluded amounts (0.50/0.99) are deliberately SUB-DOLLAR rather than merely
    /// "below 1000" (e.g. 500/999): the filter's bound value renders as the bare string
    /// <c>"1000"</c> (AR-7), and SQLite has no native decimal type, so the compiled statement compares
    /// it against the stored TEXT byte-wise -- under which <c>"500.00" &gt; "1000"</c> because
    /// <c>'5' &gt; '1'</c>, the identical lexicographic-vs-numeric mismatch
    /// <see cref="RegionSumMinMax"/>'s own comment records for MIN/MAX, here reachable through a
    /// comparison instead. A value starting with <c>'0'</c> sorts before <c>'1'</c> under BOTH
    /// readings, so it is excluded the same way on the SQL path and the EF path -- keeping this case
    /// about filter/sort COMPOSITION (its own stated purpose) rather than accidentally re-exercising
    /// <see cref="SqliteTypeAffinityDivergenceTests"/>' already-documented decimal-comparison
    /// divergence.</para>
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> RegionsFilteredAndSortedByTotal() =>
    [
        Row(("id", "1"), ("region", "EMEA"), ("amount", 2000.00m)),  // passes the filter
        Row(("id", "2"), ("region", "EMEA"), ("amount", 0.50m)),     // FAILS the filter -- must not reach EMEA's sum (which would become 2000.50 if it wrongly did)
        Row(("id", "3"), ("region", "APAC"), ("amount", 1500.00m)),
        Row(("id", "4"), ("region", "APAC"), ("amount", 1200.00m)),  // APAC sum = 2700.00
        Row(("id", "5"), ("region", "LATAM"), ("amount", 0.99m)),    // EVERY LATAM row fails the filter -- LATAM must not appear as a bucket at all
        Row(("id", "6"), ("region", "EMEA"), ("amount", null))       // FAILS the filter under SEM-2 (null > 1000 is unknown, not coalesced to a match)
    ];

    /// <summary>
    /// group-aggregation/021: grouped at segment under region='EMEA', sorted through AG Grid's
    /// coupled auto-column <c>sortModel</c> (AG-6) -- resolved and collapsed to a single key,
    /// <c>segment desc</c>. A null segment bucket exercises SEM-3's "nulls first" default for a
    /// descending key, ahead of every named value regardless of direction.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> SegmentsSortedByAutoColumn() =>
    [
        Row(("id", "1"), ("region", "EMEA"), ("segment", "Enterprise"), ("amount", 10.00m)),
        Row(("id", "2"), ("region", "EMEA"), ("segment", "SMB"), ("amount", 20.00m)),
        Row(("id", "3"), ("region", "EMEA"), ("segment", null), ("amount", 30.00m)),          // a NULL group key -> sorts FIRST under desc (SEM-3), ahead of every named segment
        Row(("id", "4"), ("region", "APAC"), ("segment", "Enterprise"), ("amount", 999.00m))   // drill-down exclusion
    ];

    /// <summary>
    /// group-aggregation/023: grouped at product under two consumed keys
    /// (<c>region = 'EMEA' AND segment = 'Enterprise'</c>) AND an active row filter
    /// (<c>fiscalYear &gt;= 2023 AND quantity &lt; 500</c>) -- GRP-5's composition of the two. Every
    /// row that fails exactly ONE of the three conditions (drill-down's segment, the filter's
    /// fiscalYear, the filter's quantity) is seeded, so dropping any single one of them silently
    /// would still be caught.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> ProductsUnderDrillDownWithRowFilter() =>
    [
        Row(("id", "1"), ("region", "EMEA"), ("segment", "Enterprise"), ("product", "ProductA"), ("fiscalYear", 2023L), ("quantity", 100L), ("amount", 10.00m)),
        Row(("id", "2"), ("region", "EMEA"), ("segment", "Enterprise"), ("product", "ProductA"), ("fiscalYear", 2022L), ("quantity", 100L), ("amount", 999.00m)), // fails the filter's fiscalYear clause -> EXCLUDED
        Row(("id", "3"), ("region", "EMEA"), ("segment", "Enterprise"), ("product", "ProductA"), ("fiscalYear", 2023L), ("quantity", 600L), ("amount", 888.00m)), // fails the filter's quantity clause -> EXCLUDED
        Row(("id", "4"), ("region", "EMEA"), ("segment", "SMB"), ("product", "ProductA"), ("fiscalYear", 2023L), ("quantity", 100L), ("amount", 777.00m)),         // fails the drill-down's segment -> EXCLUDED
        Row(("id", "5"), ("region", "EMEA"), ("segment", "Enterprise"), ("product", "ProductB"), ("fiscalYear", 2024L), ("quantity", 50L), ("amount", 20.00m)),   // ProductB: a SINGLE-ROW group
        Row(("id", "6"), ("region", "EMEA"), ("segment", "Enterprise"), ("product", "ProductA"), ("fiscalYear", 2023L), ("quantity", 499L), ("amount", 5.00m))    // ON the quantity boundary (499 < 500) -> ProductA sum = 10.00 + 5.00 = 15.00
    ];
}
