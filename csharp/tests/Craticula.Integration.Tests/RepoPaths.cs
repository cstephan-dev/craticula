namespace Craticula.Integration.Tests;

/// <summary>
/// Locates repo-root-relative paths (<c>spec/fixtures/...</c>) from wherever the test assembly runs.
/// Mirrors the sibling test projects' own <c>RepoPaths</c> (each is internal to its assembly).
/// </summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRepoRoot();

    public static string Fixture(params string[] segments) =>
        Path.Combine([Root, "spec", "fixtures", .. segments]);

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

        throw new InvalidOperationException(
            $"Could not locate the repository root (a directory holding both 'spec' and 'csharp') above {AppContext.BaseDirectory}.");
    }
}
