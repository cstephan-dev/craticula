using System.Globalization;
using System.Text;

namespace Craticula.Integration.Tests.Parity;

/// <summary>What one engine did with one generated case: the stableKey sequence it returned, or why it refused (PAR-1).</summary>
internal sealed record EngineOutcome(IReadOnlyList<long>? Ids, string? Refusal)
{
    public bool Refused => Ids is null;

    public static EngineOutcome Returned(IEnumerable<long> ids) => new([.. ids], null);

    public static EngineOutcome Refuse(string reason) => new(null, reason);
}

/// <summary>One engine's outcome for one case, in the engine order of the run.</summary>
internal sealed record EngineRun(string Engine, EngineOutcome Outcome);

/// <summary>One generated case with the outcome of every engine. A pure function of <c>(seed, index)</c> plus the engines' answers.</summary>
internal sealed record ParityCase(int Index, string RequestJson, IReadOnlyList<EngineRun> Runs);

/// <summary>Two engines whose answers differ: both returned different id sequences, or exactly one of them refused the request.</summary>
internal sealed record DisagreeingPair(string First, string Second);

/// <summary>
/// The pure half of the parity property (PAR-1): which engine pairs disagree, and how a failing case is reported. No
/// engine, no Docker: the runner feeds it id sequences.
/// </summary>
internal static class ParityComparer
{
    /// <summary>
    /// Every unordered pair of engines (n*(n-1)/2, 21 for seven) whose answers are not equal. Two refusals are not listed as
    /// a pair (each is a failure on its own, see <see cref="Refusals"/>), so a pair means: both returned and the id
    /// sequences differ in value, length or order, or exactly one refused.
    /// </summary>
    public static IReadOnlyList<DisagreeingPair> DisagreeingPairs(IReadOnlyList<EngineRun> runs)
    {
        var pairs = new List<DisagreeingPair>();
        for (var i = 0; i < runs.Count; i++)
        {
            for (var j = i + 1; j < runs.Count; j++)
            {
                var a = runs[i].Outcome;
                var b = runs[j].Outcome;
                var agree = a.Refused && b.Refused || !a.Refused && !b.Refused && a.Ids!.SequenceEqual(b.Ids!);
                if (!agree)
                {
                    pairs.Add(new DisagreeingPair(runs[i].Engine, runs[j].Engine));
                }
            }
        }

        return pairs;
    }

    /// <summary>The engines that refused the request: PAR-1 makes a refusal of a generated request a failure.</summary>
    public static IReadOnlyList<EngineRun> Refusals(IReadOnlyList<EngineRun> runs) => [.. runs.Where(run => run.Outcome.Refused)];

    public static bool Fails(ParityCase parityCase) =>
        Refusals(parityCase.Runs).Count > 0 || DisagreeingPairs(parityCase.Runs).Count > 0;

    /// <summary>
    /// The report of every failing case, at most <paramref name="printCap"/> of them in full, with the total count. Each
    /// printed case carries the seed and index (the whole replay recipe, PAR-1), the engines that disagree, every engine's
    /// id sequence, the request JSON and the dataset (by seed and row count, and by file when <paramref name="datasetPath"/> is given).
    /// </summary>
    public static string Report(
        long seed, int rows, int totalCases, IReadOnlyList<ParityCase> failures, string? datasetPath, int printCap = 20)
    {
        var text = new StringBuilder()
            .Append("Parity (PAR-1): ").Append(failures.Count).Append(" of ").Append(totalCases)
            .Append(" generated case(s) failed, seed ").Append(seed).Append(", ").Append(rows).AppendLine(" dataset rows.");

        foreach (var failure in failures.Take(printCap))
        {
            text.AppendLine().Append(Case(seed, rows, failure, datasetPath));
        }

        if (failures.Count > printCap)
        {
            text.AppendLine().Append("... ").Append(failures.Count - printCap).Append(" more failing case(s) not printed (cap ")
                .Append(printCap).Append("); failing indexes: ")
                .AppendLine(string.Join(", ", failures.Select(f => f.Index)));
        }

        return text.ToString();
    }

    private static string Case(long seed, int rows, ParityCase failure, string? datasetPath)
    {
        var pairs = DisagreeingPairs(failure.Runs);
        var refusals = Refusals(failure.Runs);
        var text = new StringBuilder()
            .Append("=== case ").Append(failure.Index).Append(" (seed ").Append(seed).Append(", index ").Append(failure.Index).AppendLine(") ===")
            .Append("replay: ParityRequestGenerator.Generate(").Append(seed).Append(", ").Append(failure.Index)
            .Append(") over ParityDataset.Generate(").Append(seed).Append(", ").Append(rows).AppendLine(")");

        if (refusals.Count > 0)
        {
            text.AppendLine("refused (PAR-1: a refusal of a generated request is a failure):");
            foreach (var run in refusals)
            {
                text.Append("  ").Append(run.Engine).Append(": ").AppendLine(run.Outcome.Refusal);
            }
        }

        if (pairs.Count > 0)
        {
            text.Append("disagreeing pairs (").Append(pairs.Count).Append("): ")
                .AppendLine(string.Join(", ", pairs.Select(pair => $"{pair.First} != {pair.Second}")));
        }

        text.AppendLine("id sequences:");
        foreach (var run in failure.Runs.Where(run => !run.Outcome.Refused))
        {
            text.Append("  ").Append(run.Engine.PadRight(14)).Append(" (").Append(run.Outcome.Ids!.Count).Append("): [")
                .Append(string.Join(", ", run.Outcome.Ids!.Select(id => id.ToString(CultureInfo.InvariantCulture)))).AppendLine("]");
        }

        text.AppendLine("request:").AppendLine(failure.RequestJson);
        text.Append("dataset: seed ").Append(seed).Append(", ").Append(rows).Append(" rows");
        if (datasetPath is not null)
        {
            text.Append("; dataset.json written to ").Append(datasetPath);
        }

        return text.AppendLine().ToString();
    }
}

/// <summary>
/// The run's configuration, read from the environment (see <c>Parity/README.md</c>): <c>CRATICULA_PARITY_SEED</c>,
/// <c>CRATICULA_PARITY_CASES</c> and <c>CRATICULA_PARITY_ROWS</c>. An unset variable takes the default; a malformed one throws
/// instead of silently running something else.
/// </summary>
internal sealed record ParityConfig(long Seed, int Cases, int Rows)
{
    public const string SeedVariable = "CRATICULA_PARITY_SEED";
    public const string CasesVariable = "CRATICULA_PARITY_CASES";
    public const string RowsVariable = "CRATICULA_PARITY_ROWS";

    public const long DefaultSeed = 20261010;
    public const int DefaultCases = 200;
    public const int DefaultRows = 300;

    public static ParityConfig FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        return new ParityConfig(
            Read(read, SeedVariable, DefaultSeed, long.MinValue),
            checked((int)Read(read, CasesVariable, DefaultCases, 1)),
            checked((int)Read(read, RowsVariable, DefaultRows, 1)));
    }

    private static long Read(Func<string, string?> read, string name, long fallback, long minimum)
    {
        var text = read(name)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return fallback;
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < minimum)
        {
            throw new InvalidOperationException($"{name}='{text}' is not an integer{(minimum > long.MinValue ? $" >= {minimum}" : string.Empty)}.");
        }

        return value;
    }
}
