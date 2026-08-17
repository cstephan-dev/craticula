using Craticula.Core.Schema;
using Craticula.Sql;

namespace Craticula.EntityFrameworkCore.Tests;

/// <summary>
/// <b>AR-4's proof.</b> "The EF Core execution adapter MAY compile the IR directly to expression trees
/// … <b>both paths MUST pass the same fixtures</b>" — and an <see cref="IQueryable{T}"/> produces no
/// artifact string, so that requirement can only ever be discharged at the level of ROWS. For each case
/// below, one SQLite in-memory database is seeded, the fixture's IR is compiled BOTH ways, both
/// statements are executed against those same rows, and the results are compared.
///
/// <para><b>Why the SQL side is compiled on the fly instead of read off disk.</b> Only 42
/// <c>sqlite.artifact.json</c> files exist (under <c>group-aggregation</c> and <c>dialect-parity</c>),
/// and almost none is in this story's IR slice — so reading them would have limited the harness to
/// whichever cases happen to have a SQLite arm. Compiling each fixture's IR with the real
/// <see cref="SqlQueryBuilder"/> and <see cref="SqliteDialect"/> removes that ceiling and makes a
/// stronger claim besides: two <em>compilers</em>, one input, both executed, rows compared. The artifact
/// tier already proves that compiler emits the text its fixtures pin
/// (<c>Craticula.Sql.Tests</c>); this suite proves the other path agrees with it about rows.</para>
///
/// <para><b>Each case carries a hand-computed expected row sequence, and that is not redundant with the
/// parity assertion — it is what stops the whole suite from being a tautology.</b> Two paths that both
/// dropped every NULL row, or both ignored a collation, would agree perfectly. The datasets are
/// therefore built to straddle each case's own boundaries (a row on each side of every comparison, a
/// null in every nullable column, values that differ only in case), and
/// <see cref="Every_Case_Discriminates_Rather_Than_Matching_Everything_Or_Nothing"/> holds the datasets
/// to that. If the two paths ever agree with each other and disagree with the expected sequence, the
/// answer is to investigate — never to adjust either compiler to fit, and never to edit the expectation.
/// #1644's lesson was a compiler bent to fit a bad artifact.</para>
///
/// <para><b>Two shapes are deliberately absent, both because SQLite has no native type for them</b>
/// (<c>decimal</c> and <c>datetime</c> comparisons/orderings). They are not skipped quietly: the two
/// paths genuinely diverge there, on this engine only, and
/// <see cref="SqliteTypeAffinityDivergenceTests"/> executes both and asserts the divergence together
/// with its cause.</para>
/// </summary>
public class EfCoreExecutionEqualityTests
{
    /// <summary>
    /// One case: a real fixture, an inline dataset built for it, the row order both paths must return,
    /// and what a wrong implementation would return instead.
    /// </summary>
    /// <param name="Discriminates">Not decoration. It is the reason the case is in the table, and it is
    /// asserted on: the expected sequence is what a CORRECT implementation returns, and this sentence
    /// names the row(s) that a specific plausible mistake would add or drop. A case that cannot answer it
    /// is a case that proves nothing.</param>
    private sealed record EqualityCase(
        string Category,
        string Prefix,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Dataset,
        IReadOnlyList<long> ExpectedIds,
        string Discriminates);

