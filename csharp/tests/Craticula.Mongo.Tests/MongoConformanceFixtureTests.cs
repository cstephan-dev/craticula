using System.Text.Json.Nodes;
using Craticula.AgGrid;
using Craticula.Core.Schema;

namespace Craticula.Mongo.Tests;

/// <summary>
/// T-1a artifact equality for MongoDB: for every in-scope fixture, run the REAL end-to-end path a
/// host runs — <see cref="AgGridRequestParser.Parse"/> -&gt;
/// <see cref="GridFilterPipeline.Process"/> -&gt; <see cref="MongoPipelineBuilder.Build"/> — and
/// compare the result against that fixture's own <c>expected/mongo.artifact.json</c>.
///
/// <para><b>This suite is the inverse of the SQL ones, and the difference matters.</b> Every
/// <c>postgres.artifact.json</c> had been compiled and re-compiled since #1638; the 157
/// <c>mongo.artifact.json</c> files were hand-authored across #1630-#1642 as a secondary arm that
/// <em>nothing had ever executed</em>. So a disagreement here was never presumptively the compiler's
/// fault, and it was never presumptively the fixture's either: each was adjudicated against
/// <c>spec/semantics.md</c> and the sibling artifacts, and the losing side corrected. See
/// <c>docs/feat/1643/context.md</c> for the census.</para>
/// </summary>
public class MongoConformanceFixtureTests
{
    /// <summary>
    /// The seven categories this compiler compiles end to end. Each declares the number of Mongo
    /// artifacts it is expected to contribute, so adding a fixture to one of them fails
    /// <see cref="Each_In_Scope_Category_Contributes_Exactly_The_Fixture_Count_It_Declares"/> rather
    /// than being silently unexercised.
    ///
    /// <para><c>dialect-parity</c> contributes 17 rather than its 22 directories:
    /// <c>001</c>-<c>004</c> are #1639's single-dialect MySQL/SQL Server/SQLite showcases and
    /// <c>005</c> is Oracle's, none of which carries a Mongo arm at all. Discovery filters on the
    /// artifact's presence, so they need no exclusion entry — there is nothing to compare
    /// against.</para>
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
    /// NEW category appears and is named in neither list. The three reasons are the same three
    /// <c>PostgresConformanceFixtureTests</c> gives, and deliberately so: nothing about them is
    /// backend-specific, so a divergence between the two lists would mean one of the suites had
    /// silently changed scope.
    /// </summary>
    private static readonly Dictionary<string, string> ExcludedCategories = new(StringComparer.Ordinal)
    {
        ["schemas"] = "Not a fixture category at all — the suite's own meta-schemas " +
                      "(mongo-artifact.schema.json and friends), validated by ajv-cli, not by this project.",
        ["adversarial"] = "Rejection-only fixtures: they carry expected/rejection.json, not a compiled artifact, " +
                          "and are already exercised by Craticula.Core.Tests' allowlist/limits/coercion suites. " +
                          "(They also carry no mongo.artifact.json, so discovery would skip them anyway — named " +
                          "here so the category list stays exhaustive.)",
        ["page-export"] = "The export feature (EXP-1..EXP-8) owns the explicit $project column list and the " +
                          "exportScope -> page:none mapping these 12 fixtures pin; a leaf builder that projects " +
                          "nothing on an ordinary row query cannot produce them. Excluded on exactly the same " +
                          "ground as in PostgresConformanceFixtureTests."
    };

