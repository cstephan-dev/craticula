using System.Text.Json;
using System.Text.RegularExpressions;
using Craticula.AgGrid;
using Craticula.Core.Schema;

namespace Craticula.Sql.Tests;

/// <summary>
/// T-1a artifact equality: for every leaf fixture this story scopes in, run the REAL end-to-end path
/// a host runs — <see cref="AgGridRequestParser.Parse"/> -&gt;
/// <see cref="GridFilterPipeline.Process"/> -&gt; <see cref="SqlQueryBuilder.Build"/> — and compare
/// the result byte-for-byte against that fixture's own <c>expected/postgres.artifact.json</c>.
///
/// <para><b>The artifact is the oracle, never a literal repeated here.</b> The fixtures were
/// hand-authored (#1630) and repaired by mechanical transformation of that hand-authored SQL
/// (#1638 Phase A) specifically so this comparison is between two independently-derived answers. A
/// disagreement is therefore informative in both directions and must be investigated, never
/// resolved by editing the fixture.</para>
/// </summary>
public class PostgresConformanceFixtureTests
{
    /// <summary>
    /// The seven categories this compiler compiles end to end. Each declares the number of Postgres
    /// artifacts it is expected to contribute, so adding a fixture to one of them fails
    /// <see cref="Each_In_Scope_Category_Contributes_Exactly_The_Fixture_Count_It_Declares"/> rather
    /// than being silently unexercised.
    ///
    /// <para><c>group-aggregation</c> joined in #1640 (BM-2), which is also why
    /// <c>ag-grid-majors</c> went from 5 to 6: its <c>004</c> is a grouped request and was excluded
    /// by name until a grouping compiler existed. It reached all 21 of its cases in #1641, when
    /// <c>016</c>-<c>019</c>'s BM-3 distinct-values path gained a compiler
    /// (<see cref="SqlQueryBuilder.BuildSetValues"/>); <see cref="FixtureCompiler"/> dispatches on the
    /// request document, so both kinds run through this same theory. #1641's review pass added
    /// <c>022</c> — the one case carrying a filter model AND a <c>searchPrefix</c>, which is what
    /// makes GRP-6(c)'s <c>WHERE</c> order artifact-pinned rather than unit-test-pinned — taking it
    /// to 22.</para>
    /// </summary>
    private static readonly (string Category, int Expected)[] InScopeCategories =
    [
        ("operators", 47),
        ("date-presets", 26),
        ("dialect-parity", 18),
        ("group-aggregation", 22),
        ("filter-tree", 15),
        ("sort-paging", 11),
        ("ag-grid-majors", 6)
    ];

    /// <summary>
    /// The categories deliberately NOT compiled, each with the reason — stated here rather than left
    /// as a silent absence, so a reader can tell "out of scope" from "forgotten".
    /// <see cref="Every_Fixture_Category_On_Disk_Is_Either_In_Scope_Or_Excluded_By_Name"/> fails if a
    /// NEW category appears and is named in neither list.
    /// </summary>
    private static readonly Dictionary<string, string> ExcludedCategories = new(StringComparer.Ordinal)
    {
        ["schemas"] = "Not a fixture category at all — the suite's own meta-schemas " +
                      "(sql-artifact.schema.json and friends), validated by ajv-cli, not by this project.",
        ["adversarial"] = "Rejection-only fixtures: they carry expected/rejection.json, not a compiled artifact, " +
                          "and are already exercised by Craticula.Core.Tests' allowlist/limits/coercion suites.",
        ["page-export"] = "The export feature (EXP-1..EXP-8) owns the explicit projection and exportScope -> " +
                          "page:none mapping these fixtures pin; a leaf builder emitting SELECT * cannot produce them."
    };

    /// <summary>
    /// The one fixture inside an in-scope category that is nonetheless excluded — with its reason.
    /// Everything else that carries no <c>expected/postgres.artifact.json</c> (the rejection-shaped
    /// cases, and <c>dialect-parity/001</c>-<c>004</c>'s single-dialect MySQL/SQL Server/SQLite
    /// artifacts, which are #1639's) is filtered out by the discovery below without needing to be
    /// listed, since there is nothing to compare against.
    /// </summary>
    private static readonly Dictionary<string, string> ExcludedFixtures = new(StringComparer.Ordinal)
    {
        ["sort-paging/012-page-none-unpaginated-export"] =
            "BLOCKED UPSTREAM, not out of scope: the fixture resolves page:none from EXP-1's " +
            "exportScope:\"all\", and AgGridRequestParser has no exportScope handling (a gap #1635 " +
            "documented deliberately, and one #1638 was told not to build export machinery for). The " +
            "SQL builder itself compiles this fixture exactly — see " +
            "SqlQueryBuilderPageAndSortTests.SortPaging012_Compiles_To_Its_Own_Artifact_Once_The_Page_Is_NonePage, " +
            "which asserts against the same artifact with only the PageSpec supplied. Delete this " +
            "entry the moment the adapter learns exportScope."
    };

