namespace Craticula.Conformance.Tests;

/// <summary>
/// Runs <c>scripts/test_release_please_config.py</c> (story #1659, AC-2/AC-3) on every <c>dotnet test</c>: the Release Please
/// config and manifest, the one-match <c>&lt;Version&gt;</c> xpath on the real props file, the manifest-equals-Version
/// invariant, and the workflow's SHA pins, permissions and absence of secrets. No network and no GitHub.
/// </summary>
public class ReleasePleaseConfigTests
{
    private const string PythonRequirement =
        "The Release Please config tests need Python 3.10+ on PATH as 'python3' or 'python' (scripts/test_release_please_config.py). " +
        "It is a hard requirement, not a skip: install Python 3.10+ and re-run.";

    [Fact]
    public void Release_Please_Config_Script_Passes()
    {
        var python = RunnerHost.Python;
        if (python is null)
        {
            Assert.Fail(PythonRequirement);
        }

        var result = RunnerHost.Run(
            python,
            ["scripts/test_release_please_config.py"],
            RepoPaths.Root,
            stdinLines: null,
            TimeSpan.FromSeconds(60));

        Assert.True(
            result.ExitCode == 0,
            $"scripts/test_release_please_config.py exited {result.ExitCode} (0 = every case passed).{Environment.NewLine}" +
            $"--- stdout ---{Environment.NewLine}{result.Stdout}{Environment.NewLine}" +
            $"--- stderr ---{Environment.NewLine}{result.Stderr}");
    }
}
