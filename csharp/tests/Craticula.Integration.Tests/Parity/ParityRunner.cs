using System.Diagnostics;
using System.Text.Json;
using Craticula.Core.Schema;
using Craticula.Integration.Tests.Containers.Document;
using Craticula.Integration.Tests.Containers.Sql;
using Craticula.Integration.Tests.Docker;
using Craticula.Integration.Tests.Oracle;
using Craticula.Integration.Tests.Parity;
using Craticula.Sql;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit.Abstractions;

namespace Craticula.Integration.Tests.Containers.Parity;

/// <summary>
/// One engine of the parity property (T-3, PAR-1): seeded once with the parity dataset, then asked for the stableKey
/// sequence of each generated request. Thin adapters over the execution-equality harnesses' own seed and run halves, so
/// the DDL, mapping and result reading are exactly the tier's.
/// </summary>
internal abstract class ParityEngine
{
    public abstract string Name { get; }

    /// <summary>Starts the container (the SQL engines start lazily inside their first seed).</summary>
    public virtual Task StartAsync() => Task.CompletedTask;

    public abstract Task SeedAsync(ExecutionFixture seed);

    /// <summary>The ids of the rows the request returns, in order. Throws when the engine or its compiler refuses.</summary>
    public abstract Task<IReadOnlyList<long>> QueryAsync(ExecutionFixture request);

    public abstract Task DisposeAsync();

    protected static IReadOnlyList<long> IdsOf(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var at = -1;
        for (var index = 0; index < columns.Count; index++)
        {
            if (string.Equals(columns[index], "id", StringComparison.OrdinalIgnoreCase))
            {
                at = index;
            }
        }

        if (at < 0)
        {
            throw new InvalidOperationException($"the result has no id column: [{string.Join(", ", columns)}]");
        }

        return [.. rows.Select(row => Convert.ToInt64(row[at] ?? throw new InvalidOperationException("a result row has a null id"), System.Globalization.CultureInfo.InvariantCulture))];
    }
}

internal sealed class SqlParityEngine(SqlEngine engine, string name) : ParityEngine
{
    public override string Name => name;

    public override Task SeedAsync(ExecutionFixture seed) => engine.SeedAsync(seed);

    public override async Task<IReadOnlyList<long>> QueryAsync(ExecutionFixture request)
    {
        var query = SqlFixtureCompiler.Compile(request.Request.GetRawText(), request.Schema, engine.Dialect);
        var result = await engine.QueryAsync(query).ConfigureAwait(false);
        return IdsOf(result.Columns, result.Rows);
    }

    public override Task DisposeAsync() => ((IAsyncLifetime)engine).DisposeAsync();
}

internal sealed class MongoParityEngine(MongoEngineFixture engine) : ParityEngine
{
    private IMongoCollection<BsonDocument>? collection;

    public override string Name => "mongo";

    public override Task StartAsync() => engine.InitializeAsync();

    public override async Task SeedAsync(ExecutionFixture seed) =>
        collection = await MongoEngine.SeedAsync(engine.Require(), "parity_" + Guid.NewGuid().ToString("N"), seed).ConfigureAwait(false);

    public override async Task<IReadOnlyList<long>> QueryAsync(ExecutionFixture request)
    {
        var rows = await MongoEngine.QueryAsync(collection!, request).ConfigureAwait(false);
        return IdsOf(rows.Columns, rows.Rows);
    }

    public override Task DisposeAsync() => engine.DisposeAsync();
}

internal sealed class SearchParityEngine(SearchEngineFixture engine) : ParityEngine
{
    private readonly string index = "parity_" + Guid.NewGuid().ToString("N");

    public override string Name => engine.EngineName;

    public override Task StartAsync() => engine.InitializeAsync();

    public override Task SeedAsync(ExecutionFixture seed) => SearchEngine.SeedAsync(engine.Require(), index, seed);

    public override async Task<IReadOnlyList<long>> QueryAsync(ExecutionFixture request)
    {
        var rows = await SearchEngine.QueryAsync(engine.Require(), index, request).ConfigureAwait(false);
        return IdsOf(rows.Columns, rows.Rows);
    }

    public override Task DisposeAsync() => engine.DisposeAsync();
}

/// <summary>
/// The seven engines, started and seeded once per run (one table, collection or index from the parity schema and dataset;
/// nothing is recreated per case). Oracle is excluded (PAR-1's engine list). Containers start concurrently; the elapsed
/// time of start plus seed is kept apart from the case loop's.
/// </summary>
public sealed class ParityEngines : IAsyncLifetime
{
    internal ParityConfig Config { get; private set; } = ParityConfig.FromEnvironment();

    internal IReadOnlyList<ParityEngine> Engines { get; private set; } = [];

    internal ExecutionFixture Seed { get; private set; } = null!;

    public string? DatasetPath { get; private set; }

    public TimeSpan StartAndSeed { get; private set; }

