using Craticula.AgGrid;
using Craticula.Export.Csv;
using Demo.Api.Grid;

namespace Demo.Api.Routes;

/// <summary>
/// The four provider-neutral routes: <c>POST /api/{provider}/rows | values | export.csv | query</c>. The body is the raw
/// AG Grid request JSON; nothing here knows which database answers.
/// </summary>
public static class GridRoutes
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/{provider}");

        group.MapPost("/rows", async (string provider, HttpContext http, ProviderRegistry providers, GridRequestPreparer preparer) =>
        {
            if (!TryProvider(providers, provider, out var service))
            {
                return UnknownProvider(provider);
            }

            var request = preparer.PrepareRows(await ReadBodyAsync(http));
            var result = await service.FetchRowsAsync(request, http.RequestAborted);

            // { rows, lastRow, groupLevelInfo? } exactly as LoadSuccessParams defines it; the page maps
            // rows -> rowData and lastRow -> rowCount when it calls params.success.
            return Results.Json(LoadSuccessParamsMapper.Map(result, request.StartRow));
        });

        group.MapPost("/values", async (string provider, HttpContext http, ProviderRegistry providers, GridRequestPreparer preparer) =>
        {
            if (!TryProvider(providers, provider, out var service))
            {
                return UnknownProvider(provider);
            }

            var request = preparer.PrepareSetValues(await ReadBodyAsync(http));
            var values = await service.FetchSetValuesAsync(request, http.RequestAborted);

            return Results.Json(new { values });
        });

        group.MapPost("/export.csv", async (string provider, HttpContext http, ProviderRegistry providers, GridRequestPreparer preparer) =>
        {
            if (!TryProvider(providers, provider, out var service))
            {
                return UnknownProvider(provider);
            }

            var request = preparer.PrepareExport(await ReadBodyAsync(http));
            var export = request.Parsed.Export!;

            // Pull the first row before the response starts: a database that is down then fails as a clean 503,
            // not as a half-written file. (Every later failure aborts the connection instead.)
            var rows = await PrimeAsync(service.StreamExportAsync(request, http.RequestAborted));

            var extension = export.Format == Craticula.Core.Export.ExportFormat.Tsv ? "tsv" : "csv";
            http.Response.ContentType = export.Format == Craticula.Core.Export.ExportFormat.Tsv
                ? "text/tab-separated-values; charset=utf-8"
                : "text/csv; charset=utf-8";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"orders.{extension}\"";

            // CsvWriterOptions defaults: UTF-8 BOM on, formula-injection hardening on (EXP-6).
            await CsvWriter.WriteAsync(
                rows, export.Columns, CsvWriterOptions.ForFormat(export.Format), http.Response.Body, http.RequestAborted);

            return Results.Empty;
        });

        group.MapPost("/query", async (string provider, HttpContext http, ProviderRegistry providers, GridRequestPreparer preparer) =>
        {
            if (!TryProvider(providers, provider, out var service))
            {
                return UnknownProvider(provider);
            }

            // Compile only: this route never opens a database connection.
            var view = service.Compile(preparer.PrepareRows(await ReadBodyAsync(http)));

            return Results.Json(new { kind = view.Kind, dialect = view.Dialect, text = view.Text, parameters = view.Parameters });
        });
    }

    private static bool TryProvider(ProviderRegistry providers, string name, out IGridService service) =>
        providers.TryGet(name, out service);

    private static IResult UnknownProvider(string name) =>
        Results.Json(
            new { error = new { kind = "unknown-provider", message = $"Unknown provider '{name}'. Use one of: postgres, mongo." } },
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>The raw body text: the library parses it, so the host never deserializes the grid request itself.</summary>
    private static async Task<string> ReadBodyAsync(HttpContext http)
    {
        using var reader = new StreamReader(http.Request.Body, System.Text.Encoding.UTF8);
        return await reader.ReadToEndAsync(http.RequestAborted);
    }

    private static async Task<IAsyncEnumerable<Craticula.Core.Export.ExportRow>> PrimeAsync(
        IAsyncEnumerable<Craticula.Core.Export.ExportRow> rows)
    {
        var enumerator = rows.GetAsyncEnumerator();
        bool hasFirst;
        try
        {
            hasFirst = await enumerator.MoveNextAsync();
        }
        catch
        {
            await enumerator.DisposeAsync();
            throw;
        }

        return Replay(enumerator, hasFirst);
    }

    private static async IAsyncEnumerable<Craticula.Core.Export.ExportRow> Replay(
        IAsyncEnumerator<Craticula.Core.Export.ExportRow> enumerator, bool hasFirst)
    {
        await using (enumerator)
        {
            if (!hasFirst)
            {
                yield break;
            }

            do
            {
                yield return enumerator.Current;
            }
            while (await enumerator.MoveNextAsync());
        }
    }
}
