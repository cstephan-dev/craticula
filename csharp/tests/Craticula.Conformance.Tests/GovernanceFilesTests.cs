namespace Craticula.Conformance.Tests;

/// <summary>
/// Runs <c>scripts/test_governance_files.py</c> (story #1662, FR-02, FR-03, FR-04, NFR-01) on every <c>dotnet test</c>:
/// CODEOWNERS parses, its owners are valid, its paths exist and the catch-all comes first; <c>SECURITY.md</c> names the private
/// report path, two response times and the supported versions and links nothing private; the public governance files name no
/// private path; the DCO workflow and its checker exist; and, where the private documents exist, the gate list of the flow rule
/// and its two copies agree and name the governance files. The gate-list test skips itself in the public mirror.
/// </summary>
public class GovernanceFilesTests
{
    [Fact]
    public void Governance_Files_Lint_Passes_On_The_Real_Files_And_Fails_On_Its_Bad_Examples()
    {
        var python = RunnerHost.Python;
        if (python is null)
        {
            Assert.Fail(
                "The governance file tests need Python 3.10+ on PATH as 'python3' or 'python' (scripts/test_governance_files.py). " +
                "It is a hard requirement, not a skip: install Python 3.10+ and re-run.");
        }

        var result = RunnerHost.Run(
            python,
            ["scripts/test_governance_files.py"],
            RepoPaths.Root,
            stdinLines: null,
            TimeSpan.FromSeconds(180));

        Assert.True(
            result.ExitCode == 0,
            $"scripts/test_governance_files.py exited {result.ExitCode} (0 = every case passed or skipped).{Environment.NewLine}" +
            $"--- stdout ---{Environment.NewLine}{result.Stdout}{Environment.NewLine}" +
            $"--- stderr ---{Environment.NewLine}{result.Stderr}");
    }
}