    public string? Failure { get; private set; }

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        try
        {
            Config = ParityConfig.FromEnvironment();
            var dataset = ParityDataset.Generate(Config.Seed, Config.Rows);
            var datasetJson = dataset.ToJson();
            DatasetPath = Path.Combine(Path.GetTempPath(), $"craticula-parity-dataset-{Config.Seed}-{Config.Rows}.json");
            await File.WriteAllTextAsync(DatasetPath, datasetJson).ConfigureAwait(false);

            using var document = JsonDocument.Parse(datasetJson);
            Seed = ExecutionFixture.FromMemory("parity-seed", ParitySchema.Create(), "{}", ExecutionFixture.ReadSeed(document.RootElement));

            Engines =
            [
                new SqlParityEngine(new PostgresEngine(), "postgres"),
                new SqlParityEngine(new MssqlEngine(), "mssql"),
                new SqlParityEngine(new MySqlEngine(), "mysql"),
                new SqlParityEngine(new SqliteEngine(), "sqlite"),
                new MongoParityEngine(new MongoEngineFixture()),
                new SearchParityEngine(new OpenSearchEngineFixture()),
                new SearchParityEngine(new ElasticsearchEngineFixture())
            ];

            await Task.WhenAll(Engines.Select(async engine =>
            {
                await engine.StartAsync().ConfigureAwait(false);
                await engine.SeedAsync(Seed).ConfigureAwait(false);
            })).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Failure = exception.ToString();
        }

        StartAndSeed = clock.Elapsed;
    }

    public async Task DisposeAsync()
    {
        foreach (var engine in Engines)
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ParityCollection : ICollectionFixture<ParityEngines>
{
    public const string Name = "Parity: seven engines";
}

/// <summary>
/// T-3 (<c>spec/semantics.md</c> PAR-1): for one seeded dataset and N generated requests, every engine returns the same
/// stableKey sequence, window included, on all 21 engine pairs. Configuration and replay: <c>Parity/README.md</c>.
/// </summary>
[Collection(ParityCollection.Name)]
public sealed class ParityPropertyTests(ParityEngines engines, ITestOutputHelper output)
{
    private const int PrintCap = 20;

    [DockerFact]
    public async Task Every_Engine_Returns_The_Same_Id_Sequence_For_Every_Generated_Request()
    {
        Assert.True(engines.Failure is null, $"the engines did not start and seed: {engines.Failure}");

        var config = engines.Config;
        var requests = Enumerable.Range(0, config.Cases).Select(index => ParityRequestGenerator.Generate(config.Seed, index)).ToList();
        var schema = engines.Seed.Schema;

        // Each engine runs every case in order, the engines concurrently: one connection per engine, 7 x N queries in all.
        var clock = Stopwatch.StartNew();
        var perEngine = await Task.WhenAll(engines.Engines.Select(engine => RunAllAsync(engine, schema, requests))).ConfigureAwait(false);
        var loop = clock.Elapsed;

        var failures = new List<ParityCase>();
        for (var index = 0; index < requests.Count; index++)
        {
            var parityCase = new ParityCase(
                index,
                requests[index],
                [.. engines.Engines.Select((engine, at) => new EngineRun(engine.Name, perEngine[at][index]))]);
            if (ParityComparer.Fails(parityCase))
            {
                failures.Add(parityCase);
            }
        }

        // A property that compared only empty windows would pass for nothing: say how much was actually compared.
        var returned = perEngine[0].Where(outcome => !outcome.Refused).Select(outcome => outcome.Ids!.Count).ToList();
        var coverage = $"{returned.Count(count => count > 0)} of {returned.Count} cases returned rows on {engines.Engines[0].Name}, {returned.Sum()} ids in all";

        var timing = $"parity: seed {config.Seed}, {config.Cases} cases, {config.Rows} rows, {engines.Engines.Count} engines; " +
            $"containers start+seed {engines.StartAndSeed.TotalSeconds:F1} s, case loop {loop.TotalSeconds:F1} s, {failures.Count} failing case(s); {coverage}";
        output.WriteLine(timing);
        Console.WriteLine(timing);

        if (failures.Count > 0)
        {
            var report = ParityComparer.Report(config.Seed, config.Rows, config.Cases, failures, engines.DatasetPath, PrintCap);
            Console.WriteLine(report);
            Assert.Fail(timing + "\n" + report);
        }
    }

    private static async Task<EngineOutcome[]> RunAllAsync(ParityEngine engine, GridSchema schema, IReadOnlyList<string> requests)
    {
        var outcomes = new EngineOutcome[requests.Count];
        for (var index = 0; index < requests.Count; index++)
        {
            try
            {
                var fixture = ExecutionFixture.FromMemory($"parity-{index}", schema, requests[index], []);
                outcomes[index] = EngineOutcome.Returned(await engine.QueryAsync(fixture).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                outcomes[index] = EngineOutcome.Refuse($"{exception.GetType().Name}: {exception.Message}");
            }
        }

        return outcomes;
    }
}
