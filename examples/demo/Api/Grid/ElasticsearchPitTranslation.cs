using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenSearch.Net;

namespace Demo.Api.Grid;

/// <summary>
/// Lets the library's OpenSearch client talk to Elasticsearch 8. The client (<c>Craticula.OpenSearch.Client</c>) opens and closes a point-in-time
/// with OpenSearch's calls; Elasticsearch has the same feature under other URLs and one other key (measured in the spike, task #2556):
/// <list type="table">
/// <listheader><term>Call</term><description>OpenSearch 2.x, then Elasticsearch 8.x</description></listheader>
/// <item><term>create</term><description><c>POST /{index}/_search/point_in_time?keep_alive=</c> answers <c>pit_id</c>; Elasticsearch: <c>POST /{index}/_pit?keep_alive=</c> answers <c>id</c></description></item>
/// <item><term>close</term><description><c>DELETE /_search/point_in_time</c> with <c>{"pit_id":[...]}</c>; Elasticsearch: <c>DELETE /_pit</c> with <c>{"id":...}</c></description></item>
/// </list>
/// Searching with a point-in-time (<c>pit</c> in the body, <c>search_after</c>, <c>pit_id</c> in the answer) is the same on both. So the demo
/// translates exactly those two calls at the HTTP level and the library's own deep paging and export run unchanged on Elasticsearch. The library
/// follow-up is #2559 (a target flavour in the client), after which this class goes.
/// </summary>
public sealed class ElasticsearchPitConnection : HttpConnection
{
    protected override HttpMessageHandler CreateHttpClientHandler(RequestData requestData) =>
        new TranslatingHandler(base.CreateHttpClientHandler(requestData));

    /// <summary>The translation, on plain <c>System.Net.Http</c> types so a test can run it with a stub inner handler and no cluster.</summary>
    public sealed class TranslatingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private const string OpenSearchCreate = "/_search/point_in_time";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == System.Net.Http.HttpMethod.Post && path.EndsWith(OpenSearchCreate, StringComparison.Ordinal))
            {
                var builder = new UriBuilder(request.RequestUri) { Path = path[..^OpenSearchCreate.Length] + "/_pit" };
                request.RequestUri = builder.Uri;

                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                return await RenameKeyAsync(response, "id", "pit_id", cancellationToken).ConfigureAwait(false);
            }

            if (request.Method == System.Net.Http.HttpMethod.Delete && path.EndsWith(OpenSearchCreate, StringComparison.Ordinal))
            {
                // Like create, matched at the end of the path, so a base path in the setting (http://proxy/es/) is kept.
                var builder = new UriBuilder(request.RequestUri) { Path = path[..^OpenSearchCreate.Length] + "/_pit" };
                request.RequestUri = builder.Uri;
                if (request.Content is not null)
                {
                    var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    request.Content = new StringContent(ToElasticsearchClose(body), Encoding.UTF8, "application/json");
                }
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        /// <summary><c>{"pit_id":["x"]}</c> becomes <c>{"id":"x"}</c>: Elasticsearch closes one point-in-time per call and the client sends one.</summary>
        public static string ToElasticsearchClose(string openSearchBody)
        {
            var node = JsonNode.Parse(openSearchBody);
            var ids = node?["pit_id"];
            var id = ids is JsonArray { Count: > 0 } array ? array[0]!.GetValue<string>() : ids?.GetValue<string>();
            return new JsonObject { ["id"] = id }.ToJsonString();
        }

        /// <summary>A successful answer whose JSON has <paramref name="from"/> gets that key renamed to <paramref name="to"/>; anything else passes untouched.</summary>
        private static async Task<HttpResponseMessage> RenameKeyAsync(
            HttpResponseMessage response, string from, string to, CancellationToken cancellationToken)
        {
            if (!response.IsSuccessStatusCode || response.Content is null)
            {
                return response;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var node = JsonNode.Parse(text) as JsonObject;
            if (node is null || !node.TryGetPropertyValue(from, out var value))
            {
                return response;
            }

            node.Remove(from);
            node[to] = value;
            response.Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
            return response;
        }
    }
}
