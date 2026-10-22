namespace Craticula.Integration.Tests.Parity;

/// <summary>
/// Offline guards for the pure half of the parity property (PAR-1): the pair finder and the failure report. No engine, no Docker.
/// </summary>
public sealed class ParityReportTests
{
    private static readonly string[] Engines = ["postgres", "mssql", "mysql", "oracle", "sqlite", "mongo", "opensearch", "elasticsearch"];

    private static IReadOnlyList<EngineRun> Runs(Func<string, EngineOutcome> outcome) =>
        [.. Engines.Select(engine => new EngineRun(engine, outcome(engine)))];

    [Fact]
    public void Identical_sequences_have_no_disagreeing_pair_and_do_not_fail()
    {
        var runs = Runs(_ => EngineOutcome.Returned([3, 1, 2]));
        Assert.Empty(ParityComparer.DisagreeingPairs(runs));
        Assert.False(ParityComparer.Fails(new ParityCase(0, "{}", runs)));
    }

    [Fact]
    public void One_odd_engine_disagrees_with_each_of_the_other_seven_and_with_none_of_them_alone()
    {
        var runs = Runs(engine => EngineOutcome.Returned(engine == "mongo" ? [3, 2, 1] : [3, 1, 2]));
        var pairs = ParityComparer.DisagreeingPairs(runs);

        Assert.Equal(7, pairs.Count);
        Assert.All(pairs, pair => Assert.True(pair.First == "mongo" || pair.Second == "mongo"));
        Assert.True(ParityComparer.Fails(new ParityCase(0, "{}", runs)));
    }

    [Fact]
    public void All_28_pairs_are_compared()
    {
        var runs = Runs(engine => EngineOutcome.Returned([Array.IndexOf(Engines, engine)]));
        var pairs = ParityComparer.DisagreeingPairs(runs);

        Assert.Equal(28, pairs.Count);
        Assert.Equal(28, pairs.Select(pair => (pair.First, pair.Second)).Distinct().Count());
    }

    [Theory]
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 1, 2 })]
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 1, 3, 2 })]
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 1, 2, 4 })]
    [InlineData(new long[] { 1 }, new long[] { })]
    public void Value_length_and_order_each_make_two_sequences_differ(long[] left, long[] right)
    {
        var pairs = ParityComparer.DisagreeingPairs(
        [
            new EngineRun("a", EngineOutcome.Returned(left)),
            new EngineRun("b", EngineOutcome.Returned(right))
        ]);

        Assert.Equal(new DisagreeingPair("a", "b"), Assert.Single(pairs));
    }

    [Fact]
    public void A_refusal_fails_the_case_and_pairs_with_every_engine_that_answered()
    {
        var runs = Runs(engine => engine == "sqlite" ? EngineOutcome.Refuse("boom") : EngineOutcome.Returned([1]));

        Assert.Single(ParityComparer.Refusals(runs));
        Assert.Equal(7, ParityComparer.DisagreeingPairs(runs).Count);
        Assert.True(ParityComparer.Fails(new ParityCase(0, "{}", runs)));
    }

    [Fact]
    public void Two_refusals_are_not_a_pair_but_each_is_a_failure()
    {
        var runs = Runs(engine => engine is "sqlite" or "mysql" ? EngineOutcome.Refuse("no") : EngineOutcome.Returned([1]));

        Assert.Equal(2, ParityComparer.Refusals(runs).Count);
        Assert.DoesNotContain(ParityComparer.DisagreeingPairs(runs), pair => pair is { First: "mysql", Second: "sqlite" } or { First: "sqlite", Second: "mysql" });
    }

    [Fact]
    public void The_report_carries_seed_index_request_pairs_every_sequence_and_the_dataset()
    {
        const string request = "{\"startRow\":0,\"endRow\":10,\"filterModel\":{\"qty\":{\"filterType\":\"number\",\"type\":\"equals\",\"filter\":5}}}";
        var runs = Runs(engine => EngineOutcome.Returned(engine == "opensearch" ? [7, 8] : [7, 9]));
        var report = ParityComparer.Report(20261010, 150, 200, [new ParityCase(42, request, runs)], "/tmp/parity-dataset.json");

        Assert.Contains("seed 20261010", report);
        Assert.Contains("index 42", report);
        Assert.Contains("1 of 200", report);
        Assert.Contains(request, report);
        Assert.Contains("postgres != opensearch", report);
        foreach (var engine in Engines)
        {
            Assert.Contains(engine, report);
        }

        Assert.Contains("[7, 8]", report);
        Assert.Contains("[7, 9]", report);
        Assert.Contains("ParityRequestGenerator.Generate(20261010, 42)", report);
        Assert.Contains("ParityDataset.Generate(20261010, 150)", report);
        Assert.Contains("/tmp/parity-dataset.json", report);
    }

    [Fact]
    public void The_report_prints_at_most_the_cap_and_counts_the_rest()
    {
        var runs = Runs(engine => EngineOutcome.Returned(engine == "mongo" ? [2] : [1]));
        var failures = Enumerable.Range(0, 25).Select(index => new ParityCase(index, $"{{\"case\":{index}}}", runs)).ToList();
        var report = ParityComparer.Report(1, 10, 200, failures, null);

        Assert.Contains("25 of 200", report);
        Assert.Contains("=== case 19 ", report);
        Assert.DoesNotContain("=== case 20 ", report);
        Assert.Contains("5 more failing case(s) not printed", report);
        Assert.Contains("20, 21, 22, 23, 24", report);
    }

    [Fact]
    public void The_configuration_defaults_and_rejects_garbage()
    {
        var defaults = ParityConfig.FromEnvironment(_ => null);
        Assert.Equal(new ParityConfig(20261010, 200, 300), defaults);

        var set = ParityConfig.FromEnvironment(name => name switch
        {
            ParityConfig.SeedVariable => "-7",
            ParityConfig.CasesVariable => "5",
            ParityConfig.RowsVariable => "40",
            _ => null
        });
        Assert.Equal(new ParityConfig(-7, 5, 40), set);

        Assert.Throws<InvalidOperationException>(() => ParityConfig.FromEnvironment(name => name == ParityConfig.CasesVariable ? "0" : null));
        Assert.Throws<InvalidOperationException>(() => ParityConfig.FromEnvironment(name => name == ParityConfig.SeedVariable ? "abc" : null));
    }
}