    private static readonly Dictionary<string, EqualityCase> Cases = new(StringComparer.Ordinal)
    {
        ["operators/001"] = new(
            "operators", "001",
            Datasets.StatusAndId(),
            [1, 2, 3],
            "SEM-1's default case-INSENSITIVITY on a string eq, and that eq is not a prefix match. Without the " +
            "collation only id 1 ('active') matches; as a startsWith, id 7 ('activex') would join."),

        ["operators/026"] = new(
            "operators", "026",
            Datasets.ProductNames(),
            [3, 4, 5, 7],
            "SEM-2's null-inclusive notContains — id 4 is NULL and MUST be returned; three-valued logic drops it " +
            "unless the disjunct is spelled. Also SEM-1 on the pattern family: case-sensitively, ids 1 and 6 " +
            "('Gizmo Pro', 'GIZMOS R US') would be returned too."),

        ["operators/029"] = new(
            "operators", "029",
            Datasets.BlankStatuses(),
            [2, 3],
            "SEM-2's blank on a TEXT field is null OR empty string: an adapter testing only NULL returns id 2 " +
            "alone, and one that trims whitespace adds id 4 (a single space, which is not blank)."),

        ["operators/035"] = new(
            "operators", "035",
            Datasets.SetStatuses(),
            [1, 2, 3],
            "SEM-1's collation on SET MEMBERSHIP, on the side that works: SQLite reads `x IN (…)` from x's own " +
            "collating sequence and silently ignores one attached to a list element, so a value-side collation " +
            "returns id 1 alone. `in` is also NOT null-inclusive (SEM-2), so id 5 stays out."),

        ["operators/039"] = new(
            "operators", "039",
            Datasets.Ages(),
            [2, 3, 4, 5],
            "SEM-4's per-boundary inclusivity, inclusive/inclusive: ids 2 and 5 are exactly ON the boundaries " +
            "(18 and 65) and a `>`/`<` rendering drops both. Compare operators/042, whose expectation is the " +
            "same dataset with exactly those two removed."),

        ["operators/042"] = new(
            "operators", "042",
            Datasets.Ages(),
            [3, 4],
            "The mirror of operators/039 on the identical dataset — exclusive/exclusive. Together the two pin " +
            "that each boundary reads its OWN flag; one flag applied to both ends passes one case and fails the " +
            "other."),

        ["dialect-parity/023"] = new(
            "dialect-parity", "023",
            Datasets.MixedCaseSensitivity(),
            [1, 2],
            "SEM-1's three-valued mode inside ONE request: lastName is insensitive by default and employeeCode " +
            "is SC-3 'sensitive'. Collapsing the two to 'insensitive' adds id 3 ('ab-100'); collapsing them to " +
            "'no mechanism' drops id 2 ('SMITH')."),

        ["filter-tree/001"] = new(
            "filter-tree", "001",
            Datasets.Orders(),
            [1, 2, 10],
            "AR-5's four-way implicit AND across four types at once: `contains` anchoring (id 10 matches " +
            "mid-string), a strict `>` on a double (id 3 is exactly 100), AR-6's unary boolean shorthand " +
            "(ids 5 and 9), and a null in every one of the four columns."),

        ["filter-tree/010"] = new(
            "filter-tree", "010",
            Datasets.ArchivedStatuses(),
            [3, 4, 5],
            "SEM-2's TREE-LEVEL not fan-out — the one shape SemanticsResolver has no method for. id 4 is NULL " +
            "and must be returned; without the `OR status IS NULL` disjunct the negation drops it. The " +
            "collation still applies inside the negation, or id 2 ('ARCHIVED') would be returned."),

        ["sort-paging/007"] = new(
            "sort-paging", "007",
            Datasets.Employees(),
            [4, 7, 6, 3, 2, 1, 8, 5],
            "Three sort keys with three different null placements plus SEM-6's appended tiebreaker. The " +
            "collated first key is decisive: under SQLite's default BINARY collation 'Engineering' < 'Sales' < " +
            "'engineering' < 'sales', which interleaves the two departments and produces a completely different " +
            "sequence. Ties on all three keys (ids 4/7 and 1/8) are what the tiebreaker resolves."),

        ["sort-paging/009"] = new(
            "sort-paging", "009",
            Datasets.ActiveFlags(),
            [3, 6, 1, 4, 2, 5],
            "SEM-3's null placement on a BOOLEAN key sorted DESC — nulls FIRST. The emulated null bucket must " +
            "sort descending here; ascending it would put ids 3 and 6 last. It is also the ordering the keyset " +
            "walk has to reproduce for a boolean key, which has no `>` operator at all."),

        ["sort-paging/011"] = new(
            "sort-paging", "011",
            Datasets.PagedLastNames(),
            [26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 16, 17, 18, 19, 20],
            "AG-3's row window over 35 rows: startRow 20, endRow 40 EXCLUSIVE, so Skip(20).Take(20) and a SHORT " +
            "final page of 15. An inclusive endRow would take 21 rows; passing endRow as the limit would take " +
            "40. The page straddles the NULLS LAST boundary, so it contains both named and null rows, and the " +
            "case-insensitive collation decides which ids are in it at all.")
    };

