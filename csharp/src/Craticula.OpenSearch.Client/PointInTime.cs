using System.Text.Json;
using System.Text.Json.Nodes;
using OpenSearch.Net;

namespace Craticula.OpenSearch.Client;

/// <summary>
/// The point-in-time lifecycle for both engines (OSE-15), shared by the paged grid query and the export stream. The
/// four places the engines differ are branched here and nowhere else.
/// </summary>
internal static class PointInTime
{
    public static async Task<string> OpenAsync(
        IOpenSearchLowLevelClient client, string index, TimeSpan keepAlive, SearchEngineFlavour flavour, CancellationToken cancellationToken)
    {
        StringResponse response;
        string idKey;
        if (flavour == SearchEngineFlavour.Elasticsearch)
        {
            // The generic call refuses a query string in the path; the keep_alive parameter is the same one OpenSearch's call takes.
            response = await client
                .DoRequestAsync<StringResponse>(
                    global::OpenSearch.Net.HttpMethod.POST, $"/{Uri.EscapeDataString(index)}/_pit", cancellationToken,
                    requestParameters: new CreatePitRequestParameters { KeepAlive = keepAlive })
                .ConfigureAwait(false);
            idKey = "id";
        }
        else
        {
            response = await client
                .CreatePitAsync<StringResponse>(index, new CreatePitRequestParameters { KeepAlive = keepAlive }, cancellationToken)
                .ConfigureAwait(false);
            idKey = "pit_id";
        }

        OpenSearchGridQuery.RequireSuccess(response);

        using var document = JsonDocument.Parse(response.Body!);
        return document.RootElement.GetProperty(idKey).GetString()!;
    }

    /// <summary>Best-effort: an unclosed point-in-time merely costs cluster memory until its own keep-alive lapses, so a
    /// failure here never fails the caller. <c>CancellationToken.None</c> goes to the close call itself: a caller whose own
    /// request was cancelled still wants the point-in-time closed, not a second cancellation racing the first.</summary>
    public static async Task TryCloseAsync(IOpenSearchLowLevelClient client, string pitId, SearchEngineFlavour flavour)
    {
        if (string.IsNullOrEmpty(pitId))
        {
            return;
        }

        try
        {
            if (flavour == SearchEngineFlavour.Elasticsearch)
            {
                var body = new JsonObject { ["id"] = pitId };
                await client
                    .DoRequestAsync<StringResponse>(
                        global::OpenSearch.Net.HttpMethod.DELETE, "/_pit", CancellationToken.None, PostData.String(body.ToJsonString()))
                    .ConfigureAwait(false);
            }
            else
            {
                var body = new JsonObject { ["pit_id"] = new JsonArray(pitId) };
                await client.DeletePitAsync<StringResponse>(PostData.String(body.ToJsonString()), null, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // Swallowed by design (best-effort) -- see this method's own remarks.
        }
    }
}
