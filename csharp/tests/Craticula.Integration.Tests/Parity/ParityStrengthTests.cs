using Xunit.Abstractions;

namespace Craticula.Integration.Tests.Parity;

/// <summary>SQLite's in-memory database is one shared name per process: the strength measurement runs alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ParityStrengthCollection
{
    public const string Name = "Parity: strength on in-process SQLite";
}

/// <summary>
/// Offline guards for how hard the parity property looks (T-3, #2270). They run the generated requests on in-process
/// SQLite, no Docker, and fail when the default and a few other seeds stop exercising what the property exists to check.
/// </summary>
[Collection(ParityStrengthCollection.Name)]
public sealed class ParityStrengthTests(ITestOutputHelper output)
{
    private const int Rows = ParityConfig.DefaultRows;

    [Fact]
    public async Task The_default_run_compares_rows_in_nearly_every_case()
    {
        var report = await Measure(ParityConfig.DefaultSeed, ParityConfig.DefaultCases);

        // 85 %: the tuned generator measures 88-92 % on every seed tried; the untuned one was 63 %.
        Assert.True(report.NonEmptyShare >= 0.85, $"only {report.NonEmptyShare:P1} of the cases returned rows");
    }

    [Theory]
    [InlineData(ParityConfig.DefaultSeed)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(424242)]
    [InlineData(-9)]
    public async Task A_thousand_cases_exercise_rows_ties_nulls_and_windows(long seed)
    {
        var report = await Measure(seed, 1000);

        Assert.True(report.NonEmptyShare >= 0.85, $"rows returned in {report.NonEmptyShare:P1}");
        Assert.True(report.MeanIdsPerNonEmpty >= 10, $"{report.MeanIdsPerNonEmpty:F1} ids per non-empty case");

        // Every operator family returns rows in most of its cases, so none is compared on empty results alone.
        foreach (var (family, (cases, nonEmpty)) in report.Families)
        {
            Assert.True(cases >= 30, $"{family} appears in only {cases} cases");
            Assert.True(nonEmpty >= cases * 7 / 10, $"{family} returns rows in {nonEmpty} of {cases} cases");
        }

        // The tiebreaker (SEM-6): most multi-key sorts tie on the first key somewhere in the result.
        Assert.True(report.MultiKeyCases >= 300, $"{report.MultiKeyCases} multi-key sorts");
        Assert.True(report.TieShare >= 0.7, $"first-key ties in {report.TieShare:P1} of the multi-key sorts");

        // NULL placement (SEM-3): the first sort key holds NULLs next to values in many results.
        Assert.True(report.NullOrderCases >= 300, $"{report.NullOrderCases} results with nulls in the first sort key");

        // PAR-1 "window included": a window that starts inside the result and one that stops short of its end.
        Assert.True(report.MidCutShare >= 0.3, $"windows cut mid-result in {report.MidCutShare:P1}");
        Assert.True(report.WindowTruncates >= 400, $"{report.WindowTruncates} windows truncate the result");
    }

    [Fact]
    public async Task The_measurement_is_deterministic_per_seed()
    {
        var first = await Measure(ParityConfig.DefaultSeed, 100);
        var second = await Measure(ParityConfig.DefaultSeed, 100);

        Assert.Equal(first.Summary(), second.Summary());
    }

    [Fact]
    public void The_shares_are_counted_over_the_cases_they_name()
    {
        var empty = new Dictionary<OperatorFamily, (int, int)>();
        var report = new ParityStrengthReport(10, 8, 40, empty, 4, 3, 2, 5, 4, 3, 1);

        Assert.Equal(0.8, report.NonEmptyShare, 6);
        Assert.Equal(5.0, report.MeanIdsPerNonEmpty, 6);
        Assert.Equal(0.75, report.TieShare, 6);
        Assert.Equal(0.4, report.MidCutShare, 6);
        Assert.Equal(0, new ParityStrengthReport(0, 0, 0, empty, 0, 0, 0, 0, 0, 0, 0).NonEmptyShare);
    }

    private async Task<ParityStrengthReport> Measure(long seed, int cases)
    {
        var report = await ParityStrength.MeasureAsync(seed, cases, Rows);
        output.WriteLine($"seed {seed}: {report.Summary()}");
        return report;
    }
}
