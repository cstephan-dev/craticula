namespace Craticula.Core.Tests;

/// <summary>
/// Locates repo-root-relative paths (e.g. <c>spec/fixtures/...</c>) from wherever the test
/// assembly happens to run (its build output directory, not its source directory), by walking up
/// from <see cref="AppContext.BaseDirectory"/> until a directory containing both <c>spec</c> and
/// <c>csharp</c> is found. Avoids hard-coding a fragile "how many ../ from the output dir" count.
/// </summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRepoRoot();

    public static string Fixture(params string[] segments)
    {
        var parts = new List<string> { Root, "spec", "fixtures" };
        parts.AddRange(segments);
        return Path.Combine(parts.ToArray());
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "spec")) &&
                Directory.Exists(Path.Combine(dir.FullName, "csharp")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repo root (a directory containing both 'spec' and 'csharp') " +
            $"walking up from '{AppContext.BaseDirectory}'.");
    }
}
