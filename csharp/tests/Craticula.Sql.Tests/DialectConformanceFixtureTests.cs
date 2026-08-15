using System.Text.Json;
using System.Text.RegularExpressions;

namespace Craticula.Sql.Tests;

/// <summary>
/// T-1a artifact equality for the three dialects #1639 adds, the exact counterpart of
/// <see cref="PostgresConformanceFixtureTests"/>: for every fixture carrying an
/// <c>expected/&lt;dialect&gt;.artifact.json</c>, run the same end-to-end path
/// (<c>AgGridRequestParser.Parse</c> -&gt; <c>GridFilterPipeline.Process</c> -&gt;
/// <see cref="SqlQueryBuilder.Build"/>, via <see cref="FixtureCompiler"/>) and compare byte-for-byte
/// against that arm.
///
/// <para><b>The artifact is the oracle, never a literal repeated here.</b> All 55 non-Postgres arms
/// were produced by mechanical transformation — the 19 pre-existing ones by applying each engine's
/// quote character to their own fixture's <c>schema.json</c> identifiers, the 30 promoted ones by
/// substituting documented per-dialect tokens into that fixture's own <b>Postgres</b> arm, and
/// <c>021</c>/<c>022</c>'s six by transforming the arms of the fixture whose schema they reuse
/// verbatim (<c>014</c> and <c>020</c> respectively) — and never by running these dialects and
/// recording the output. That is what keeps this comparison between two independently-derived
/// answers instead of a tautology. A disagreement is informative in both directions and must be
/// investigated, never resolved by editing the fixture.</para>
///
/// <para><b>Why the parity showcases are the real assertion.</b> <c>dialect-parity/006</c>-<c>022</c>
/// carry the same request compiled for all four SQL dialects. Because the predicate structure,
/// SQL-2's parenthesisation, the ORDER BY key sequence, SEM-6's tiebreaker and the parameter
/// <em>values</em> are the builder's and cannot vary per dialect, four arms passing at once is a
/// direct check of BM-1's "adding a dialect changes only spellings" — the same claim
/// <see cref="DialectIndependenceTests"/> makes against a synthetic dialect, here against real
/// engines and a hand-authored oracle.</para>
/// </summary>
public class DialectConformanceFixtureTests
{
    /// <summary>
    /// The dialects under test, each with the number of arms it is expected to find on disk. Adding
    /// an arm to a fixture without updating this table fails
    /// <see cref="Each_Dialect_Contributes_Exactly_The_Arm_Count_It_Declares"/> rather than being
    /// silently unexercised — the same discovery-based anti-rot shape the Postgres suite uses.
    ///
    /// <para>#1640 added 16 arms per dialect in <c>group-aggregation/</c>; the rest live in
    /// <c>dialect-parity/</c>. Discovery below deliberately scans
    /// <em>every</em> category: an arm added elsewhere must show up as a count mismatch here, not
    /// disappear because this suite only looked in one directory.</para>
    /// </summary>
    private static readonly (string Name, ISqlDialect Dialect, int Expected)[] Dialects =
    [
        ("mssql", MssqlDialect.Instance, 35),
        ("mysql", MySqlDialect.Instance, 36),
        ("sqlite", SqliteDialect.Instance, 35)
    ];

    /// <summary>
    /// Deliberately empty, and kept rather than deleted so the shape of the guard matches the
    /// Postgres suite's: every non-Postgres arm on disk is currently compiled end to end. The one
    /// fixture the Postgres suite must exclude (<c>sort-paging/012</c>, blocked on the adapter's
    /// missing <c>exportScope</c>) carries no non-Postgres arm, so it never reaches here. An entry
    /// added later must state its reason, exactly as that one does.
    /// </summary>
    private static readonly Dictionary<string, string> ExcludedArms = new(StringComparer.Ordinal);

