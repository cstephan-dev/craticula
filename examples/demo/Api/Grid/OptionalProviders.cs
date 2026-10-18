namespace Demo.Api.Grid;

/// <summary>
/// An engine that is on only when its connection setting is present: <see cref="EnvVar"/> set (and not blank) means
/// configured. There is no default connection string, unlike PostgreSQL, EF and MongoDB, which are always on.
/// <see cref="Register"/> receives the setting's value and adds the engine's <see cref="IGridService"/> (and whatever it needs).
/// </summary>
public sealed record OptionalProvider(string Name, string EnvVar, Action<IServiceCollection, string> Register);

/// <summary>
/// The optional engines the demo knows. Empty on purpose: the SQL Server, MySQL and OpenSearch tasks each add one entry
/// here (SQLite is always on and is registered in <c>DemoApp</c>). Tests pass their own list to <c>DemoApp.Build</c>.
/// </summary>
public static class OptionalProviders
{
    public static IReadOnlyList<OptionalProvider> All { get; } = [];

    /// <summary>Registers each provider whose setting is present; returns the names registered, in order.</summary>
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

            provider.Register(services, setting);
            registered.Add(provider.Name);
        }

        return registered;
    }
}
