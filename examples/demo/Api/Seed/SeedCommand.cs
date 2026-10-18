using Demo.Api.Errors;

namespace Demo.Api.Seed;

/// <summary>What `seed [provider ...]` asked for: the targets to fill, or the names that are not known.</summary>
public sealed record SeedPlan(IReadOnlyList<SeedTarget> Targets, IReadOnlyList<string> Unknown);

/// <summary>
/// The `seed` command: generate once, write the same rows to each requested database, print the counts. Each provider is
/// seeded on its own, so a database that is down reports its error and the others still run.
/// Exit codes: 0 every requested provider holds the right count, 1 a count differs, 2 a provider failed or a name is unknown.
/// </summary>
public static class SeedCommand
{
    /// <summary>Pure: no argument means the default targets; names (case-insensitive, repeats ignored) select a subset in the order given.</summary>
    public static SeedPlan Parse(IReadOnlyList<string> names, IReadOnlyList<SeedTarget> known)
    {
        if (names.Count == 0)
        {
            return new SeedPlan([.. known.Where(target => target.Default)], []);
        }

        var targets = new List<SeedTarget>();
        var unknown = new List<string>();
        foreach (var name in names)
        {
            var target = known.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                unknown.Add(name);
            }
            else if (!targets.Contains(target))
            {
                targets.Add(target);
            }
        }

        return new SeedPlan(targets, unknown);
    }

    public static Task<int> RunAsync(IReadOnlyList<string> names) =>
        RunAsync(names, SeedTargets.All, Console.Out, Console.Error);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> names, IReadOnlyList<SeedTarget> known, TextWriter output, TextWriter error)
    {
        var plan = Parse(names, known);
        if (plan.Unknown.Count > 0)
        {
            error.WriteLine($"Unknown provider: {string.Join(", ", plan.Unknown)}. Known providers: {string.Join(", ", known.Select(target => target.Name))}.");
            return 2;
        }

        var rows = OrderGenerator.Generate();
        output.WriteLine($"Generated {rows.Count} orders (seed {OrderGenerator.Seed}).");

        var mismatch = false;
        var failed = false;
        foreach (var target in plan.Targets)
        {
            try
            {
                var count = await target.SeedAsync(rows);
                output.WriteLine(target.Describe(count));
                mismatch |= count != rows.Count;
            }
            catch (Exception ex) when (EngineFailures.IsUnavailable(ex))
            {
                failed = true;
                error.WriteLine($"Seed failed for {target.Name}: {ex.Message}");
                error.WriteLine(target.Hint);
            }
        }

        return failed ? 2 : mismatch ? 1 : 0;
    }
}