    public static TheoryData<string> CaseNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Cases.Keys.OrderBy(name => name, StringComparer.Ordinal))
            {
                data.Add(name);
            }

            return data;
        }
    }

    // ---- the AR-4 assertion -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Both_Paths_Return_The_Same_Rows_From_The_Same_Database(string caseName)
    {
        var testCase = Cases[caseName];
        var fixture = LoadFixture(testCase);

        using var database = SqliteFixtureDatabase.Create(fixture.Schema, testCase.Dataset);

        // Path 1: the SQL compiler, executed as text + parameters — what a Dapper adapter runs.
        var compiled = SqlQueryBuilder.Build(fixture.Sql, SqliteDialect.Instance);
        var sqlRows = database.ExecuteCompiled(compiled);

        // Path 2: this adapter, composed onto the same table's IQueryable and executed by EF.
        var page = await EfCoreGridQuery.FetchPageAsync(
            database.Rows(), fixture.Ef, EfCoreProviderSemantics.Sqlite);

        Assert.Equal(fixture.Schema.FieldOrder, page.Columns);

        // Against the independent expectation FIRST: if this fails, the two paths agreeing with each
        // other says nothing, and the failure names which side of the boundary moved.
        var stableKey = fixture.Schema.FieldOrder.ToList().IndexOf(fixture.Schema.StableKey);
        Assert.Equal(testCase.ExpectedIds, Ids(page.Rows, stableKey));
        Assert.Equal(testCase.ExpectedIds, Ids(sqlRows, stableKey));

        // ...then the parity assertion itself: same rows, same order, same values, same CLR types.
        AssertRowsEqual(sqlRows, page.Rows, fixture.Schema, testCase);
    }

    /// <summary>
    /// #1653's obligation on an execution adapter, asserted on the side that carries it: every cell this
    /// adapter yields is the native CLR type the schema declares, never a driver wrapper. It is the
    /// discriminator <c>CsvWriter</c>'s EXP-5 formatting and EXP-6 hardening both dispatch on, and the
    /// writer explicitly cannot check it for itself.
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Every_Cell_Is_The_Native_Clr_Type_The_Schema_Declares(string caseName)
    {
        var testCase = Cases[caseName];
        var fixture = LoadFixture(testCase);

        using var database = SqliteFixtureDatabase.Create(fixture.Schema, testCase.Dataset);
        var page = await EfCoreGridQuery.FetchPageAsync(
            database.Rows(), fixture.Ef, EfCoreProviderSemantics.Sqlite);

        foreach (var row in page.Rows)
        {
            for (var index = 0; index < page.Columns.Count; index++)
            {
                if (row[index] is not { } cell)
                {
                    continue;
                }

                var declared = Nullable.GetUnderlyingType(
                    EfCoreQueryBuilder.ClrTypeFor(fixture.Schema.Fields[page.Columns[index]].Type))
                    ?? EfCoreQueryBuilder.ClrTypeFor(fixture.Schema.Fields[page.Columns[index]].Type);

                Assert.Equal(declared, cell.GetType());
            }
        }
    }

    // ---- guards on the cases themselves ------------------------------------------------------------

    /// <summary>
    /// The anti-tautology guard, and the most important test in this file. A dataset every row of which
    /// matches (or none of which does) makes its case pass under an implementation that ignores the
    /// filter entirely — which is this repo's recurring defect in oracle design. So: every case must
    /// return at least one row, and a case whose fixture actually carries a filter must return FEWER
    /// rows than were seeded.
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Every_Case_Discriminates_Rather_Than_Matching_Everything_Or_Nothing(string caseName)
    {
        var testCase = Cases[caseName];
        var fixture = LoadFixture(testCase);

        Assert.NotEmpty(testCase.ExpectedIds);
        Assert.NotEmpty(testCase.Discriminates);

        if (fixture.Ef.Filter is not null)
        {
            Assert.True(
                testCase.ExpectedIds.Count < testCase.Dataset.Count,
                $"{fixture.Name} filters, so its dataset must contain rows the filter REJECTS — otherwise an " +
                $"implementation that dropped the WHERE clause would pass. Seeded {testCase.Dataset.Count}, " +
                $"expected {testCase.ExpectedIds.Count}.");
        }
        else
        {
            // A sort-only case returns everything the window admits; what makes it discriminate is the
            // ORDER, so the expectation must not be the insertion order.
            Assert.NotEqual(
                testCase.Dataset.Select(row => Convert.ToInt64(row[fixture.Schema.StableKey])).ToList(),
                testCase.ExpectedIds);
        }
    }

    /// <summary>
    /// SEM-2 is only exercised by data that contains nulls, and it is the rule most likely to be got
    /// wrong in both paths at once (three-valued logic drops rows quietly). Every nullable column of every
    /// case's dataset therefore carries at least one null — and the stable key never does, because SEM-6's
    /// tiebreaker and EXP-3's keyset walk both rest on it being present and unique.
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Every_Dataset_Has_A_Null_In_Every_Column_But_A_Unique_Non_Null_Stable_Key(string caseName)
    {
        var testCase = Cases[caseName];
        var fixture = LoadFixture(testCase);
        var stableKey = fixture.Schema.StableKey;

        var keys = new List<object?>();
        foreach (var row in testCase.Dataset)
        {
            Assert.True(
                row.TryGetValue(stableKey, out var key) && key is not null,
                $"{fixture.Name}: every seeded row must carry the stable key '{stableKey}'.");
            keys.Add(key);
        }

        Assert.Equal(keys.Count, keys.Distinct().Count());

        foreach (var field in fixture.Schema.FieldOrder)
        {
            if (field == stableKey)
            {
                continue;
            }

            Assert.Contains(testCase.Dataset, row => !row.TryGetValue(field, out var value) || value is null);
        }
    }

    /// <summary>
    /// Every case's fixture is a real directory carrying a real Postgres artifact — so a case cannot
    /// silently stop referring to anything after a rename, and the prefix lookup cannot start matching a
    /// different fixture than the one whose dataset was designed. The artifact is not compared here (that
    /// is <c>Craticula.Sql.Tests</c>' job); its presence is what makes the fixture a compiled one at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Every_Case_Names_A_Fixture_That_Exists_And_Carries_A_Compiled_Artifact(string caseName)
    {
        var testCase = Cases[caseName];
        var directory = FixtureRequest.ResolveDirectory(testCase.Category, testCase.Prefix);

        Assert.True(File.Exists(
            RepoPaths.Fixture(testCase.Category, directory, "expected", "postgres.artifact.json")));
    }

    [Fact]
    public void The_Harness_Covers_Twelve_Cases_Across_Four_Categories()
    {
        // Stated so that deleting a case is a decision rather than an omission. The categories are the
        // four that hold this story's IR slice: operators (per-operator semantics), filter-tree (AR-5's
        // tree shapes), sort-paging (AR-9/SEM-3/SEM-6/AG-3) and dialect-parity (SEM-1's three-valued
        // mode). date-presets is deliberately absent — its cases resolve to ordinary inRange comparisons
        // over `date`/`datetime`, and the datetime half cannot be compared on SQLite at all
        // (SqliteTypeAffinityDivergenceTests).
        Assert.Equal(12, Cases.Count);
        Assert.Equal(
            ["dialect-parity", "filter-tree", "operators", "sort-paging"],
            Cases.Values.Select(value => value.Category).Distinct().OrderBy(name => name, StringComparer.Ordinal));
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static FixtureRequest LoadFixture(EqualityCase testCase) =>
        FixtureRequest.Load(testCase.Category, FixtureRequest.ResolveDirectory(testCase.Category, testCase.Prefix));

    private static IReadOnlyList<long> Ids(IReadOnlyList<IReadOnlyList<object?>> rows, int stableKeyIndex) =>
        [.. rows.Select(row => Convert.ToInt64(row[stableKeyIndex]))];

    private static void AssertRowsEqual(
        IReadOnlyList<IReadOnlyList<object?>> sql,
        IReadOnlyList<IReadOnlyList<object?>> ef,
        GridSchema schema,
        EqualityCase testCase)
    {
        Assert.Equal(sql.Count, ef.Count);

        for (var row = 0; row < sql.Count; row++)
        {
            for (var column = 0; column < schema.FieldOrder.Count; column++)
            {
                var field = schema.FieldOrder[column];

                Assert.True(
                    Equals(sql[row][column], ef[row][column]),
                    $"Row {row}, column '{field}': the SQL path returned " +
                    $"{Describe(sql[row][column])} and the EF path returned {Describe(ef[row][column])}. " +
                    $"This case exists to catch: {testCase.Discriminates}");
            }
        }
    }

    private static string Describe(object? value) =>
        value is null ? "<null>" : $"{value} ({value.GetType().Name})";
}
