using System.Diagnostics;

namespace Craticula.Integration.Tests.Docker;

/// <summary>
/// Whether a Docker daemon answers, probed once per test run with <c>docker info</c> (which honours
/// <c>DOCKER_HOST</c> and the current context, as Testcontainers does). The container tests use it to
/// skip with a reason instead of grinding through socket errors (#2205). Set
/// <c>CRATICULA_INTEGRATION_DOCKER=off</c> to force the skip, for example to prove it, or <c>=on</c> to
/// skip the probe.
/// </summary>
public static class DockerAvailability
{
    public const string OverrideVariable = "CRATICULA_INTEGRATION_DOCKER";

    private static readonly Lazy<string?> Reason = new(Probe);

    public static bool IsAvailable => Reason.Value is null;

    /// <summary>Why Docker is not usable, or null when it is.</summary>
    public static string? UnavailableReason => Reason.Value;

    private static string? Probe()
    {
        var forced = Environment.GetEnvironmentVariable(OverrideVariable)?.Trim().ToLowerInvariant();
        if (forced == "off")
        {
            return $"{OverrideVariable}=off";
        }

        if (forced == "on")
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return "the docker CLI did not start";
            }

            // Drain both pipes so a chatty daemon error cannot block the exit.
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                process.Kill(entireProcessTree: true);
                return "docker info timed out after 15 s";
            }

            Task.WaitAll(output, error);
            return process.ExitCode == 0 && output.Result.Trim().Length > 0
                ? null
                : $"no Docker daemon answered (docker info exit {process.ExitCode})";
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"the docker CLI is not installed ({exception.GetType().Name})";
        }
    }
}
