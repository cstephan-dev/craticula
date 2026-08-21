using System.Globalization;
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.Sql;

namespace Craticula.EntityFrameworkCore.Tests;

/// <summary>
/// #1888: <see cref="EfCoreExecutionEqualityTests"/>'s AR-4 claim, extended to all 18 fixtures under
/// <c>spec/fixtures/group-aggregation/</c> that a grouped/leaf query can reach --
/// <c>001</c>-<c>015</c>, <c>020</c>, <c>021</c>, <c>023</c> (<c>016</c>-<c>019</c> and <c>022</c> are
/// BM-3's set-filter-values population, a different query shape entirely, and stay out of this
/// harness).
///
/// <para><b>Why a SEPARATE case shape and a SEPARATE file, rather than 18 more entries in
/// <see cref="EfCoreExecutionEqualityTests.EqualityCase"/>.</b> That record assumes
/// <c>Columns == fixture.Schema.FieldOrder</c> and identifies/orders rows by the
/// <see cref="GridSchema.StableKey"/> column via a list of ids -- neither holds for a GROUPED level:
/// GRP-3's projection is <c>[groupColumn, ...aggregations.Select(a =&gt; a.OutputName)]</c>, and there
/// is no stable-key column in it to order or identify a row by at all. <see cref="GroupedEqualityCase"/>
/// below instead carries the expected COLUMN LIST and the expected ROWS as ordered cell lists,
/// unifying a grouped level's bucket rows and a drill-down leaf's full-field rows (<c>005</c>/<c>006</c>,
/// which DO have the ungrouped shape) under one representation: from this suite's point of view a leaf
/// is simply the case whose <c>ExpectedColumns</c> happens to equal <see cref="GridSchema.FieldOrder"/>,
/// with no separate code path needed. This mirrors why <see cref="SqliteTypeAffinityDivergenceTests"/>
/// is its own file rather than two more cases wedged into the ungrouped harness: a differently-shaped
/// assertion over the SAME plumbing (<see cref="FixtureRequest"/>, <see cref="SqliteFixtureDatabase"/>,
/// <see cref="Datasets"/>).</para>
///
/// <para><b>Every case's expected result is hand-computed and asserted BEFORE the parity assertion</b>,
/// for the identical reason <see cref="EfCoreExecutionEqualityTests"/> gives: two paths that agree with
/// each other and disagree with the independent expectation says which side of a boundary moved, and a
/// bare <c>efRows == sqlRows</c> is half an oracle. The datasets in <see cref="Datasets"/>' grouped
/// section are built so each case would FAIL under a plausible wrong implementation (a dropped
/// drill-down, a dropped row filter, a naive <c>g.Count()</c> row count, <c>AVG</c> treating a null as
/// zero) -- see each dataset's own comment for which mistake it catches, and
/// <see cref="Every_Case_Discriminates_Rather_Than_Matching_Everything_Or_Nothing"/> for the mechanical
/// guard that holds every case to it.</para>
///
/// <para><b>AGG-1's non-null-count trap (<c>011</c>) and AGG-6's empty-group answer (<c>013</c>) are
/// caught BY A REAL FIXTURE here</b> -- #1887's <c>EfCoreGroupingTests</c> already proved both, but
/// through a hand-built <see cref="EfCoreQueryRequest"/> its own doc says explicitly is not a fixture.
/// This is the fixture-driven counterpart.</para>
///
/// <para><b>SQLite's decimal/datetime type-affinity divergence (<see cref="SqliteTypeAffinityDivergenceTests"/>)
/// does not recur here.</b> Both documented divergences are about a COMPARISON operator
/// (<c>&gt;</c>/<c>&lt;=</c>) reading a TEXT-stored value differently on the two paths (a raw lexicographic
/// read vs EF's <c>ef_compare</c>/its own instant encoding); none of these 18 fixtures compares a
/// <c>decimal</c> or <c>datetime</c> value at all (<c>020</c>'s <c>amount &gt; 1000</c> is the one
/// row-level comparison in the population, and it is a plain numeric literal, not the boundary shape
/// the divergence needs). Every fixture here that touches <c>amount</c> instead AGGREGATES it
/// (<c>sum</c>/<c>avg</c>/<c>min</c>/<c>max</c>), and that is a DIFFERENT code path on both sides: EF
/// translates the aggregate into the SAME native SQLite <c>SUM</c>/<c>AVG</c>/<c>MIN</c>/<c>MAX</c> the
/// compiled statement calls (no <c>ef_compare</c> rewrite exists for an aggregate function), so the two
/// paths ask SQLite the identical question over the identical TEXT-affinity column and get the identical
/// answer -- measured here across all of <c>007</c>-<c>015</c>, <c>020</c>, <c>021</c>, <c>023</c>, every
/// aggregated value seeded as a whole-cent amount specifically so SQLite's own TEXT-to-REAL coercion
/// introduces no binary floating-point rounding for this suite to have to reason about.</para>
/// </summary>
public class EfCoreGroupedExecutionEqualityTests
{
    private const string Category = "group-aggregation";

