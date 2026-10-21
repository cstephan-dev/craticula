using System.Globalization;
using System.Text.Json;
using Craticula.Core.Export;
using Craticula.Core.Grid;
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.OpenSearch.Client;
using Craticula.Search;
using OpenSearch.Net;

namespace Demo.Api.Grid;

/// <summary>
/// OpenSearch 2.x, or Elasticsearch 8 (<c>elasticsearch: true</c>), through <c>Craticula.OpenSearch.Client</c> and the low-level client. An
/// optional engine: registered only when <c>DEMO_OPENSEARCH</c> (or <c>DEMO_ELASTICSEARCH</c>), a URL with no credential (the compose services
/// have security off), is set. The same compiler artifact serves both; the one difference, the point-in-time calls, is the client's
/// <see cref="SearchEngineFlavour"/> setting (OSE-15), which this service sets for Elasticsearch.
/// <para>One client and one <see cref="InMemorySearchCursorCache"/> are shared by every request, as the adapter requires: the deep
/// paging cursor (PIT and <c>search_after</c>, or a composite <c>after</c> key) lives in that cache, so a restart of the API loses it
/// and the next deep page walks from the start. The AG Grid request carries no grid session id, so every request uses one fixed id
/// (<see cref="GridSession"/>); the cache key also hashes the compiled filter and sort, so two grids with different queries do not
/// share a cursor, but two grids with the same query and different positions take turns. With two such grids reading at once past 10,000 rows, one request can close the PIT the other just read from; that request then answers 500 (a known demo limit with one fixed session id; one browser tab does not hit it).</para>
/// <para>Grouped requests go through the adapter. The few shapes that PostgreSQL answers and OpenSearch cannot are named in
/// <see cref="OpenSearchShapes"/> and answer <see cref="Errors.UnsupportedShapeException"/> (HTTP 400, kind <c>unsupported-shape</c>).</para>
/// </summary>
public sealed class OpenSearchGridService : IGridService
{
    /// <summary>The one grid session id: the AG Grid wire request carries none.</summary>
    public const string GridSession = "demo";

    private readonly IOpenSearchLowLevelClient client;
    private readonly InMemorySearchCursorCache cursors = new();
    private readonly GridRequestPreparer preparer;

    private readonly bool elasticsearch;
    private readonly SearchEngineFlavour flavour;

    public OpenSearchGridService(string url, GridRequestPreparer preparer, bool elasticsearch = false)
    {
        this.preparer = preparer;
        this.elasticsearch = elasticsearch;
        client = CreateClient(ParseUrl(url));
        flavour = elasticsearch ? SearchEngineFlavour.Elasticsearch : SearchEngineFlavour.OpenSearch;
    }

