namespace Craticula.Conformance.Tests;

/// <summary>
/// Runs <c>scripts/test_apply_public_change.py</c> (story #1662, FR-15 and AC-7) on every <c>dotnet test</c>: on scratch
/// repositories and a scratch bare public remote, a contributor's change keeps its author, date, message and
/// <c>Signed-off-by</c> line when <c>scripts/apply-public-change.sh</c> re-applies it, the publish gate accepts it once it is
/// recorded by its <c>Public-Sync</c> trailer, and the <c>commit-msg</c> hook accepts such a commit only with the sign-off.
/// The script, its test and the hook are private paths: where the file is absent (the public mirror) the test does nothing.
/// The real <c>github</c> remote is never contacted.
/// </summary>
public class ApplyPublicChangeTests
{
    [Fact]
    public void Apply_Public_Change_Script_And_Hook_Pass_Their_Scratch_Repository_Cases()
    {
        var script = Path.Combine(RepoPaths.Root, "scripts", "test_apply_public_change.py");
        if (!File.Exists(script))
        {
            return; // the public mirror does not carry it
        }

        var python = RunnerHost.Python;
        if (python is null)
        {
            Assert.Fail(
                "The apply-public-change tests need Python 3.10+ on PATH as 'python3' or 'python' (scripts/test_apply_public_change.py). " +
                "It is a hard requirement, not a skip: install Python 3.10+ and re-run.");
        }

        var result = RunnerHost.Run(
            python,
            ["scripts/test_apply_public_change.py"],
            RepoPaths.Root,
            stdinLines: null,
            TimeSpan.FromSeconds(180));

        Assert.True(
            result.ExitCode == 0,
            $"scripts/test_apply_public_change.py exited {result.ExitCode} (0 = every case passed or skipped).{Environment.NewLine}" +
            $"--- stdout ---{Environment.NewLine}{result.Stdout}{Environment.NewLine}" +
            $"--- stderr ---{Environment.NewLine}{result.Stderr}");
    }
}
