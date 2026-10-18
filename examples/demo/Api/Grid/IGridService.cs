using Craticula.Core.Export;
using Craticula.Core.Grid;

namespace Demo.Api.Grid;

/// <summary>What the <c>/query</c> route shows: the compiled artifact, never an executed one.</summary>
public sealed record CompiledQueryView(string Kind, string Dialect, string Text, IReadOnlyList<object?> Parameters);

/// <summary>One implementation per provider, so the routes stay provider-neutral.</summary>
public interface IGridService
{
    /// <summary>The route segment: <c>postgres</c>, <c>ef</c> or <c>mongo</c>.</summary>
    string Name { get; }

    Task<GridPageResult> FetchRowsAsync(PreparedRows request, CancellationToken cancellationToken);

    /// <summary>Distinct values of the set filter's column, ascending, nulls last, capped at the request's resolved limit.</summary>
    Task<IReadOnlyList<object?>> FetchSetValuesAsync(PreparedSetValues request, CancellationToken cancellationToken);

    /// <summary>The whole filtered, sorted result as rows in the order and columns of <c>request.Parsed.Export</c>.</summary>
    IAsyncEnumerable<ExportRow> StreamExportAsync(PreparedRows request, CancellationToken cancellationToken);

    /// <summary>Compile only. The query is the one <see cref="FetchRowsAsync"/> would run for the same request.</summary>
    CompiledQueryView Compile(PreparedRows request);
}
