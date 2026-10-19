using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using MySqlConnector;

namespace Demo.Api.Tests;

/// <summary>
/// Why an optional engine's tests cannot run, or null when they can. One clear reason per case: the variable is unset, the engine
/// does not answer (a real login is tried, not only a port), or PostgreSQL and MongoDB (the baseline) are down. Probed once per
/// variable per run. Each engine task adds its own probe to <see cref="Probes"/>.
/// </summary>
internal static class EngineProbe
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new();

    private static readonly Dictionary<string, Func<string, string?>> Probes = new()
    {
        [Seed.DemoSettings.SqlServerEnvVar] = ProbeSqlServer,
        [Seed.DemoSettings.MySqlEnvVar] = ProbeMySql,
        [Seed.DemoSettings.OpenSearchEnvVar] = ProbeOpenSearch
    };

    public static string? SkipReason(string envVar) => Cache.GetOrAdd(envVar, Compute);

    private static string? Compute(string envVar)
    {
        var setting = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(setting))
        {
            return $"{envVar} is not set. Start the engine with its compose profile, seed it with `dotnet run --project Api -- seed <provider>`, "
                + "and set the variable to its connection string (README.md of examples/demo).";
        }

        if (!Probes.TryGetValue(envVar, out var probe))
        {
            return $"No probe is defined for {envVar} (add it to EngineProbe).";
        }

        return probe(setting) ?? new RequiresDatabasesFactAttribute().Skip;
    }

    private static string? ProbeSqlServer(string setting)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(setting) { ConnectTimeout = 3, ConnectRetryCount = 0 };
            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
            return null;
        }
        catch (Exception ex) when (ex is SqlException or ArgumentException or InvalidOperationException)
        {
            return $"SQL Server does not answer at the {Seed.DemoSettings.SqlServerEnvVar} connection string ({ex.GetType().Name}). "
                + "Is `docker compose --profile sqlserver up -d` healthy and the database seeded (`seed sqlserver`)?";
        }
    }

    private static string? ProbeOpenSearch(string setting)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(setting.Trim()), Timeout = TimeSpan.FromSeconds(3) };
            using var response = http.GetAsync("_cluster/health").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode
                ? null
                : $"OpenSearch answers HTTP {(int)response.StatusCode} at the {Seed.DemoSettings.OpenSearchEnvVar} URL.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return $"OpenSearch does not answer at the {Seed.DemoSettings.OpenSearchEnvVar} URL ({ex.GetType().Name}). "
                + "Is `docker compose --profile opensearch up -d --wait` healthy and the index seeded (`seed opensearch`)?";
        }
    }

    private static string? ProbeMySql(string setting)
    {
        try
        {
            var builder = new MySqlConnectionStringBuilder(setting) { ConnectionTimeout = 3 };
            using var connection = new MySqlConnection(builder.ConnectionString);
            connection.Open();
            return null;
        }
        catch (Exception ex) when (ex is MySqlException or ArgumentException or InvalidOperationException)
        {
            return $"MySQL does not answer at the {Seed.DemoSettings.MySqlEnvVar} connection string ({ex.GetType().Name}). "
                + "Is `docker compose --profile mysql up -d` healthy and the database seeded (`seed mysql`)?";
        }
    }
}

/// <summary>A [Fact] skipped, with the reason, unless the optional engine named by <c>envVar</c> answers (and PostgreSQL and MongoDB do).</summary>
public sealed class RequiresEngineFactAttribute : FactAttribute
{
    public RequiresEngineFactAttribute(string envVar) => Skip = EngineProbe.SkipReason(envVar);
}

/// <summary>A [Theory] with the same skip rule as <see cref="RequiresEngineFactAttribute"/>.</summary>
public sealed class RequiresEngineTheoryAttribute : TheoryAttribute
{
    public RequiresEngineTheoryAttribute(string envVar) => Skip = EngineProbe.SkipReason(envVar);
}
