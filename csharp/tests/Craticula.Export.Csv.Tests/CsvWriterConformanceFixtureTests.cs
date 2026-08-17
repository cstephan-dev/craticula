using System.Text.Json;
using Craticula.AgGrid;
using Craticula.Core.Export;

namespace Craticula.Export.Csv.Tests;

/// <summary>
/// EXP-8's tier for the writer: for every fixture that carries a row stream, run the real path a
/// host runs — <see cref="AgGridRequestParser.Parse"/> for the column list and its headers, the
/// fixture's own <c>rowStream.json</c> for the rows, its own <c>writerConfig.json</c> (or the
/// writer's defaults, when it declares none) — and compare the emitted <b>bytes</b> against that
/// fixture's own <c>expected/export.csv</c>/<c>.tsv</c>.
///
/// <para><b>Bytes, never text.</b> The BOM, the CRLF record separator and the UTF-8 encoding are the
/// substance of EXP-4, and every one of them survives a string comparison: .NET, Python and every
/// editor swallow a leading U+FEFF, and <c>File.ReadAllText</c> normalizes nothing but hides the
/// difference between what was decoded and what was on disk. A test comparing strings would pass on
/// a missing BOM, which is the single most likely regression in this module.</para>
///
/// <para><b>The expected files are hand-authored (#1651), like every artifact in this suite.</b> They
/// were written record by record from EXP-4's rules, not captured from this writer's output, so a
/// disagreement is informative in both directions and must be investigated rather than resolved by
/// regenerating the file.</para>
/// </summary>
public class CsvWriterConformanceFixtureTests
{
    /// <summary>
    /// The categories that carry writer fixtures at all, each declaring how many it contributes — so
    /// a case added later must either be exercised here or be given an explicit exclusion reason.
    /// Only <c>page-export</c> has any: the writer's input is an export, and an export request is
    /// what that category is for.
    ///
    /// <para>Five of that category's 25 cases carry a row stream: <c>012</c>-<c>015</c> (EXP-4's
    /// delimiter presets and BOM on/off, #1651's DoD) and <c>025</c> (tab, authored by #1651 because
    /// nothing pinned TSV output bytes). The other six writer-documenting cases — <c>016</c>-<c>020</c>
    /// and <c>022</c> — are EXP-6's hostile values and stay prose until #1652 implements the
    /// formula-injection prefixing they document; <c>021</c> is EXP-7's row limit, which is an
    /// execution-adapter concern with no writer output at all.</para>
    /// </summary>
    private static readonly (string Category, int Expected)[] InScopeCategories =
    [
        ("page-export", 5)
    ];

    /// <summary>
    /// Empty, and kept as a named collection rather than deleted, for the same reason the three
    /// compiler runners keep theirs: a fixture is either exercised or excluded FOR A STATED REASON,
    /// and an empty exclusion list is the strongest form of that claim.
    /// </summary>
    private static readonly Dictionary<string, string> ExcludedFixtures = new(StringComparer.Ordinal);

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

    /// <summary>Every case directory in <paramref name="category"/> that carries a row stream — the
    /// presence of the input IS the scoping rule, exactly as the presence of
    /// <c>postgres.artifact.json</c> is for the SQL runner.</summary>
    private static IReadOnlyList<string> DiscoverInScope(string category) =>
    [
        .. Directory.EnumerateDirectories(RepoPaths.Fixture(category))
            .Select(path => Path.GetFileName(path)!)
            .Where(name => File.Exists(RepoPaths.Fixture(category, name, "rowStream.json")))
            .Where(name => !ExcludedFixtures.ContainsKey($"{category}/{name}"))
            .OrderBy(name => name, StringComparer.Ordinal)
    ];

    // ---- the conformance assertion ---------------------------------------------------------------

    [Theory]
    [MemberData(nameof(InScopeFixtures))]
    public async Task Writes_The_Bytes_Its_Own_Expected_Export_File_Pins(string category, string fixtureDir)
    {
        var (spec, options, rows) = Load(category, fixtureDir);

        var actual = await WriteAsync(rows, spec, options);

        Assert.Equal(ExpectedBytes(category, fixtureDir, spec.Format), actual);
    }

