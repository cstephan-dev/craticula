using System.Text.Json;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Fixture-driven coverage against the real <c>spec/fixtures/date-presets/</c> suite (26 cases:
/// AG Grid's 22 presets in 001-022, plus the four SEM-4 field-type/zone/DST axis-variation cases in
/// 023-026).
///
/// <para>Each case is run the way the conformance harness must: read the fixture's own
/// <c>requestContext.now</c> and pin the injected clock to it (<see cref="FixedClock"/>), load the
/// fixture's own <c>schema.json</c> through <see cref="GridSchema.Parse"/> so the bounds get typed
/// from real declared field types, then compare the parsed IR bounds against the fixture's own
/// <c>expected/postgres.artifact.json</c> parameters. Using the artifact as the oracle — rather than a
/// second copy of the expected dates inlined here — is what makes this a conformance test: if the
/// hand-authored fixtures and this implementation ever disagree, the assertion fails instead of both
/// drifting together.</para>
/// </summary>
public class AgGridRequestParserDatePresetsFixtureTests
{
    private const string Category = "date-presets";

    /// <summary>Every case directory, and the preset each one exercises.</summary>
    public static readonly TheoryData<string, string> AllDatePresetFixtures = new()
    {
        { "001-today", "today" },
        { "002-yesterday", "yesterday" },
        { "003-tomorrow", "tomorrow" },
        { "004-this-week", "thisWeek" },
        { "005-last-week", "lastWeek" },
        { "006-next-week", "nextWeek" },
        { "007-this-month", "thisMonth" },
        { "008-last-month", "lastMonth" },
        { "009-next-month", "nextMonth" },
        { "010-this-quarter", "thisQuarter" },
        { "011-last-quarter", "lastQuarter" },
        { "012-next-quarter", "nextQuarter" },
        { "013-this-year", "thisYear" },
        { "014-last-year", "lastYear" },
        { "015-next-year", "nextYear" },
        { "016-year-to-date", "yearToDate" },
        { "017-last-7-days", "last7Days" },
        { "018-last-30-days", "last30Days" },
        { "019-last-90-days", "last90Days" },
        { "020-last-6-months", "last6Months" },
        { "021-last-12-months", "last12Months" },
        { "022-last-24-months", "last24Months" },
        { "023-today-datetime-field-utc", "today" },
        { "024-today-datetime-field-paris-zone", "today" },
        { "025-this-week-date-field-paris-zone-day-shift", "thisWeek" },
        { "026-this-week-datetime-field-paris-dst-spring-forward", "thisWeek" }
    };

    private static string Read(string fixtureDir, params string[] rest) =>
        File.ReadAllText(RepoPaths.Fixture([Category, fixtureDir, .. rest]));

    /// <summary>
    /// Parses a fixture exactly as the conformance harness must: the fixture's own pinned clock and
    /// its own GridSchema, with everything else left at the adapter's defaults (Monday week start,
    /// UTC fallback zone — every case declares its zone explicitly anyway).
    /// </summary>
    private static ComparisonNode ParseFixture(string fixtureDir)
    {
        var parsed = AgGridRequestParser.Parse(Read(fixtureDir, "agGridRequest.json"), OptionsFor(fixtureDir));

        return Assert.IsType<ComparisonNode>(parsed.NativeFilter);
    }

    /// <summary>The harness setup a port must reproduce: the fixture's own pinned <c>now</c> driving
    /// the injected clock, and its own <c>schema.json</c>. Shared by the artifact-driven cases and the
    /// zone-rejection cases so both are parsed identically.</summary>
    private static AgGridParseOptions OptionsFor(string fixtureDir)
    {
        using var request = JsonDocument.Parse(Read(fixtureDir, "agGridRequest.json"));
        var pinnedNow = request.RootElement.GetProperty("requestContext").GetProperty("now").GetString()!;

        return new AgGridParseOptions
        {
            Clock = FixedClock.At(pinnedNow),
            Schema = GridSchema.Parse(Read(fixtureDir, "schema.json"))
        };
    }

    /// <summary>The two bound literals from a fixture's Postgres artifact: <c>params</c> is
    /// <c>[from, to, limit, offset]</c> across this whole category.</summary>
    private static (string From, string To) ExpectedBounds(string fixtureDir)
    {
        using var artifact = JsonDocument.Parse(Read(fixtureDir, "expected", "postgres.artifact.json"));
        var parameters = artifact.RootElement.GetProperty("params");

        return (parameters[0].GetString()!, parameters[1].GetString()!);
    }

    private static string BoundText(TypedValue? bound) => bound switch
    {
        DateValue date => date.Value,
        DatetimeValue datetime => datetime.Value,
        _ => throw new Xunit.Sdk.XunitException($"A preset bound must be a dateValue or a datetimeValue, got {bound?.GetType().Name ?? "null"}.")
    };

    [Theory]
    [MemberData(nameof(AllDatePresetFixtures))]
    public void Every_Fixture_Resolves_To_The_Half_Open_Range_Its_Own_Artifact_Pins(string fixtureDir, string preset)
    {
        var comparison = ParseFixture(fixtureDir);
        var (expectedFrom, expectedTo) = ExpectedBounds(fixtureDir);

        Assert.Equal(ComparisonOperator.InRange, comparison.Op);
        Assert.True(comparison.FromInclusive, $"{fixtureDir}: AG Grid's preset spec table is half-open [from, to).");
        Assert.False(comparison.ToInclusive, $"{fixtureDir}: AG Grid's preset spec table is half-open [from, to).");
        Assert.Equal(expectedFrom, BoundText(comparison.From));
        Assert.Equal(expectedTo, BoundText(comparison.To));

        // ...and the fixture really does exercise the preset the table above claims it does.
        using var request = JsonDocument.Parse(Read(fixtureDir, "agGridRequest.json"));
        var wireType = request.RootElement
            .GetProperty("filterModel").EnumerateObject().Single()
            .Value.GetProperty("type").GetString();
        Assert.Equal(preset, wireType);
    }

    [Theory]
    [MemberData(nameof(AllDatePresetFixtures))]
    public void Every_Fixtures_Bounds_Match_Its_Fields_Declared_Type(string fixtureDir, string preset)
    {
        _ = preset;

        var schema = GridSchema.Parse(Read(fixtureDir, "schema.json"));
        var comparison = ParseFixture(fixtureDir);
        var declaredType = schema.Fields[comparison.Field].Type;

        switch (declaredType)
        {
            case FieldType.Date:
                Assert.IsType<DateValue>(comparison.From);
                Assert.IsType<DateValue>(comparison.To);
                break;

            case FieldType.Datetime:
                // SEM-4: value is an absolute instant; zone is provenance, and must be the zone the
                // request itself declared (never the server's own).
                var from = Assert.IsType<DatetimeValue>(comparison.From);
                using (var request = JsonDocument.Parse(Read(fixtureDir, "agGridRequest.json")))
                {
                    var declaredZone = request.RootElement.GetProperty("requestContext").GetProperty("zone").GetString();
                    Assert.Equal(declaredZone, from.Zone);
                    Assert.Equal(declaredZone, Assert.IsType<DatetimeValue>(comparison.To).Zone);
                }

                Assert.EndsWith("Z", from.Value, StringComparison.Ordinal);
                break;

            default:
                throw new Xunit.Sdk.XunitException($"{fixtureDir}: a date-presets fixture must filter a date/datetime field.");
        }
    }

    [Fact]
    public void The_Category_Covers_Every_Preset_In_AG_Grids_Closed_Vocabulary_Exactly_Once()
    {
        // The point of cases 001-022: one per member, no member forgotten, none duplicated. (023-026
        // reuse `today`/`thisWeek` on purpose, so only the first 22 rows participate here.)
        var covered = AllDatePresetFixtures
            .Select(row => (string)row[1]!)
            .Take(22)
            .ToList();

        Assert.Equal(DatePresetResolver.PresetNames, covered);
    }

    /// <summary>
    /// The category's rejection-shaped cases: a bad <c>requestContext.zone</c> carries
    /// <c>expected/rejection.json</c> instead of backend artifacts, so it cannot ride the
    /// artifact-driven theory above. Both are pure wire-shape properties with host-independent
    /// outcomes — see the category README's "Zone-rejection cases" section.
    /// </summary>
    private static readonly string[] ZoneRejectionFixtureDirs =
    [
        "027-unresolvable-zone-rejected",
        "028-non-iana-zone-alias-rejected",
    ];

    public static TheoryData<string> ZoneRejectionFixtures => [.. ZoneRejectionFixtureDirs];

    [Fact]
    public void The_Fixture_Directories_On_Disk_Match_The_Table_Above()
    {
        // Guards the usual fixture-test rot: a case added to spec/fixtures/ but never wired into
        // either theory would otherwise be silently unexercised.
        var onDisk = Directory
            .EnumerateDirectories(RepoPaths.Fixture(Category))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var declared = AllDatePresetFixtures
            .Select(row => (string?)row[0])
            .Concat(ZoneRejectionFixtureDirs)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(declared, onDisk);
    }

    [Theory]
    [MemberData(nameof(ZoneRejectionFixtures))]
    public void A_Zone_Rejection_Fixture_Is_Rejected_With_The_Reason_Its_Own_Oracle_Pins(string fixtureDir)
    {
        using var oracle = JsonDocument.Parse(Read(fixtureDir, "expected", "rejection.json"));
        var expectedStage = oracle.RootElement.GetProperty("rejectedAt").GetString();
        var expectedReason = oracle.RootElement.GetProperty("reason").GetString();

        // The fixture's own expected/rejection.json is the oracle — not a literal repeated here.
        Assert.Equal("requestParsing", expectedStage);

        var ex = Assert.Throws<AgGridRequestParseException>(
            () => AgGridRequestParser.Parse(Read(fixtureDir, "agGridRequest.json"), OptionsFor(fixtureDir)));

        Assert.Equal(expectedReason, ex.Reason);
    }

    [Theory]
    [MemberData(nameof(AllDatePresetFixtures))]
    public void Every_Fixture_Carries_A_Pinned_Clock_And_Zone_And_The_Standard_Sort_And_Page(string fixtureDir, string preset)
    {
        _ = preset;

        using var request = JsonDocument.Parse(Read(fixtureDir, "agGridRequest.json"));
        var context = request.RootElement.GetProperty("requestContext");

        // Without a pinned `now`, a preset fixture is simply not reproducible by any port.
        Assert.Equal(JsonValueKind.String, context.GetProperty("now").ValueKind);
        Assert.Equal(JsonValueKind.String, context.GetProperty("zone").ValueKind);
        Assert.True(TimeZoneInfo.FindSystemTimeZoneById(context.GetProperty("zone").GetString()!) is not null);

        var parsed = AgGridRequestParser.Parse(
            Read(fixtureDir, "agGridRequest.json"),
            new AgGridParseOptions
            {
                Clock = FixedClock.At(context.GetProperty("now").GetString()!),
                Schema = GridSchema.Parse(Read(fixtureDir, "schema.json"))
            });

        Assert.Null(parsed.CustomFilters);
        Assert.Equal("id", Assert.Single(parsed.Sort!).Field);
        var page = Assert.IsType<RangePage>(parsed.Page);
        Assert.Equal(0, page.StartRow);
        Assert.Equal(100, page.EndRow);
    }
}
