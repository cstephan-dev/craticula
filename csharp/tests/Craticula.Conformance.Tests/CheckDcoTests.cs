namespace Craticula.Conformance.Tests;

/// <summary>
/// Runs <c>scripts/test_check_dco.py</c> (story #1662, FR-02 and AC-2) on every <c>dotnet test</c>: on scratch repositories,
/// the DCO check of the public pull request workflow (<c>scripts/check-dco.sh</c>) passes a signed-off range, names exactly
/// the commits that lack a <c>Signed-off-by</c> trailer with the author's or committer's e-mail, exempts merge commits, takes no
/// exemption from a commit's author e-mail, and the exemption of <c>.github/workflows/dco.yml</c> (the pull request author's
/// login, same repository only) behaves as written. The tests skip themselves where bash or git is missing.
/// </summary>
public class CheckDcoTests
{
    [Fact]
    public void Check_Dco_Script_And_Workflow_Exemption_Pass_Their_Scratch_Repository_Cases()
    {
        var python = RunnerHost.Python;
        if (python is null)
        {
            Assert.Fail(
                "The DCO check tests need Python 3.10+ on PATH as 'python3' or 'python' (scripts/test_check_dco.py). " +
                "It is a hard requirement, not a skip: install Python 3.10+ and re-run.");
        }

        var result = RunnerHost.Run(
            python,
            ["scripts/test_check_dco.py"],
            RepoPaths.Root,
            stdinLines: null,
            TimeSpan.FromSeconds(180));

        Assert.True(
            result.ExitCode == 0,
            $"scripts/test_check_dco.py exited {result.ExitCode} (0 = every case passed or skipped).{Environment.NewLine}" +
            $"--- stdout ---{Environment.NewLine}{result.Stdout}{Environment.NewLine}" +
            $"--- stderr ---{Environment.NewLine}{result.Stderr}");
    }
}