    /// <summary>
    /// The one fixture inside an in-scope category that is nonetheless excluded, with its reason —
    /// again identical to the SQL suite's, because the blockage is upstream of both compilers.
    /// </summary>
    private static readonly Dictionary<string, string> ExcludedFixtures = new(StringComparer.Ordinal)
    {
        ["sort-paging/012-page-none-unpaginated-export"] =
            "BLOCKED UPSTREAM, not out of scope: the fixture resolves page:none from EXP-1's " +
            "exportScope:\"all\", and AgGridRequestParser has no exportScope handling (a gap #1635 " +
            "documented deliberately). The Mongo builder itself compiles this fixture exactly — see " +
            "MongoPipelineSortAndPagingTests.SortPaging012_Compiles_To_Its_Own_Artifact_Once_The_Page_Is_NonePage, " +
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

    /// <summary>Every case directory in <paramref name="category"/> that carries a Mongo artifact and
    /// is not explicitly excluded, in ordinal name order.</summary>
    private static IReadOnlyList<string> DiscoverInScope(string category) =>
    [
        .. Directory.EnumerateDirectories(RepoPaths.Fixture(category))
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .Where(name => File.Exists(RepoPaths.Fixture(category, name, "expected", "mongo.artifact.json")))
            .Where(name => !ExcludedFixtures.ContainsKey($"{category}/{name}"))
            .OrderBy(name => name, StringComparer.Ordinal)
    ];

    // ---- the conformance assertion ---------------------------------------------------------------

    [Theory]
    [MemberData(nameof(InScopeFixtures))]
    public void Compiles_To_The_Pipeline_Its_Own_Mongo_Artifact_Pins(string category, string fixtureDir)
    {
        var actual = MongoFixtureCompiler.Compile(category, fixtureDir);

        Assert.Equal(
            MongoFixtureCompiler.CanonicalJson(Read(category, fixtureDir, "expected", "mongo.artifact.json")),
            MongoFixtureCompiler.CanonicalJson(actual.ToArtifact()));
    }

    /// <summary>
    /// SEM-3's structural invariant, asserted independently of the artifact text: the null-bucket
    /// emulation is a CONTIGUOUS <c>$addFields</c> / <c>$sort</c> / <c>$unset</c> trio, and every
    /// bucket the first stage adds is named by the third.
    ///
    /// <para>Contiguity is the strongest form of SEM-3's "the <c>$unset</c> MUST appear after every
    /// <c>$sort</c> that used a bucket, before the pipeline's <c>$skip</c>/<c>$limit</c>/<c>$group</c>":
    /// nothing can slip between the three, so no stage that consumes documents ever sees the
    /// synthetic field. (It is stated that way rather than as "no <c>$group</c> before the
    /// <c>$unset</c>", because a grouped level's own <c>$group</c> legitimately runs BEFORE the trio —
    /// it is what produces the documents being sorted.)</para>
    ///
    /// <para>Worth its own test because it is the rule the hand-authored suite got wrong most often:
    /// a leaked bucket is a shape divergence from the Postgres/Search results for the identical IR,
    /// and the artifact comparison above would only catch it in the fixtures that happen to pin the
    /// stage. Here it is checked on all 144.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(InScopeFixtures))]
    public void Emulates_Null_Placement_With_A_Contiguous_AddFields_Sort_Unset_Trio(string category, string fixtureDir)
    {
        var pipeline = MongoFixtureCompiler.Compile(category, fixtureDir).Pipeline;

        var start = pipeline.Select((stage, index) => (stage, index)).First(entry => entry.stage.ContainsKey("$addFields")).index;

        Assert.True(pipeline[start + 1].ContainsKey("$sort"), "SEM-3: the $sort follows the buckets it uses.");
        Assert.True(pipeline[start + 2].ContainsKey("$unset"), "SEM-3: the $unset follows the $sort immediately.");

        var added = pipeline[start]["$addFields"]!.AsObject().Select(entry => entry.Key).ToList();
        var unset = pipeline[start + 2]["$unset"]!;
        var removed = unset is JsonArray array
            ? array.Select(name => name!.GetValue<string>()).ToList()
            : [unset.GetValue<string>()];

        Assert.NotEmpty(added);
        Assert.Equal(added, removed);
        Assert.All(added, name => Assert.EndsWith("NullBucket", name));

        // ...and there is exactly one trio, so no later $sort can reintroduce a bucket unnoticed.
        Assert.Single(pipeline, stage => stage.ContainsKey("$addFields"));
        Assert.Single(pipeline, stage => stage.ContainsKey("$unset"));
    }

    /// <summary>
    /// The artifact-shape rule <c>mongo-artifact.schema.json</c> states in prose: the
    /// <c>{ pipeline, collation }</c> wrapper appears if and only if a query-level collation is
    /// present, and the bare array otherwise. Asserted against the fixture on disk, so a compiler
    /// that emitted the right stages under the wrong wrapper still fails.
    /// </summary>
    [Theory]
    [MemberData(nameof(InScopeFixtures))]
    public void Uses_The_Wrapper_Object_Shape_Exactly_When_A_Query_Level_Collation_Is_Present(
        string category, string fixtureDir)
    {
        var actual = MongoFixtureCompiler.Compile(category, fixtureDir);
        var expected = JsonNode.Parse(Read(category, fixtureDir, "expected", "mongo.artifact.json"));

        Assert.Equal(actual.Collation is not null, expected is JsonObject);
        Assert.Equal(actual.Collation is not null, actual.ToArtifact() is JsonObject);
    }

    // ---- anti-rot guards -------------------------------------------------------------------------

    [Fact]
    public void Each_In_Scope_Category_Contributes_Exactly_The_Fixture_Count_It_Declares()
    {
        var actual = InScopeCategories.ToDictionary(entry => entry.Category, entry => DiscoverInScope(entry.Category).Count);
        var declared = InScopeCategories.ToDictionary(entry => entry.Category, entry => entry.Expected);

        Assert.Equal(declared, actual);
    }

    [Fact]
    public void The_End_To_End_Suite_Is_144_Of_The_145_Mongo_Artifacts_In_The_Scoped_Categories()
    {
        // 145 minus the ONE entry in ExcludedFixtures: sort-paging/012, which the Mongo builder
        // compiles correctly but the AG Grid adapter cannot yet FEED correctly. The suite holds 157
        // mongo.artifact.json files in total; the 12 outside this number are page-export's, whose
        // whole category is excluded.
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
    public void Every_Mongo_Artifact_On_Disk_Is_Compiled_Or_Accounted_For()
    {
        // The count that closes the loop on "144 in scope": every mongo.artifact.json in the repo is
        // either exercised above, or inside a category/fixture this class excludes BY NAME. A new
        // artifact in an excluded category still moves this number, which is what makes the exclusion
        // reasons re-read rather than inherited.
        var onDisk = Directory
            .EnumerateFiles(RepoPaths.Fixture(), "mongo.artifact.json", SearchOption.AllDirectories)
            .Count();

        var excludedCategoryArtifacts = ExcludedCategories.Keys
            .Where(category => Directory.Exists(RepoPaths.Fixture(category)))
            .Sum(category => Directory
                .EnumerateFiles(RepoPaths.Fixture(category), "mongo.artifact.json", SearchOption.AllDirectories)
                .Count());

        Assert.Equal(158, onDisk);
        Assert.Equal(12, excludedCategoryArtifacts);
        Assert.Equal(onDisk - excludedCategoryArtifacts - ExcludedFixtures.Count, InScopeFixtures.Count);
    }

    /// <summary>
    /// A recorded COVERAGE GAP, asserted so it self-heals rather than being a comment nobody
    /// re-reads: <b>no in-scope fixture declares a <c>physical.mongo.path</c> that differs from its
    /// colId</b>, so SC-4's path mapping — the thing that turns <c>customerName</c> into
    /// <c>customer.name</c> in every <c>$match</c> key, <c>$sort</c> key, <c>$group._id</c>,
    /// <c>$cond</c> reference and <c>$project</c> value — is exercised by unit tests alone
    /// (<c>MongoPipelineOperatorTests</c>, <c>MongoPipelineSortAndPagingTests</c>). The only schemas
    /// in the suite that declare one belong to <c>page-export/</c>, whose whole category is excluded
    /// above for a reason that has nothing to do with paths.
    ///
    /// <para>This test FAILS the day a fixture closes the gap, which is the point: at that moment
    /// this note is obsolete and should be deleted along with it, rather than left claiming a
    /// weakness that no longer exists.</para>
    /// </summary>
    [Fact]
    public void Records_That_No_In_Scope_Fixture_Maps_A_Mongo_Path_Away_From_Its_ColId()
    {
        var remapped = new List<string>();

        foreach (var (category, _) in InScopeCategories)
        {
            foreach (var fixtureDir in DiscoverInScope(category))
            {
                var schema = GridSchema.Parse(Read(category, fixtureDir, "schema.json"));

                remapped.AddRange(schema.Fields
                    .Where(field => field.Value.Physical?.Mongo?.Path is { } path && path != field.Key)
                    .Select(field => $"{category}/{fixtureDir}:{field.Key}"));
            }
        }

        Assert.Empty(remapped);
    }

    [Fact]
    public void Every_Excluded_Fixture_Still_Exists_On_Disk()
    {
        foreach (var (path, reason) in ExcludedFixtures)
        {
            var segments = path.Split('/');
            Assert.True(
                Directory.Exists(RepoPaths.Fixture(segments)),
                $"Excluded fixture '{path}' no longer exists. Reason recorded for the exclusion: {reason}");
        }
    }

    private static string Read(string category, string fixtureDir, params string[] rest) =>
        MongoFixtureCompiler.Read(category, fixtureDir, rest);
}