    /// <summary>
    /// The synchronous overload emits the identical file. It exists for adapters whose driver only
    /// offers a blocking cursor, and an export that differed by one byte depending on which one the
    /// host called would be the worst kind of divergence — invisible until two hosts compared
    /// downloads.
    /// </summary>
    [Theory]
    [MemberData(nameof(InScopeFixtures))]
    public void Writes_The_Same_Bytes_Through_The_Synchronous_Overload(string category, string fixtureDir)
    {
        var (spec, options, rows) = Load(category, fixtureDir);

        using var destination = new MemoryStream();
        var rowCount = CsvWriter.Write(rows, spec.Columns, options, destination);

        Assert.Equal(ExpectedBytes(category, fixtureDir, spec.Format), destination.ToArray());
        Assert.Equal(rows.Count, rowCount);
    }

    /// <summary>
    /// The row stream declares the columns it carries, and they must be EXP-1(b)'s resolved
    /// projection for this very request. Without this the fixture could drift into hand-picked data:
    /// a stream carrying a column the request never projects, or missing one it does, would still
    /// produce bytes — just not the bytes an adapter executing that fixture's compiled query could
    /// ever have fed the writer.
    /// </summary>
    [Theory]
    [MemberData(nameof(InScopeFixtures))]
    public void The_Row_Stream_Carries_Exactly_The_Columns_The_Export_Resolves(string category, string fixtureDir)
    {
        var schema = WriterFixture.SchemaOf(category, fixtureDir);
        var spec = WriterFixture.ExportSpecOf(category, fixtureDir, schema);

        Assert.Equal(spec.ProjectionColumns, WriterFixture.StreamColumns(category, fixtureDir));
    }