    /// <summary>The cluster address from the setting. Throws <see cref="FormatException"/> for anything that is not an absolute
    /// <c>http</c> or <c>https</c> URL; the host checks it at startup (<c>OptionalProviders</c>).</summary>
    public static Uri ParseUrl(string setting)
    {
        if (Uri.TryCreate(setting.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return uri;
        }

        throw new FormatException("The setting is not an absolute http or https URL.");
    }

    /// <summary>A client with a 10 s request timeout (the connect timeout is part of it) and no ping: a single-node pool does not retry,
    /// so an unreachable cluster answers 503 at once when the port is closed and after the timeout when nothing answers.</summary>
    private static OpenSearchLowLevelClient CreateClient(Uri url) =>
        new(new ConnectionConfiguration(url)
            .DisablePing()
            .RequestTimeout(TimeSpan.FromSeconds(10)));

    public string Name => elasticsearch ? "elasticsearch" : "opensearch";

    public async Task<GridPageResult> FetchRowsAsync(PreparedRows request, CancellationToken cancellationToken)
    {
        OpenSearchShapes.ThrowIfUnsupported(request, elasticsearch ? "Elasticsearch" : "OpenSearch");
        var searchRequest = ToSearchRequest(request);
        var page = await OpenSearchGridQuery.FetchPageAsync(
            client, searchRequest, GridSession, cursors, new OpenSearchDeepPagingOptions { Flavour = flavour }, includeRowCount: true, cancellationToken);
        return page.ToGridPageResult(searchRequest);
    }

    /// <summary>The adapter has no entry point for BM-3's distinct values: compile with <c>SearchQueryBuilder.BuildSetValues</c>,
    /// run only that body and read the <c>terms</c> buckets (the library's reader is internal).</summary>
    public async Task<IReadOnlyList<object?>> FetchSetValuesAsync(PreparedSetValues request, CancellationToken cancellationToken)
    {
        var query = SearchQueryBuilder.BuildSetValues(
            new SearchSetValuesRequest
            {
                Schema = preparer.Schema,
                ColId = request.ColId,
                Filter = request.Filter,
                RowLimit = request.RowLimit
            });

        var response = await client.SearchAsync<StringResponse>(
            query.Index, PostData.String(query.ToArtifact().ToJsonString()), null, cancellationToken);
        OpenSearchCalls.Require(response, "The distinct-values call");

        using var document = JsonDocument.Parse(response.Body);
        var type = preparer.Schema.Fields[request.ColId].Type;
        var buckets = document.RootElement.GetProperty("aggregations").GetProperty(request.ColId).GetProperty("buckets");
        return [.. buckets.EnumerateArray().Select(bucket => BucketKey(bucket, type))];
    }

    /// <summary>
    /// The export streams through <c>OpenSearchExportStream</c> (one PIT, <c>search_after</c> chunks). That stream always returns every
    /// schema column in schema order (OSE-13: the compiled body has no projection), so the rows are narrowed and reordered here to the
    /// export's column list, as the CSV writer expects.
    /// </summary>
    public async IAsyncEnumerable<ExportRow> StreamExportAsync(
        PreparedRows request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var order = preparer.Schema.FieldOrder;
        var picks = request.Parsed.Export!.Columns.Select(column => IndexOf(order, column.ColId)).ToArray();

        await foreach (var row in OpenSearchExportStream.StreamAsync(client, ToSearchRequest(request), new OpenSearchExportOptions { Flavour = flavour }, cancellationToken))
        {
            yield return new ExportRow([.. picks.Select(index => row[index])]);
        }
    }

    /// <summary>The compiled search body as JSON text. The adapter adds a PIT, <c>search_after</c> or a composite <c>after</c> key, and
    /// a count request on top of it, so this is the compiler's output, not byte for byte what is sent.</summary>
    public CompiledQueryView Compile(PreparedRows request)
    {
        OpenSearchShapes.ThrowIfUnsupported(request, elasticsearch ? "Elasticsearch" : "OpenSearch");
        var query = SearchQueryBuilder.Build(ToSearchRequest(request));
        var text = query.ToArtifact().ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        // The values travel inside the body as data; there are no bind parameters.
        return new CompiledQueryView("json", elasticsearch ? "elasticsearch" : "opensearch", text, []);
    }

    private static int IndexOf(IReadOnlyList<string> order, string colId)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], colId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Column '{colId}' is not in the schema.");
    }

    /// <summary>A terms bucket's key as the column's CLR type: a keyword is text, a number is a number, a boolean, date or datetime
    /// is read from <c>key_as_string</c> (the key itself is 0/1 or epoch milliseconds).</summary>
    private static object? BucketKey(JsonElement bucket, FieldType type)
    {
        var key = bucket.GetProperty("key");
        return type switch
        {
            FieldType.String => key.ValueKind == JsonValueKind.String ? key.GetString() : key.GetRawText(),
            FieldType.Int64 => key.ValueKind == JsonValueKind.String ? long.Parse(key.GetString()!, CultureInfo.InvariantCulture) : key.GetInt64(),
            FieldType.Decimal => decimal.Parse(key.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture),
            FieldType.Double => key.GetDouble(),
            FieldType.Boolean => bucket.GetProperty("key_as_string").GetString() == "true",
            FieldType.Date => DateOnly.ParseExact(bucket.GetProperty("key_as_string").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            FieldType.Datetime => DateTime.Parse(
                bucket.GetProperty("key_as_string").GetString()!, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            _ => throw new InvalidOperationException($"Unmapped FieldType '{type}'.")
        };
    }

    private SearchQueryRequest ToSearchRequest(PreparedRows request) => new()
    {
        Schema = preparer.Schema,
        Filter = request.Filter,
        Sort = request.Parsed.Sort,
        Page = request.Parsed.Page,
        Group = request.Parsed.Group,
        Aggregations = request.Parsed.Aggregations
    };
}