    public static TheoryData<string, string, string> Arms
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var (name, _, _) in Dialects)
            {
                foreach (var (category, fixtureDir) in Discover(name))
                {
                    data.Add(name, category, fixtureDir);
                }
            }

            return data;
        }
    }

    /// <summary>Every fixture on disk, in any category, carrying an arm for
    /// <paramref name="dialect"/> and not excluded by name — in ordinal category/name order.</summary>
    private static IReadOnlyList<(string Category, string Fixture)> Discover(string dialect) =>
    [
        .. Directory.EnumerateDirectories(RepoPaths.Fixture())
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .SelectMany(category => Directory
                .EnumerateDirectories(RepoPaths.Fixture(category))
                .Select(path => Path.GetFileName(path)!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(fixture => (Category: category, Fixture: fixture)))
            .Where(entry => File.Exists(
                RepoPaths.Fixture(entry.Category, entry.Fixture, "expected", $"{dialect}.artifact.json")))
            .Where(entry => !ExcludedArms.ContainsKey($"{entry.Category}/{entry.Fixture}/{dialect}"))
    ];

    private static ISqlDialect DialectNamed(string name) =>
        Dialects.Single(entry => entry.Name == name).Dialect;

    // ---- the conformance assertion ---------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Arms))]
    public void Compiles_To_The_Sql_And_Params_Its_Own_Artifact_Pins(string dialect, string category, string fixtureDir)
    {
        var actual = FixtureCompiler.Compile(category, fixtureDir, DialectNamed(dialect));
        using var expected = JsonDocument.Parse(
            FixtureCompiler.Read(category, fixtureDir, "expected", $"{dialect}.artifact.json"));

        Assert.Equal(expected.RootElement.GetProperty("dialect").GetString(), actual.Dialect);
        Assert.Equal(expected.RootElement.GetProperty("sql").GetString(), actual.Sql);
        Assert.Equal(
            FixtureCompiler.CanonicalJson(expected.RootElement.GetProperty("params")),
            FixtureCompiler.CanonicalJson(actual.Parameters));
    }

    /// <summary>
    /// SEC-2's structural invariant, generalised past Postgres: the statement's placeholders and the
    /// parameter list are the same length.
    ///
    /// <para>Only SQL Server can keep the Postgres suite's stronger form. A <c>@pN</c> placeholder
    /// carries its ordinal, so <c>@p1..@pN</c> can be asserted ascending in first-appearance order;
    /// a MySQL/SQLite <c>?</c> carries no ordinal at all — position in the text <em>is</em> the
    /// binding — so there is nothing to compare and the check is necessarily count-only. That is not
    /// a weaker test by accident but the actual difference between a named and a positional protocol,
    /// and it is why <c>params</c> ordering on those two dialects is guarded by the artifact
    /// comparison above rather than by arithmetic here.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Arms))]
    public void Binds_Exactly_As_Many_Parameters_As_The_Statement_Has_Placeholders(string dialect, string category, string fixtureDir)
    {
        var actual = FixtureCompiler.Compile(category, fixtureDir, DialectNamed(dialect));

        if (dialect == "mssql")
        {
            var ordinals = NamedPlaceholder.Matches(actual.Sql)
                .Select(match => int.Parse(match.Groups[1].Value))
                .ToList();

            Assert.Equal(actual.Parameters.Count, ordinals.Count);
            Assert.Equal(Enumerable.Range(1, actual.Parameters.Count), ordinals);
            return;
        }

        Assert.Equal(actual.Parameters.Count, PositionalPlaceholder.Matches(actual.Sql).Count);
    }

    private static readonly Regex NamedPlaceholder = new(@"@p([0-9]+)", RegexOptions.Compiled);

    /// <summary>
    /// A bare <c>?</c> is also SQLite's GLOB single-character wildcard, so counting every <c>?</c>
    /// could over-count on a case-sensitive substring match — <c>dialect-parity/021</c>'s sqlite arm
    /// reaches that path for real. It stays safe for two independent reasons, and the delimiter
    /// lookarounds encode the second: the class-escaped form (<c>[?]</c>) lives in the bound
    /// <em>parameter</em>, never in the SQL text, and a GLOB anchor is a single-quoted literal
    /// (<c>'*'</c>) which cannot be preceded by a space the way every placeholder is.
    /// </summary>
    private static readonly Regex PositionalPlaceholder = new(@"(?<=[\s(])\?(?=[\s,)]|$)", RegexOptions.Compiled);

    // ---- anti-rot guards -------------------------------------------------------------------------

    [Fact]
    public void Each_Dialect_Contributes_Exactly_The_Arm_Count_It_Declares()
    {
        var actual = Dialects.ToDictionary(entry => entry.Name, entry => Discover(entry.Name).Count);
        var declared = Dialects.ToDictionary(entry => entry.Name, entry => entry.Expected);

        Assert.Equal(declared, actual);
    }

    [Fact]
    public void The_Suite_Is_All_106_Non_Postgres_Arms_In_The_Fixture_Tree()
    {
        // #1639 left 55 (19 re-authored + 30 from promoting `011`-`020` to full parity coverage +
        // 3 for `021`, the case-sensitive substring case, + 3 for `022`, T-SQL's `[` wildcard).
        // #1640 added 51: three dialects for each of `group-aggregation/001`-`015`, `020` and
        // `021` (the last added in #1640's review pass, pinning AG-6's auto-group-column colId),
        // which is what makes BM-2's "identical grouping protocol across all four dialects"
        // checkable at all. This number going up without the per-dialect table above changing with
        // it means an arm was added and went unexercised.
        Assert.Equal(106, Arms.Count);
    }

    [Fact]
    public void Every_Non_Postgres_Sql_Artifact_On_Disk_Belongs_To_A_Dialect_This_Suite_Runs()
    {
        // The reverse direction of the count guard: catches an arm named for a dialect nobody
        // implements (a typo'd filename, or #1642's Oracle landing artifacts before its dialect).
        var known = Dialects.Select(entry => entry.Name).Append("postgres").ToHashSet(StringComparer.Ordinal);

        var unknown = Directory
            .EnumerateFiles(RepoPaths.Fixture(), "*.artifact.json", SearchOption.AllDirectories)
            .Select(path => Path.GetFileName(path)!)
            .Select(name => name[..name.IndexOf('.', StringComparison.Ordinal)])
            .Where(name => name is not ("mongo" or "search"))
            .Where(name => !known.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(unknown);
    }

    [Fact]
    public void Every_Excluded_Arm_Still_Exists_On_Disk()
    {
        // An exclusion that no longer matches a real artifact is dead weight that hides whether the
        // case was renamed (and is now silently in scope) or deleted.
        foreach (var (path, reason) in ExcludedArms)
        {
            var segments = path.Split('/');
            Assert.True(
                File.Exists(RepoPaths.Fixture(segments[0], segments[1], "expected", $"{segments[2]}.artifact.json")),
                $"Excluded arm '{path}' no longer exists. Reason recorded for the exclusion: {reason}");
        }
    }

    /// <summary>
    /// SQL-1 made checkable on the arms this story re-authored: every artifact quotes its table and
    /// its columns, so none of the 49 can silently regress to the pre-#1639 bare form. Deliberately
    /// asserted on the <em>file</em> rather than on compiler output — that is where the regression
    /// would live, and the compiler is what this suite is trying to grade.
    /// </summary>
    [Theory]
    [MemberData(nameof(Arms))]
    public void Every_Arm_Quotes_Its_Identifiers_With_Its_Own_Engines_Character(string dialect, string category, string fixtureDir)
    {
        using var artifact = JsonDocument.Parse(
            FixtureCompiler.Read(category, fixtureDir, "expected", $"{dialect}.artifact.json"));
        var sql = artifact.RootElement.GetProperty("sql").GetString()!;

        using var schema = JsonDocument.Parse(FixtureCompiler.Read(category, fixtureDir, "schema.json"));
        var source = schema.RootElement.GetProperty("source").GetString()!;

        var (open, close) = dialect switch
        {
            "mssql" => ("[", "]"),
            "mysql" => ("`", "`"),
            _ => ("\"", "\"")
        };

        Assert.Contains($"FROM {open}{source}{close}", sql, StringComparison.Ordinal);

        foreach (var field in schema.RootElement.GetProperty("fields").EnumerateObject())
        {
            var column = field.Value.GetProperty("physical").GetProperty("sql").GetProperty("column").GetString()!;

            // A column the fixture's request never references simply does not appear; what must
            // never appear is an UNQUOTED occurrence of one that does.
            Assert.DoesNotMatch(
                new Regex($@"(?<!{Regex.Escape(open)})\b{Regex.Escape(column)}\b(?!{Regex.Escape(close)})"),
                sql);
        }
    }
}
