namespace Craticula.Conformance.Tests;

/// <summary>
/// Runs the offline unit tests of the dependency license gate (<c>scripts/test_check_licenses.py</c>, ADR-0003, spec
/// FR-08, FR-09, FR-14, NFR-03) on every <c>dotnet test</c>: synthetic <c>nuget-license</c> output pins the allowed and
/// denied families, the MPL-2.0 ADR rule (it fails closed without <c>docs/adr/</c>), the <c>MySql.Data</c> trap, the
/// test-only Oracle packages, and the overrides file (a reason is mandatory, a stale entry fails). The real run over
/// <c>csharp/</c> needs the <c>nuget-license</c> tool and nuget.org, so the license-check workflow and the PR pipeline run
/// it, not this test.
/// </summary>
public class LicenseCheckTests
{
    private const string PythonRequirement =
        "The license check tests need Python 3.10+ on PATH as 'python3' or 'python' (scripts/check-licenses.py). " +
        "It is a hard requirement, not a skip: install Python 3.10+ and re-run.";

    [Fact]
    public void License_Check_Script_Passes_Its_Synthetic_Cases()
    {
        var python = RunnerHost.Python;
        if (python is null)
        {
            Assert.Fail(PythonRequirement);
        }

        var result = RunnerHost.Run(
            python,
            ["scripts/test_check_licenses.py"],
            RepoPaths.Root,
            stdinLines: null,
            TimeSpan.FromSeconds(120));

        Assert.True(
            result.ExitCode == 0,
            $"scripts/test_check_licenses.py exited {result.ExitCode} (0 = every case passed).{Environment.NewLine}" +
            $"--- stdout ---{Environment.NewLine}{result.Stdout}{Environment.NewLine}" +
            $"--- stderr ---{Environment.NewLine}{result.Stderr}");
    }
}
