namespace Craticula.Conformance.Tests;

/// <summary>
/// Runs <c>scripts/test_release_notes.py</c> on every <c>dotnet test</c>: the spec-version line for the
/// release notes is built from <c>spec/VERSION</c> and the fixture folders, appended once, and bad input is refused.
/// No network and no GitHub.
/// </summary>
public class ReleaseNotesTests
{
    private const string PythonRequirement =
        "The release notes tests need Python 3.10+ on PATH as 'python3' or 'python' (scripts/test_release_notes.py). " +
        "It is a hard requirement, not a skip: install Python 3.10+ and re-run.";

    [Fact]
    public void Release_Notes_Script_Passes()
    {
        var python = RunnerHost.Python;
        if (python is null)
        {
            Assert.Fail(PythonRequirement);
        }

        var result = RunnerHost.Run(
            python,
            ["scripts/test_release_notes.py"],
            RepoPaths.Root,
            stdinLines: null,
            TimeSpan.FromSeconds(60));

        Assert.True(
            result.ExitCode == 0,
            $"scripts/test_release_notes.py exited {result.ExitCode} (0 = every case passed).{Environment.NewLine}" +
            $"--- stdout ---{Environment.NewLine}{result.Stdout}{Environment.NewLine}" +
            $"--- stderr ---{Environment.NewLine}{result.Stderr}");
    }
}
