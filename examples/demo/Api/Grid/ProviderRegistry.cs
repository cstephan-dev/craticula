namespace Demo.Api.Grid;

public sealed class ProviderRegistry(IEnumerable<IGridService> services)
{
    private readonly Dictionary<string, IGridService> byName =
        services.ToDictionary(service => service.Name, StringComparer.Ordinal);

    public IReadOnlyCollection<string> Names => byName.Keys;

    public bool TryGet(string name, out IGridService service) => byName.TryGetValue(name, out service!);
}
