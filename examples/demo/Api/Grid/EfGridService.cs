using Craticula.Core.Export;
using Craticula.Core.Grid;
using Craticula.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Demo.Api.Grid;

/// <summary>
/// One EF Core route through <c>Craticula.EntityFrameworkCore</c>: the IR becomes an <c>IQueryable</c> over a property-bag model and the
/// provider of <typeparamref name="TContext"/> writes the SQL. One context per call, from the factory. The route's name, its dialect
/// label, the engine facts (<see cref="EfCoreProviderSemantics"/>) and the distinct-values call are parameters, so PostgreSQL
/// (<see cref="EfGridService"/>), SQL Server and SQLite share the code.
/// </summary>
public class EfGridService<TContext>(
    string name,
    string dialect,
    EfCoreProviderSemantics semantics,
    IDbContextFactory<TContext> contexts,
    Func<PreparedSetValues, CancellationToken, Task<IReadOnlyList<object?>>> fetchSetValues,
    GridRequestPreparer preparer) : IGridService
    where TContext : DbContext
{
    public string Name => name;

    public async Task<GridPageResult> FetchRowsAsync(PreparedRows request, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var page = await EfCoreGridQuery.FetchPageAsync(
            GridSchemaPropertyBag.Query(context, preparer.Schema), ToEfRequest(request), semantics,
            includeRowCount: true, cancellationToken);
        return page.ToGridPageResult();
    }

    /// <summary>The EF adapter has no entry point for BM-3's distinct values either: the compiled artifact of the engine's own Dapper route runs instead.</summary>
    public Task<IReadOnlyList<object?>> FetchSetValuesAsync(PreparedSetValues request, CancellationToken cancellationToken) =>
        fetchSetValues(request, cancellationToken);

    public async IAsyncEnumerable<ExportRow> StreamExportAsync(
        PreparedRows request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await foreach (var row in EfCoreExportStream.StreamAsync(
            GridSchemaPropertyBag.Query(context, preparer.Schema), ToEfRequest(request), semantics,
            options: null, cancellationToken))
        {
            yield return row;
        }
    }

    /// <summary>
    /// The statement EF would run for the first page's rows, from <c>ToQueryString()</c>. EF inlines parameter values in that
    /// text (as declarations in comments), so <c>Parameters</c> is empty and the text can contain filter values: unlike the
    /// postgres and mongo views it is not a parameterized artifact. Nothing is executed and no connection is opened.
    /// </summary>
    public CompiledQueryView Compile(PreparedRows request)
    {
        const string kind = "sql";
        var efRequest = ToEfRequest(request);

        if (efRequest.Group is not null)
        {
            // The grouped and drill-down shapes are composed inside the adapter and no public method returns them as a queryable.
            return new CompiledQueryView(kind, dialect,
                "-- Grouped and drill-down requests are composed inside the EF adapter, which exposes no queryable to print.",
                []);
        }

        using var context = contexts.CreateDbContext();
        var source = GridSchemaPropertyBag.Query(context, preparer.Schema);
        var filtered = EfCoreQueryBuilder.ApplyFilter(source, efRequest, semantics);
        var ordered = EfCoreQueryBuilder.ApplyOrdering(filtered, efRequest, semantics);
        var window = EfCoreQueryBuilder.ApplyRowWindow(ordered, efRequest.Page);
        var projected = EfCoreQueryBuilder.ApplyProjection(
            window, preparer.Schema, efRequest.Projection ?? preparer.Schema.FieldOrder);
        return new CompiledQueryView(kind, dialect, projected.ToQueryString(), []);
    }

    private EfCoreQueryRequest ToEfRequest(PreparedRows request) => new()
    {
        Schema = preparer.Schema,
        Filter = request.Filter,
        Sort = request.Parsed.Sort,
        Page = request.Parsed.Page,
        Group = request.Parsed.Group,
        Aggregations = request.Parsed.Aggregations,
        Projection = request.Parsed.Export?.ProjectionColumns
    };
}

/// <summary>PostgreSQL through <c>Craticula.EntityFrameworkCore</c> and the Npgsql EF provider (route <c>ef</c>).</summary>
public sealed class EfGridService(
    IDbContextFactory<OrdersDbContext> contexts, NpgsqlDataSource dataSource, GridRequestPreparer preparer)
    : EfGridService<OrdersDbContext>(
        "ef",
        "ef-npgsql",
        OrdersDbContext.PostgresSemantics,
        contexts,
        (request, cancellationToken) => PostgresSetValues.FetchAsync(dataSource, preparer.Schema, request, cancellationToken),
        preparer);
