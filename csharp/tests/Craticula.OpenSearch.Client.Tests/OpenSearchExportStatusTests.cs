using System.Text;
using Craticula.Core.Ir;
using OpenSearch.Client;
using OpenSearch.Net;

namespace Craticula.OpenSearch.Client.Tests;

/// <summary>
/// Bug #2523: <c>OpenSearchExportStream</c> built its failure exception without the HTTP status that
/// <c>OpenSearchGridQuery</c> already passes, so a host could not tell an overloaded cluster (503/429: retry
/// later, answer "unavailable") from a bug (500). OFFLINE: the low-level client runs on an in-memory
/// connection that answers a fixed status, so no engine is needed (unlike the rest of this project, which is
/// excluded from the offline suite because it needs a live cluster; run these with
/// <c>dotnet test --filter OpenSearchExportStatusTests</c>).
/// </summary>
public sealed class OpenSearchExportStatusTests
{
    private static IOpenSearchLowLevelClient ClientAnswering(int status) =>
        new OpenSearchClient(new ConnectionSettings(
            new SingleNodeConnectionPool(new Uri("http://localhost:9200")),
            new InMemoryConnection(Encoding.UTF8.GetBytes("{\"error\":\"overloaded\"}"), status))).LowLevel;

    private static async Task<OpenSearchClientCompilationException> ExportFailureAsync(int status)
    {
        var fixture = FixtureRequest.Load("page-export", FixtureRequest.ResolveDirectory("page-export", "008"));
        var request = fixture.Request with { Page = new NonePage() };
        return await Assert.ThrowsAsync<OpenSearchClientCompilationException>(async () =>
        {
            await foreach (var _ in OpenSearchExportStream.StreamAsync(ClientAnswering(status), request))
            {
            }
        });
    }

    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(404)]
    public async Task A_Failed_Export_Call_Carries_The_Http_Status_Of_The_Response(int status)
    {
        var failure = await ExportFailureAsync(status);

        Assert.Equal(status, failure.HttpStatusCode);
        Assert.Contains($"HTTP {status}", failure.Message, StringComparison.Ordinal);
    }
}