    public static TheoryData<string, string> InScopeFixtures
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (category, _) in InScopeCategories)
            {
                foreach (var fixtureDir in DiscoverInScope(category))
                {
                    data.Add(category, fixtureDir);
                }
            }

            return data;
        }
    }

    /// <summary>Every case directory in <paramref name="category"/> that carries a Postgres artifact
    /// and is not explicitly excluded, in ordinal name order.</summary>
    private static IReadOnlyList<string> DiscoverInScope(string category) =>
    [
        .. Directory.EnumerateDirectories(RepoPaths.Fixture(category))
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .Where(name => File.Exists(RepoPaths.Fixture(category, name, "expected", "postgres.artifact.json")))
            .Where(name => !ExcludedFixtures.ContainsKey($"{category}/{name}"))
            .OrderBy(name => name, StringComparer.Ordinal)
    ];

    // ---- the conformance assertion ---------------------------------------------------------------

    [Theory]
    [MemberData(nameof(InScopeFixtures))]
    public void Compiles_To_The_Sql_And_Params_Its_Own_Postgres_Artifact_Pins(string category, string fixtureDir)
    {
        var actual = Compile(category, fixtureDir);
        using var expected = JsonDocument.Parse(Read(category, fixtureDir, "expected", "postgres.artifact.json"));

        Assert.Equal(expected.RootElement.GetProperty("dialect").GetString(), actual.Dialect);
        Assert.Equal(expected.RootElement.GetProperty("sql").GetString(), actual.Sql);
        Assert.Equal(
            CanonicalJson(expected.RootElement.GetProperty("params")),
            CanonicalJson(actual.Parameters));
    }

    /// <summary>
    /// SEC-2's structural invariant: the statement's placeholders and the parameter list are the
    /// same length. A dialect that formatted a placeholder without binding (or bound without
    /// formatting) would desynchronize the two, and the artifact comparison above would only catch
    /// it if the drift happened to change the SQL text as well.
    /// </summary>
    [Theory]
    [MemberData(nameof(InScopeFixtures))]
    public void Binds_Exactly_As_Many_Parameters_As_The_Statement_Has_Placeholders(string category, string fixtureDir)
    {
        var actual = Compile(category, fixtureDir);

        var ordinals = PostgresPlaceholder.Matches(actual.Sql)
            .Select(match => int.Parse(match.Groups[1].Value))
            .ToList();

        Assert.Equal(actual.Parameters.Count, ordinals.Count);

        // ...and they are $1..$N in first-appearance order, which is what makes "params[] is
        // positional" true rather than merely plausible.
        Assert.Equal(Enumerable.Range(1, actual.Parameters.Count), ordinals);
    }

    private static readonly Regex PostgresPlaceholder = new(@"\$([0-9]+)", RegexOptions.Compiled);

    // ---- anti-rot guards -------------------------------------------------------------------------

    [Fact]
    public void Each_In_Scope_Category_Contributes_Exactly_The_Fixture_Count_It_Declares()
    {
        // Guards the usual fixture-test rot in the direction that matters: a case ADDED to one of
        // these categories later must either pass this suite or be given an explicit exclusion
        // reason — it can never just quietly go unexercised.
        var actual = InScopeCategories.ToDictionary(entry => entry.Category, entry => DiscoverInScope(entry.Category).Count);
        var declared = InScopeCategories.ToDictionary(entry => entry.Category, entry => entry.Expected);

        Assert.Equal(declared, actual);
    }

    [Fact]
    public void The_End_To_End_Suite_Is_145_Of_The_146_Fixtures_In_The_Scoped_Categories()
    {
        // 146 minus the ONE remaining entry in ExcludedFixtures: sort-paging/012, which the SQL
        // builder compiles correctly but the AG Grid adapter cannot yet FEED correctly (see
        // ExcludedFixtures for the full reason, and SqlQueryBuilderPageAndSortTests for the
        // assertion that closes the loop on the builder's half). This number going up without
        // ExcludedFixtures shrinking means a fixture was added. It last moved in #1641's review
        // pass, which added group-aggregation/022; the pass before that built BM-3's compiler and
        // released group-aggregation/016-019 from the exclusion list.
        Assert.Equal(145, InScopeFixtures.Count);
    }

    [Fact]
    public void Every_Fixture_Category_On_Disk_Is_Either_In_Scope_Or_Excluded_By_Name()
    {
        var onDisk = Directory
            .EnumerateDirectories(RepoPaths.Fixture())
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var accounted = InScopeCategories
            .Select(entry => entry.Category)
            .Concat(ExcludedCategories.Keys)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(accounted, onDisk);
    }

    [Fact]
    public void Every_Excluded_Fixture_Still_Exists_On_Disk()
    {
        // An exclusion that no longer matches a real directory is dead weight that hides whether the
        // case was renamed (and is now silently in scope) or deleted.
        foreach (var (path, reason) in ExcludedFixtures)
        {
            var segments = path.Split('/');
            Assert.True(
                Directory.Exists(RepoPaths.Fixture(segments)),
                $"Excluded fixture '{path}' no longer exists. Reason recorded for the exclusion: {reason}");
        }
    }

    // ---- the harness ------------------------------------------------------------------------------

    private static SqlQuery Compile(string category, string fixtureDir) =>
        FixtureCompiler.Compile(category, fixtureDir, PostgresDialect.Instance);

    private static string Read(string category, string fixtureDir, params string[] rest) =>
        FixtureCompiler.Read(category, fixtureDir, rest);

    private static string CanonicalJson<T>(T value) => FixtureCompiler.CanonicalJson(value);
}
