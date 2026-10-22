namespace Craticula.Conformance.Tests;

/// <summary>
/// Runs <c>scripts/test_build_site.py</c> (story #1666, task #2836) on every <c>dotnet test</c>: the Markdown converter, the link and
/// anchor check, the exit codes, the build of this repository's own site and the rule that no public page links into the private part
/// of the repository. Standard-library Python only, no network.
/// </summary>
public class SiteBuildTests
{
    private const string PythonRequirement =
        "The site build tests need Python 3.10+ on PATH as 'python3' or 'python' (scripts/test_build_site.py). " +
        "It is a hard requirement, not a skip: install Python 3.10+ and re-run.";

    [Fact]
    public void Site_Build_Script_Passes_Its_Tests()
    {
        var python = RunnerHost.Python;
        if (python is null)
        {
            Assert.Fail(PythonRequirement);
        }

        var result = RunnerHost.Run(
            python,
            ["scripts/test_build_site.py"],
            RepoPaths.Root,
            stdinLines: null,
            TimeSpan.FromSeconds(120));

        Assert.True(
            result.ExitCode == 0,
            $"scripts/test_build_site.py exited {result.ExitCode} (0 = every case passed).{Environment.NewLine}" +
            $"--- stdout ---{Environment.NewLine}{result.Stdout}{Environment.NewLine}" +
            $"--- stderr ---{Environment.NewLine}{result.Stderr}");
    }
}