    /// <summary>
    /// EXP-4's BOM toggle, asserted as the relation it actually is rather than twice against two
    /// files: <c>014</c> and <c>015</c> are the same rows and the same configuration but for the
    /// mark, so one file must be the other plus <c>EF BB BF</c>. Comparing each against its own
    /// expected file cannot catch a writer that emits a BOM in the wrong place, or one whose two
    /// paths encode the payload differently.
    /// </summary>
    [Fact]
    public void The_Bom_Fixture_Is_Its_Bom_Off_Sibling_Prefixed_By_Exactly_Three_Bytes()
    {
        var withBom = File.ReadAllBytes(
            RepoPaths.Fixture("page-export", "014-csv-writer-bom-on-default", "expected", "export.csv"));
        var withoutBom = File.ReadAllBytes(
            RepoPaths.Fixture("page-export", "015-csv-writer-bom-off-configured", "expected", "export.csv"));

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, withBom.Take(3).ToArray());
        Assert.Equal(withoutBom, withBom.Skip(3).ToArray());
    }

    /// <summary>
    /// The delimiter pair, asserted as a relation for the same reason: <c>012</c> and <c>013</c> share
    /// a row stream, so their two files must differ — a preset that silently fell back to the default
    /// would still match one of them.
    /// </summary>
    [Fact]
    public void The_Two_Delimiter_Fixtures_Share_A_Row_Stream_And_Differ_In_Their_Output()
    {
        var comma = RepoPaths.Fixture("page-export", "012-csv-writer-delimiter-comma-default");
        var semicolon = RepoPaths.Fixture("page-export", "013-csv-writer-delimiter-semicolon-preset");

        Assert.Equal(
            File.ReadAllBytes(Path.Combine(comma, "rowStream.json")),
            File.ReadAllBytes(Path.Combine(semicolon, "rowStream.json")));

        Assert.NotEqual(
            File.ReadAllBytes(Path.Combine(comma, "expected", "export.csv")),
            File.ReadAllBytes(Path.Combine(semicolon, "expected", "export.csv")));
    }

    /// <summary>
    /// The format pair, and the strongest of the three sibling relations: <c>012</c> and <c>025</c> are
    /// the SAME wire request but for <c>exportFormat</c>, they compile to a byte-identical artifact,
    /// and the files they produce are different bytes under a different extension. That is the whole
    /// of what <c>exportFormat</c> does, stated as one assertion.
    ///
    /// <para><b>It is asserted here because nothing else in the suite can assert it.</b>
    /// <c>page-export/004</c> and <c>/005</c> are often described as the format-isolation pair and are
    /// NOT: their requests differ in <c>filterModel</c> and <c>sortModel</c> as well, so their
    /// artifacts differ for reasons that have nothing to do with the format, and a compiler that read
    /// <c>exportFormat</c> would still pass them. <c>PostgresConformanceFixtureTests</c> states the
    /// artifact half of this relation in prose; here it is executable.</para>
    ///
    /// <para>Note what is deliberately NOT claimed: the two <em>row streams</em> differ (025 carries a
    /// tab-bearing value, which is the point of a TSV oracle), so "the outputs differ" is not by
    /// itself the delimiter's doing. The load-bearing half is the artifact identity above a request
    /// difference — the writer's own delimiter switch is pinned in bytes by 025's expected file and as
    /// a rule by <c>CsvWriterTests</c>.</para>
    /// </summary>
    [Fact]
    public void The_Tsv_Fixture_Is_The_Comma_Fixtures_Request_With_Only_Its_ExportFormat_Changed()
    {
        var csv = RepoPaths.Fixture("page-export", "012-csv-writer-delimiter-comma-default");
        var tsv = RepoPaths.Fixture("page-export", "025-tsv-writer-tab-delimited");

        var differing = DifferingMembers(
            Path.Combine(csv, "agGridRequest.json"), Path.Combine(tsv, "agGridRequest.json"));

        Assert.Equal(["exportFormat"], differing.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("\"csv\"", "\"tsv\""), differing["exportFormat"]);

        Assert.Equal(
            File.ReadAllBytes(Path.Combine(csv, "expected", "postgres.artifact.json")),
            File.ReadAllBytes(Path.Combine(tsv, "expected", "postgres.artifact.json")));

        Assert.NotEqual(
            File.ReadAllBytes(Path.Combine(csv, "expected", "export.csv")),
            File.ReadAllBytes(Path.Combine(tsv, "expected", "export.tsv")));
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
    public void The_Writer_Suite_Is_All_5_Fixtures_That_Carry_A_Row_Stream()
    {
        // 5 with NOTHING subtracted. The number moves when #1652 gives 016-020/022 their own row
        // streams and expected files (six more) — that story's DoD is exactly this count going to 11.
        Assert.Equal(5, InScopeFixtures.Count);
        Assert.Empty(ExcludedFixtures);
    }

    /// <summary>
    /// The reconciliation the three compiler runners each do for their own artifact kind, done here
    /// for the kind #1651 introduced. Two directions, because neither closes the loop alone: an
    /// <c>expected/export.*</c> file with no row stream beside it would never be compared (nothing
    /// discovers it), and a <c>rowStream.json</c> under a category this suite does not scope would
    /// leave every count above untouched while going unexercised.
    /// </summary>
    [Fact]
    public void Every_Row_Stream_And_Expected_Export_File_On_Disk_Is_Exercised()
    {
        var rowStreams = Directory
            .EnumerateFiles(RepoPaths.Fixture(), "rowStream.json", SearchOption.AllDirectories)
            .Select(path => Path.GetDirectoryName(path)!)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var expectedOutputs = Directory
            .EnumerateFiles(RepoPaths.Fixture(), "export.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".csv" or ".tsv")
            .Select(path => Path.GetDirectoryName(Path.GetDirectoryName(path))!)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(rowStreams, expectedOutputs);
        Assert.Equal(InScopeFixtures.Count, rowStreams.Count);
    }

    /// <summary>
    /// A <c>writerConfig.json</c> is only meaningful beside a row stream — one sitting alone would be
    /// configuration nothing applies, which is how a fixture silently stops testing what its
    /// <c>description.md</c> says it tests.
    /// </summary>
    [Fact]
    public void Every_Writer_Config_On_Disk_Sits_Beside_A_Row_Stream()
    {
        var orphans = Directory
            .EnumerateFiles(RepoPaths.Fixture(), "writerConfig.json", SearchOption.AllDirectories)
            .Where(path => !File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "rowStream.json")))
            .ToList();

        Assert.Empty(orphans);
    }

    /// <summary>
    /// The expected file's extension is the request's own <c>exportFormat</c>. It is the one place
    /// the wire's format field is visible in the fixture tree at all — <c>page-export/004</c>/<c>005</c>
    /// prove it reaches no compiled artifact — so letting a <c>.csv</c> file sit beside a
    /// <c>"tsv"</c> request would hide the only effect that field has.
    /// </summary>
    [Theory]
    [MemberData(nameof(InScopeFixtures))]
    public void The_Expected_Files_Extension_Is_The_Requests_Export_Format(string category, string fixtureDir)
    {
        var schema = WriterFixture.SchemaOf(category, fixtureDir);
        var spec = WriterFixture.ExportSpecOf(category, fixtureDir, schema);

        Assert.True(File.Exists(ExpectedPath(category, fixtureDir, spec.Format)));
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

    // ---- the harness ------------------------------------------------------------------------------

    private static (ExportSpec Spec, CsvWriterOptions Options, IReadOnlyList<ExportRow> Rows) Load(
        string category, string fixtureDir)
    {
        var schema = WriterFixture.SchemaOf(category, fixtureDir);
        var spec = WriterFixture.ExportSpecOf(category, fixtureDir, schema);
        var options = WriterFixture.OptionsOf(category, fixtureDir, spec.Format);

        return (spec, options, WriterFixture.Rows(category, fixtureDir, schema, spec));
    }

    private static async Task<byte[]> WriteAsync(
        IReadOnlyList<ExportRow> rows, ExportSpec spec, CsvWriterOptions options)
    {
        using var destination = new MemoryStream();
        var written = await CsvWriter.WriteAsync(AsAsync(rows), spec.Columns, options, destination);

        Assert.Equal(rows.Count, written);
        return destination.ToArray();
    }

    private static async IAsyncEnumerable<ExportRow> AsAsync(IEnumerable<ExportRow> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
            await Task.Yield();
        }
    }

    /// <summary>The top-level members of two request documents that are not written identically, as
    /// <c>name -> (left, right)</c>. Raw text rather than a semantic comparison: these files are
    /// hand-authored siblings formatted the same way, so "written identically" is the property worth
    /// asserting — a reformatted member is a difference a reader would have to explain.</summary>
    private static Dictionary<string, (string Left, string Right)> DifferingMembers(
        string leftPath, string rightPath)
    {
        using var left = JsonDocument.Parse(File.ReadAllText(leftPath));
        using var right = JsonDocument.Parse(File.ReadAllText(rightPath));

        var names = left.RootElement.EnumerateObject().Select(member => member.Name)
            .Concat(right.RootElement.EnumerateObject().Select(member => member.Name))
            .Distinct(StringComparer.Ordinal);

        var differing = new Dictionary<string, (string Left, string Right)>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var leftText = RawMember(left.RootElement, name);
            var rightText = RawMember(right.RootElement, name);
            if (!string.Equals(leftText, rightText, StringComparison.Ordinal))
            {
                differing[name] = (leftText, rightText);
            }
        }

        return differing;
    }

    private static string RawMember(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetRawText() : "<absent>";

    private static byte[] ExpectedBytes(string category, string fixtureDir, ExportFormat format) =>
        File.ReadAllBytes(ExpectedPath(category, fixtureDir, format));

    private static string ExpectedPath(string category, string fixtureDir, ExportFormat format) =>
        RepoPaths.Fixture(category, fixtureDir, "expected", format == ExportFormat.Tsv ? "export.tsv" : "export.csv");
}
