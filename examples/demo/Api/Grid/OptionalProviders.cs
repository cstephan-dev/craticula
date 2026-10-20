using Craticula.EntityFrameworkCore;
using Demo.Api.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Demo.Api.Grid;

/// <summary>
/// An engine that is on only when its connection setting is present: <see cref="EnvVar"/> set (and not blank) means
/// configured. There is no default connection string, unlike PostgreSQL, EF and MongoDB, which are always on.
/// <see cref="Register"/> receives the setting's value and adds the engine's <see cref="IGridService"/> (and whatever it needs).
/// <see cref="Validate"/>, when given, parses the value at startup and throws <see cref="ArgumentException"/> or <see cref="FormatException"/>
/// for a malformed one; <see cref="Engine"/> is the name the startup message uses.
/// </summary>
public sealed record OptionalProvider(
    string Name,
    string EnvVar,
    Action<IServiceCollection, string> Register,
    Action<string>? Validate = null,
    string? Engine = null);

/// <summary>
/// The optional engines the demo knows: SQL Server, MySQL, OpenSearch and Elasticsearch (SQLite is
/// always on and is registered in <c>DemoApp</c>). Tests pass their own list to <c>DemoApp.Build</c>.
/// </summary>
public static class OptionalProviders
{
    public static IReadOnlyList<OptionalProvider> All { get; } =
    [
        new OptionalProvider(
            "sqlserver",
            DemoSettings.SqlServerEnvVar,
            (services, setting) => services.AddSingleton<IGridService>(provider =>
                new SqlServerGridService(setting, provider.GetRequiredService<GridRequestPreparer>())),
            setting => SqlServerGridService.Normalise(setting),
            "SQL Server"),
        // EF Core on the same SQL Server database (route ef-sqlserver): switched on by the same setting as the Dapper route.
        new OptionalProvider(
            "ef-sqlserver",
            DemoSettings.SqlServerEnvVar,
            RegisterEfSqlServer,
            setting => SqlServerGridService.Normalise(setting),
            "SQL Server"),
        new OptionalProvider(
            "mysql",
            DemoSettings.MySqlEnvVar,
            (services, setting) => services.AddSingleton<IGridService>(provider =>
                new MySqlGridService(setting, provider.GetRequiredService<GridRequestPreparer>())),
            setting => MySqlGridService.Normalise(setting),
            "MySQL"),
        new OptionalProvider(
            "opensearch",
            DemoSettings.OpenSearchEnvVar,
            (services, setting) => services.AddSingleton<IGridService>(provider =>
                new OpenSearchGridService(setting, provider.GetRequiredService<GridRequestPreparer>())),
            setting => OpenSearchGridService.ParseUrl(setting),
            "OpenSearch"),
        // The same client and compiler on Elasticsearch 8; the point-in-time calls are translated at the HTTP level (ElasticsearchPitConnection).
        new OptionalProvider(
            "elasticsearch",
            DemoSettings.ElasticsearchEnvVar,
            (services, setting) => services.AddSingleton<IGridService>(provider =>
                new OpenSearchGridService(setting, provider.GetRequiredService<GridRequestPreparer>(), elasticsearch: true)),
            setting => OpenSearchGridService.ParseUrl(setting),
            "Elasticsearch")
    ];

    // Unlike the Npgsql EF provider, the SQL Server one does not wrap a connection failure by default (measured: a stopped server answers 503
    // without NonRetryingExecutionStrategy), so none is configured.
    private static void RegisterEfSqlServer(IServiceCollection services, string setting)
    {
        var normalised = SqlServerGridService.Normalise(setting);
        services.AddDbContextFactory<SqlServerOrdersDbContext>(options => options
            .UseSqlServer(normalised)
            .ReplaceService<IModelCacheKeyFactory, GridSchemaModelCacheKeyFactory>());
        services.AddSingleton<IGridService>(provider => new EfGridService<SqlServerOrdersDbContext>(
            "ef-sqlserver",
            "ef-sqlserver",
            SqlServerOrdersDbContext.Semantics,
            provider.GetRequiredService<IDbContextFactory<SqlServerOrdersDbContext>>(),
            new SqlServerGridService(setting, provider.GetRequiredService<GridRequestPreparer>()).FetchSetValuesAsync,
            provider.GetRequiredService<GridRequestPreparer>()));
    }

    /// <summary>Registers each provider whose setting is present; returns the names registered, in order. A malformed setting
    /// throws <see cref="InvalidOperationException"/> naming the variable.</summary>
    public static IReadOnlyList<string> RegisterConfigured(
        IServiceCollection services, IEnumerable<OptionalProvider> providers, Func<string, string?> readSetting)
    {
        var registered = new List<string>();
        foreach (var provider in providers)
        {
            var setting = readSetting(provider.EnvVar);
            if (string.IsNullOrWhiteSpace(setting))
            {
                continue;
            }

            if (provider.Validate is not null)
            {
                // Fail fast: a setting error is loud at startup and never reaches request time.
                ConnectionStringGuard.Build(
                    setting,
                    value =>
                    {
                        provider.Validate(value);
                        return 0;
                    },
                    () => new InvalidOperationException(ConnectionStringGuard.Message(provider.EnvVar, provider.Engine ?? provider.Name, provider.Name is "opensearch" or "elasticsearch" ? "URL" : "connection string")));
            }

            provider.Register(services, setting);
            registered.Add(provider.Name);
        }

        return registered;
    }
}