    /// <summary>
    /// A population-wide property the ticket requires the 18 cases to collectively exhibit at least
    /// once each (not necessarily every case) -- checked mechanically per case in
    /// <see cref="Every_Claimed_Discrimination_Property_Actually_Holds_In_Its_Dataset"/> and for
    /// population coverage in <see cref="The_Harness_Covers_Eighteen_Grouped_Cases"/>, rather than
    /// trusted from a case's own <see cref="GroupedEqualityCase.Discriminates"/> prose.
    /// </summary>
    private enum DiscriminationProperty
    {
        NullInAggregatedColumn,
        AllNullGroup,
        SingleRowGroup,
        NullGroupKey
    }

    /// <summary>
    /// One case: a real fixture under <see cref="Category"/>, an inline dataset built for it, the
    /// output COLUMNS and ROWS a correct implementation returns (in that order -- there is no id to
    /// sort or identify a grouped row by, so the order itself is part of the expectation), which of
    /// <see cref="DiscriminationProperty"/> this case's own dataset is claimed to exhibit, and what a
    /// plausible wrong implementation would return instead.
    /// </summary>
    private sealed record GroupedEqualityCase(
        string Prefix,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Dataset,
        IReadOnlyList<string> ExpectedColumns,
        IReadOnlyList<IReadOnlyList<object?>> ExpectedRows,
        IReadOnlyList<DiscriminationProperty> Properties,
        string Discriminates);

