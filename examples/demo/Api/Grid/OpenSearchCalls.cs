using Craticula.OpenSearch.Client;
using OpenSearch.Net;

namespace Demo.Api.Grid;

/// <summary>
/// The low-level client does not throw for a failed call: it returns a response with the status and the transport exception. This turns
/// a failed response into the one exception type the adapter itself throws for a failed call
/// (<see cref="OpenSearchClientCompilationException"/>, with the status and the transport exception inside), so that the set-values reader and the seeder
/// fail the same way as the adapter and <c>EngineFailures</c> recognises them in one place.
/// </summary>
public static class OpenSearchCalls
{
    public static void Require(StringResponse response, string action)
    {
        if (response.HttpStatusCode is >= 200 and < 300)
        {
            return;
        }

        var body = response.Body ?? "";
        throw new OpenSearchClientCompilationException(
            $"{action} failed (HTTP {response.HttpStatusCode?.ToString() ?? "n/a"}): {(body.Length <= 300 ? body : body[..300] + "...")}",
            response.OriginalException ?? new InvalidOperationException("The call did not succeed."),
            response.HttpStatusCode);
    }
}
