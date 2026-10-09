using System.Diagnostics;
using System.Text;

namespace Craticula.Conformance.Tests;

/// <summary>The outcome of one child process: exit code and everything it wrote.</summary>
internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Starts the language-neutral runner (<c>conformance/run.py</c>, spec/conformance-runner.md §9) and
/// the C# driver as child processes. Both streams are read asynchronously (a full pipe cannot
/// deadlock the child) and a child that outlives its timeout is killed together with its whole
/// process tree, so a hung driver never leaves an orphan behind.
/// </summary>
internal static class RunnerHost
{
    public static readonly TimeSpan SuiteTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan DriverTimeout = TimeSpan.FromSeconds(30);

    private static readonly Lazy<string?> PythonExecutable = new(FindPython);

    /// <summary>The Python 3.10+ the runner needs: <c>python3</c>, then <c>python</c> (the Windows spelling); null when neither qualifies.</summary>
    public static string? Python => PythonExecutable.Value;

    /// <summary>The driver's dll, copied beside the test assembly by the project reference.</summary>
    public static string DriverDll { get; } =
        Path.Combine(AppContext.BaseDirectory, "Craticula.Conformance.Driver.dll");

    /// <summary>Runs <c>python conformance/run.py --driver "dotnet &lt;driver dll&gt;"</c> from the repo root.</summary>
    public static ProcessResult RunSuite(string python, params string[] runnerArguments)
    {
        // Double quotes survive both readings of --driver (spec §9): POSIX splits it like a shell
        // line, Windows hands the string to CreateProcess unchanged.
        var driverCommand = $"dotnet \"{DriverDll}\"";
        return Run(
            python,
            ["conformance/run.py", "--driver", driverCommand, .. runnerArguments],
            RepoPaths.Root,
            stdinLines: null,
            SuiteTimeout);
    }

    /// <summary>Runs the driver dll directly: writes <paramref name="stdinLines"/>, closes stdin, collects the answer.</summary>
    public static ProcessResult RunDriver(IReadOnlyList<string> stdinLines) =>
        Run("dotnet", [DriverDll], RepoPaths.Root, stdinLines, DriverTimeout);

    public static ProcessResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyList<string>? stdinLines,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) { stdout.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) { stderr.AppendLine(e.Data); } } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // A driver that exits early closes the pipe; the write then fails and is irrelevant.
        try
        {
            if (stdinLines is not null)
            {
                foreach (var line in stdinLines)
                {
                    process.StandardInput.Write(line + "\n");
                }
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException(
                $"'{fileName}' did not finish within {timeout.TotalSeconds:0} s and was killed.{Environment.NewLine}" +
                $"stdout so far:{Environment.NewLine}{stdout}{Environment.NewLine}stderr so far:{Environment.NewLine}{stderr}");
        }

        // The parameterless overload waits for the asynchronous readers to drain.
        process.WaitForExit();
        lock (stdout)
        {
            lock (stderr)
            {
                return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
            }
        }
    }

    private static string? FindPython()
    {
        foreach (var candidate in new[] { "python3", "python" })
        {
            try
            {
                var probe = Run(
                    candidate,
                    ["-c", "import sys; sys.exit(0 if sys.version_info >= (3, 10) else 3)"],
                    RepoPaths.Root,
                    stdinLines: null,
                    TimeSpan.FromSeconds(20));
                if (probe.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not on PATH: try the next spelling.
            }
        }

        return null;
    }
}