    private static readonly Dictionary<string, GroupedEqualityCase> Cases = new(StringComparer.Ordinal)
    {
        ["001"] = new(
            "001",
            Datasets.GroupedRegionsTopLevel(),
            ["region", "amount_sum"],
            [["APAC", 50.00m], ["EMEA", 300.00m], [null, 75.00m]],
            [DiscriminationProperty.NullInAggregatedColumn, DiscriminationProperty.SingleRowGroup, DiscriminationProperty.NullGroupKey],
            "GRP-1's top-level dispatch (0 keys consumed) with no drill-down and no filter at all: what " +
            "discriminates is that grouping actually COLLAPSES the 5 seeded rows into 3 buckets, that a NULL " +
            "region survives as its own bucket rather than being dropped, and that a null amount inside EMEA's " +
            "bucket is skipped rather than breaking the sum."),

        ["002"] = new(
            "002",
            Datasets.SegmentsUnderEmeaExact(),
            ["segment", "amount_sum"],
            [["Enterprise", 150.00m], ["SMB", 30.00m]],
            [DiscriminationProperty.SingleRowGroup],
            "GRP-2's exact (never case-folding) drill-down match: a lowercase 'emea' row must be EXCLUDED " +
            "even though region is an ordinary case-INSENSITIVE field everywhere else -- folding case here " +
            "would inflate Enterprise's sum by 999.00. A second row of the wrong region entirely is the " +
            "ordinary exclusion check beside it."),

        ["003"] = new(
            "003",
            Datasets.ProductsUnderEmeaEnterprise(),
            ["product", "amount_sum"],
            [["ProductA", 15.00m], ["ProductB", 20.00m]],
            [DiscriminationProperty.SingleRowGroup],
            "GRP-5/SQL-2's wrapped AND across two consumed keys: one row matches region but not segment, " +
            "another matches segment but not region, and both must be excluded -- a compiler that OR'd the " +
            "two conditions instead of ANDing them would wrongly admit at least one."),

        ["004"] = new(
            "004",
            Datasets.ProductsUnderEmeaEnterprise2024(),
            ["product", "amount_sum"],
            [["ProductA", 7.00m], ["ProductB", 3.00m]],
            [DiscriminationProperty.SingleRowGroup],
            "The third consumed key is int64 (fiscalYear = 2024), not a string: a row with every OTHER key " +
            "matching but the wrong fiscalYear must still be excluded, which fails if the drill-down's typed " +
            "equality were rendered as a textual rather than a numeric comparison."),

        ["005"] = new(
            "005",
            Datasets.DrillDownLeafTwoKeys(),
            ["id", "region", "segment", "product", "fiscalYear", "amount", "quantity"],
            [
                ["A2", "EMEA", "Enterprise", "Apple", 2024L, 20.00m, 2L],
                ["A3", "EMEA", "Enterprise", "APPLE", 2023L, null, null],
                ["A1", "EMEA", "Enterprise", "banana", 2024L, 10.00m, 1L]
            ],
            [],
            "GRP-3's full-field leaf projection, sorted by the ORDINARY (SEM-1-collating) key 'product': " +
            "'Apple'/'APPLE' tie under the case-insensitive collation and are resolved by the appended 'id' " +
            "tiebreaker, which renders BARE even though 'id' is itself a case-insensitive field -- collating " +
            "the tiebreaker instead would still break the tie, but by the wrong rule. Two rows outside the " +
            "drill-down (a wrong segment, a wrong region) and one lowercase 'emea' (GRP-2 exactness) must all " +
            "be excluded."),

        ["006"] = new(
            "006",
            Datasets.DrillDownLeafSingleKey(),
            ["id", "region", "segment", "product", "fiscalYear", "amount", "quantity"],
            [
                ["B1", "EMEA", "Y", "P2", 2024L, 2.00m, 2L],
                ["a9", "EMEA", "Z", "P3", 2024L, 3.00m, 3L],
                ["b2", "EMEA", "X", "P1", 2024L, 1.00m, 1L]
            ],
            [],
            "With no sortModel at all, the ONLY sort key is the appended 'id' tiebreaker, and SEM-6's " +
            "supersession note is the whole point of this case: it renders BARE (SQLite's default BINARY " +
            "collation, uppercase before lowercase), so the correct order is B1, a9, b2 -- a case-INSENSITIVE " +
            "tiebreaker would instead produce a9, B1, b2, a different sequence this case's ids are chosen to " +
            "expose."),

        ["007"] = new(
            "007",
            Datasets.SegmentSums(),
            ["segment", "amount_sum"],
            [["Enterprise", 30.00m], ["SMB", 5.00m]],
            [DiscriminationProperty.NullInAggregatedColumn],
            "sum(amount): a null amount inside SMB's own bucket must be skipped by the arithmetic, not " +
            "propagate a null result or throw, and the APAC row stays excluded by the region drill-down."),

        ["008"] = new(
            "008",
            Datasets.SegmentMins(),
            ["segment", "amount_min"],
            [["Enterprise", 10.00m], ["SMB", 30.00m]],
            [DiscriminationProperty.NullInAggregatedColumn],
            "min(amount): SMB's null must be skipped rather than treated as the smallest possible value, and " +
            "the excluded APAC row (1.00) is numerically BELOW every surviving value -- if the drill-down were " +
            "dropped, both segments' minimums would silently become 1.00."),

        ["009"] = new(
            "009",
            Datasets.SegmentMaxes(),
            ["segment", "amount_max"],
            [["Enterprise", 90.00m], ["SMB", 20.00m]],
            [DiscriminationProperty.NullInAggregatedColumn],
            "max(amount): SMB's null must be skipped, and the excluded APAC row (999.00) is numerically ABOVE " +
            "every surviving value -- the mirror of 008's min case."),

        ["010"] = new(
            "010",
            Datasets.SegmentAverages(),
            ["segment", "amount_avg"],
            [["Enterprise", 20.00m], ["SMB", 15.00m]],
            [DiscriminationProperty.NullInAggregatedColumn, DiscriminationProperty.SingleRowGroup],
            "avg(amount): Enterprise's null must be excluded from BOTH the numerator and the denominator -- an " +
            "implementation that averages over every row in the bucket (treating the null as zero) reports " +
            "13.33, not the correct 20.00."),

        ["011"] = new(
            "011",
            Datasets.SegmentIdCounts(),
            ["segment", "id_count"],
            [["Enterprise", 1], ["SMB", 2]],
            [DiscriminationProperty.NullInAggregatedColumn],
            "AGG-1: count(id) counts NON-NULL occurrences of the field, never a bare row count. Enterprise's " +
            "bucket has 2 rows but only 1 non-null id -- a naive g.Count()/{\"$sum\":1} row count answers 2, " +
            "and the drill-down exclusion (APAC) would otherwise inflate it further still."),

        ["012"] = new(
            "012",
            Datasets.SegmentSumAndAverage(),
            ["segment", "amountSum", "amountAvg"],
            [["Enterprise", 40.00m, 20.00m], ["SMB", 25.00m, 25.00m]],
            [DiscriminationProperty.NullInAggregatedColumn, DiscriminationProperty.SingleRowGroup],
            "AGG-2's explicit aliasing of two aggregations on the SAME field, each independently skipping " +
            "Enterprise's null: sum must not silently reuse avg's denominator or vice versa, and both must " +
            "agree on which rows they are aggregating over (the same drill-down-restricted bucket)."),

        ["013"] = new(
            "013",
            Datasets.RegionSumMinMax(),
            ["region", "amountSum", "amountMin", "amountMax"],
            [["APAC", 0.00m, null, null], ["EMEA", 140.00m, 50.00m, 90.00m]],
            [DiscriminationProperty.NullInAggregatedColumn, DiscriminationProperty.AllNullGroup],
            "AGG-6, pinned through a REAL fixture rather than #1887's hand-built one: APAC's bucket has amount " +
            "NULL in EVERY row, so sum must answer 0.00 while min/max answer null -- ALL THREE computed from " +
            "the identical all-null bucket in one request."),

        ["014"] = new(
            "014",
            Datasets.RegionTotalRevenueAlias(),
            ["region", "totalRevenue"],
            [["APAC", 5.00m], ["EMEA", 30.00m]],
            [DiscriminationProperty.NullInAggregatedColumn],
            "An EXPLICIT alias ('totalRevenue') must be used verbatim as the output column name -- an " +
            "implementation that fell back to the compiler's default ('amount_sum') is caught by the columns " +
            "assertion itself, before a single value is even compared."),

        ["015"] = new(
            "015",
            Datasets.SegmentDefaultAliasSum(),
            ["segment", "amount_sum"],
            [["Enterprise", 40.00m], ["SMB", 60.00m]],
            [DiscriminationProperty.NullInAggregatedColumn, DiscriminationProperty.SingleRowGroup],
            "No alias at all: the compiler's DEFAULT output name ('amount_sum') must be used, contrasted with " +
            "014's explicit one -- and Enterprise's null must still be skipped."),

        ["020"] = new(
            "020",
            Datasets.RegionsFilteredAndSortedByTotal(),
            ["region", "amount_sum"],
            [["APAC", 2700.00m], ["EMEA", 2000.00m]],
            [DiscriminationProperty.SingleRowGroup],
            "AR-5's row-level filter (amount > 1000) applied BEFORE aggregation, composed with a sort on the " +
            "aggregate itself: a row that fails the filter inside an otherwise-surviving bucket (EMEA's 0.50) " +
            "must not reach the sum, a null amount must fail the filter under SEM-2's three-valued logic rather " +
            "than being coalesced into a match, and a region every one of whose rows fails the filter (LATAM) " +
            "must not appear as a bucket AT ALL -- which is what tells a pre-aggregation WHERE apart from a " +
            "post-aggregation HAVING."),

        ["021"] = new(
            "021",
            Datasets.SegmentsSortedByAutoColumn(),
            ["segment", "amount_sum"],
            [[null, 30.00m], ["SMB", 20.00m], ["Enterprise", 10.00m]],
            [DiscriminationProperty.SingleRowGroup, DiscriminationProperty.NullGroupKey],
            "AG-6's coupled auto-column sortModel (1 + N entries) must collapse to the single group-column key " +
            "'segment desc' -- if the collapse instead left 'region'/'product' as live sort keys, the whole " +
            "request would be rejected as ordering by a column the grouped projection does not contain. The " +
            "null segment bucket sorting FIRST (SEM-3's nulls-first default for desc) is the second thing this " +
            "case pins."),

        ["023"] = new(
            "023",
            Datasets.ProductsUnderDrillDownWithRowFilter(),
            ["product", "amount_sum"],
            [["ProductA", 15.00m], ["ProductB", 20.00m]],
            [DiscriminationProperty.SingleRowGroup],
            "GRP-5's composition of a drill-down AND a user filter, in ONE request: one row fails only the " +
            "drill-down's segment, one fails only the filter's fiscalYear, one fails only the filter's " +
            "quantity (right on the 500 boundary) -- dropping any ONE of the three conditions silently would " +
            "still be caught, since each has its own dedicated counter-example row.")
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

    // ---- the AR-4 assertion, adapted for bucket rows -----------------------------------------------

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Both_Paths_Return_The_Same_Rows_From_The_Same_Grouped_Or_Leaf_Level(string caseName)
    {
        var testCase = Cases[caseName];
        var fixture = LoadFixture(testCase);
        var grouping = GroupingPlan.Resolve(fixture.Ef.Group, fixture.Ef.Aggregations)!;

        using var database = SqliteFixtureDatabase.Create(fixture.Schema, testCase.Dataset);

        // Path 1: the SQL compiler, executed as text + parameters.
        var compiled = SqlQueryBuilder.Build(fixture.Sql, SqliteDialect.Instance);
        var sqlRows = grouping.GroupColumn is null
            ? database.ExecuteCompiledLeafProjection(compiled)
            : database.ExecuteCompiledGroupedLevel(compiled, grouping.GroupColumn, grouping.Aggregations);

        // Path 2: this adapter, composed onto the same table's IQueryable and executed by EF.
        var page = await EfCoreGridQuery.FetchPageAsync(
            database.Rows(), fixture.Ef, EfCoreProviderSemantics.Sqlite);

        Assert.Equal(testCase.ExpectedColumns, page.Columns);

        // Against the independent expectation FIRST: if this fails, the two paths agreeing with each
        // other says nothing about which one is right.
        Assert.Equal(testCase.ExpectedRows, sqlRows);
        Assert.Equal(testCase.ExpectedRows, page.Rows);

        // ...then the parity assertion itself.
        AssertRowsEqual(sqlRows, page.Rows, testCase);
    }

    /// <summary>
    /// #1653's obligation, restated for a bucket row: the group column reads back as the SCHEMA's own
    /// declared type, and each aggregation reads back as its function's own CLR result type --
    /// <c>sum</c>/<c>avg</c>/<c>min</c>/<c>max</c> unwrap to the aggregated field's declared type,
    /// <c>count</c> is always <see cref="int"/> regardless of what it counts (<c>011</c> counts a
    /// <c>string</c> field).
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Every_Cell_Is_The_Native_Clr_Type_The_Schema_Or_Aggregate_Declares(string caseName)
    {
        var testCase = Cases[caseName];
        var fixture = LoadFixture(testCase);
        var grouping = GroupingPlan.Resolve(fixture.Ef.Group, fixture.Ef.Aggregations)!;

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

                var declared = ExpectedClrType(fixture.Schema, grouping, page.Columns[index]);
                Assert.Equal(declared, cell.GetType());
            }
        }
    }

    // ---- guards on the cases themselves --------------------------------------------------------------

    /// <summary>
    /// The anti-tautology guard. A case with a drill-down and/or a row filter must seed at least one row
    /// that EITHER fails: without one, an implementation that dropped the drill-down/filter entirely
    /// would compute the identical aggregate this case expects, and the case would prove nothing. A case
    /// with NEITHER (001, 013, 014 -- top-level, unfiltered) instead must have its dataset actually
    /// COLLAPSE into fewer groups than seeded rows, or an implementation that skipped grouping outright
    /// (echoing one output row per input row) would be indistinguishable from a correct one by row count
    /// alone.
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Every_Case_Discriminates_Rather_Than_Matching_Everything_Or_Nothing(string caseName)
    {
        var testCase = Cases[caseName];
        var fixture = LoadFixture(testCase);

        Assert.NotEmpty(testCase.ExpectedRows);
        Assert.NotEmpty(testCase.Discriminates);

        var hasDrillDown = fixture.Ef.Group is { GroupKeys.Count: > 0 };
        var hasFilter = fixture.Ef.Filter is not null;

        if (hasDrillDown || hasFilter)
        {
            Assert.True(
                testCase.Dataset.Any(row => ExcludedByDrillDownOrFilter(row, fixture)),
                $"{fixture.Name} carries a drill-down and/or a row filter, so its dataset must contain a row " +
                "EITHER rejects -- otherwise an implementation that dropped it entirely would pass unnoticed.");
        }
        else
        {
            Assert.True(
                testCase.ExpectedRows.Count < testCase.Dataset.Count,
                $"{fixture.Name} carries neither a drill-down nor a row filter, so its dataset must still " +
                "collapse into fewer groups than seeded rows, or an ungrouped echo would pass it too.");
        }
    }

    /// <summary>
    /// Every <see cref="DiscriminationProperty"/> a case claims (<see cref="GroupedEqualityCase.Properties"/>)
    /// is verified against its own dataset here, mechanically -- a leaf case (005/006) has no bucket for
    /// any of the four to describe and so must claim none.
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Every_Claimed_Discrimination_Property_Actually_Holds_In_Its_Dataset(string caseName)
    {
        var testCase = Cases[caseName];
        var fixture = LoadFixture(testCase);
        var grouping = GroupingPlan.Resolve(fixture.Ef.Group, fixture.Ef.Aggregations)!;

        if (grouping.GroupColumn is null)
        {
            Assert.Empty(testCase.Properties);
            return;
        }

        var buckets = Buckets(testCase, fixture, grouping);

        foreach (var property in testCase.Properties)
        {
            var holds = property switch
            {
                DiscriminationProperty.NullInAggregatedColumn => grouping.Aggregations.Any(aggregation =>
                    testCase.Dataset.Any(row =>
                        !ExcludedByDrillDownOrFilter(row, fixture) &&
                        (!row.TryGetValue(aggregation.Field, out var value) || value is null))),

                DiscriminationProperty.AllNullGroup => buckets.Any(bucket => grouping.Aggregations.Any(
                    aggregation => bucket.All(row => !row.TryGetValue(aggregation.Field, out var value) || value is null))),

                DiscriminationProperty.SingleRowGroup => buckets.Any(bucket => bucket.Count() == 1),

                DiscriminationProperty.NullGroupKey => buckets.Any(bucket => bucket.Key is null),

                _ => throw new ArgumentOutOfRangeException(nameof(property), property, "Unknown discrimination property.")
            };

            Assert.True(holds, $"{fixture.Name} claims '{property}' but its own dataset does not exhibit it.");
        }
    }

    /// <summary>
    /// Every case's fixture is a real directory under <see cref="Category"/> carrying a real Postgres
    /// artifact, mirroring <see cref="EfCoreExecutionEqualityTests.Every_Case_Names_A_Fixture_That_Exists_And_Carries_A_Compiled_Artifact"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Every_Case_Names_A_Fixture_That_Exists_And_Carries_A_Compiled_Artifact(string caseName)
    {
        var testCase = Cases[caseName];
        var directory = FixtureRequest.ResolveDirectory(Category, testCase.Prefix);

        Assert.True(File.Exists(RepoPaths.Fixture(Category, directory, "expected", "postgres.artifact.json")));
    }

    [Fact]
    public void The_Harness_Covers_Eighteen_Grouped_Cases()
    {
        // A SEPARATE count from EfCoreExecutionEqualityTests' twelve -- that guard is specific to the
        // ungrouped population and folding this one into it would conflate two differently-shaped
        // populations, the same reason SqliteTypeAffinityDivergenceTests stays its own file rather than
        // two more cases in the ungrouped table.
        Assert.Equal(18, Cases.Count);
        Assert.Equal(
            ["001", "002", "003", "004", "005", "006", "007", "008", "009", "010",
             "011", "012", "013", "014", "015", "020", "021", "023"],
            Cases.Keys.OrderBy(name => name, StringComparer.Ordinal));

        // The four properties the ticket requires the POPULATION to hit at least once each, even though
        // no single dataset needs all four -- verified against what the cases above actually CLAIM
        // (Every_Claimed_Discrimination_Property_Actually_Holds_In_Its_Dataset checks each claim is true
        // of its own case; this checks the claims collectively cover the whole required set).
        Assert.Equal(
            Enum.GetValues<DiscriminationProperty>().OrderBy(property => property),
            Cases.Values.SelectMany(testCase => testCase.Properties).Distinct().OrderBy(property => property));
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private static FixtureRequest LoadFixture(GroupedEqualityCase testCase) =>
        FixtureRequest.Load(Category, FixtureRequest.ResolveDirectory(Category, testCase.Prefix));

    /// <summary>
    /// Whether <paramref name="row"/> is excluded from every bucket a correct implementation would
    /// produce -- either by GRP-2's exact drill-down match or by the request's own row-level filter.
    /// This is validation of THIS SUITE'S OWN fixtures (did the case author actually seed a row the
    /// drill-down/filter rejects), never a re-implementation of a compiler: see
    /// <see cref="EvaluateSimpleFilter"/> for the deliberately narrow scope that keeps it that way.
    /// </summary>
    private static bool ExcludedByDrillDownOrFilter(IReadOnlyDictionary<string, object?> row, FixtureRequest fixture)
    {
        if (fixture.Ef.Group is { } group)
        {
            for (var i = 0; i < group.GroupKeys.Count; i++)
            {
                if (!MatchesExact(row, group.GroupCols[i], group.GroupKeys[i]))
                {
                    return true;
                }
            }
        }

        return fixture.Ef.Filter is { } filter && !EvaluateSimpleFilter(filter, row);
    }

    /// <summary>GRP-2: a drill-down key match is exact, never case-folding -- ordinal/numeric equality,
    /// the same mechanism <see cref="EfCoreQueryBuilder"/> resolves it through in production.</summary>
    private static bool MatchesExact(IReadOnlyDictionary<string, object?> row, string column, TypedValue key)
    {
        row.TryGetValue(column, out var value);

        return (key, value) switch
        {
            (StringValue s, string text) => string.Equals(s.Value, text, StringComparison.Ordinal),
            (Int64Value i, long l) => i.Value == l,
            _ => false
        };
    }

    /// <summary>
    /// A deliberately minimal <see cref="FilterNode"/> evaluator -- <see cref="AndNode"/> plus a numeric
    /// comparison (<c>gt</c>/<c>gte</c>/<c>lt</c>/<c>lte</c>/<c>eq</c>) over an <see cref="Int64Value"/>
    /// or a <see cref="DecimalValue"/>, exactly the two shapes <c>020</c> and <c>023</c> carry and no
    /// more. This checks whether THIS SUITE'S dataset rows satisfy the fixture's own filter, so the
    /// anti-tautology guard above can confirm a case actually seeded a row the filter rejects -- it is
    /// not a second compiler, and it throws rather than guess at a shape it was not built for.
    /// </summary>
    private static bool EvaluateSimpleFilter(FilterNode node, IReadOnlyDictionary<string, object?> row) => node switch
    {
        AndNode and => and.Nodes.All(child => EvaluateSimpleFilter(child, row)),
        ComparisonNode comparison => EvaluateComparison(comparison, row),
        _ => throw new NotSupportedException(
            $"This harness's own minimal filter evaluator (test-dataset validation only, never a compiler) " +
            $"does not support '{node.GetType().Name}' -- extend it if a future case needs it.")
    };

    private static bool EvaluateComparison(ComparisonNode node, IReadOnlyDictionary<string, object?> row)
    {
        row.TryGetValue(node.Field, out var raw);
        if (raw is null)
        {
            // SEM-2: a null operand fails every comparison here (three-valued logic), matching every
            // compiler this suite compares against -- never coalesced into a match.
            return false;
        }

        var left = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
        var right = node.Value switch
        {
            Int64Value i => (decimal)i.Value,
            DecimalValue d => decimal.Parse(d.Value, NumberStyles.Number, CultureInfo.InvariantCulture),
            var value => throw new NotSupportedException(
                $"This harness's own minimal filter evaluator does not support comparing against a " +
                $"'{value?.GetType().Name}'.")
        };

        return node.Op switch
        {
            ComparisonOperator.Gt => left > right,
            ComparisonOperator.Gte => left >= right,
            ComparisonOperator.Lt => left < right,
            ComparisonOperator.Lte => left <= right,
            ComparisonOperator.Eq => left == right,
            _ => throw new NotSupportedException(
                $"This harness's own minimal filter evaluator does not support operator '{node.Op}'.")
        };
    }

    /// <summary>The dataset's rows, restricted to those a correct implementation actually buckets
    /// (drill-down/filter survivors), grouped by the group column's own raw value -- used only to check
    /// the <see cref="DiscriminationProperty"/> claims above against the fixtures' own group column,
    /// never to compute an expected VALUE (those are hand-authored in <see cref="Datasets"/>).</summary>
    private static IEnumerable<IGrouping<object?, IReadOnlyDictionary<string, object?>>> Buckets(
        GroupedEqualityCase testCase, FixtureRequest fixture, GroupingPlan grouping) =>
        testCase.Dataset
            .Where(row => !ExcludedByDrillDownOrFilter(row, fixture))
            .GroupBy(row => row.TryGetValue(grouping.GroupColumn!, out var value) ? value : null);

    private static Type ExpectedClrType(GridSchema schema, GroupingPlan grouping, string column)
    {
        if (grouping.GroupColumn is null || column == grouping.GroupColumn)
        {
            return Unwrap(EfCoreQueryBuilder.ClrTypeFor(schema.Fields[column].Type));
        }

        var aggregation = grouping.Aggregations.Single(a => a.OutputName == column);

        return aggregation.Func == AggregationFunc.Count
            ? typeof(int)
            : Unwrap(EfCoreQueryBuilder.ClrTypeFor(schema.Fields[aggregation.Field].Type));
    }

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static void AssertRowsEqual(
        IReadOnlyList<IReadOnlyList<object?>> sql,
        IReadOnlyList<IReadOnlyList<object?>> ef,
        GroupedEqualityCase testCase)
    {
        Assert.Equal(sql.Count, ef.Count);

        for (var row = 0; row < sql.Count; row++)
        {
            for (var column = 0; column < testCase.ExpectedColumns.Count; column++)
            {
                Assert.True(
                    Equals(sql[row][column], ef[row][column]),
                    $"Row {row}, column '{testCase.ExpectedColumns[column]}': the SQL path returned " +
                    $"{Describe(sql[row][column])} and the EF path returned {Describe(ef[row][column])}. " +
                    $"This case exists to catch: {testCase.Discriminates}");
            }
        }
    }

    private static string Describe(object? value) =>
        value is null ? "<null>" : $"{value} ({value.GetType().Name})";
}
