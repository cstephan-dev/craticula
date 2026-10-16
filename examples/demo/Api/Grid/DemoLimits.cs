using Craticula.Core.Schema;

namespace Demo.Api.Grid;

/// <summary>SEC-3 limits for the demo host. The GridSchema JSON does not carry them: they are a host decision, passed
/// to every library call that takes limits (<c>GridFilterPipeline</c>, set-values cap).</summary>
public static class DemoLimits
{
    public static ResourceLimits Default { get; } = new()
    {
        MaxPageSize = 500,
        MaxDepth = 6,
        MaxLeaves = 32,
        MaxInValues = 200,
        MaxSetValuesResults = 1_000,
        DefaultSetValuesResults = 200
    };
}
