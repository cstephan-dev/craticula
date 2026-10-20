using System.Net;
using System.Text;
using System.Text.Json;
using Demo.Api.Grid;

namespace Demo.Api.Tests;

/// <summary>
/// The HTTP translation that lets the library's OpenSearch client use Elasticsearch 8 point-in-time (<see cref="ElasticsearchPitConnection"/>),
/// proven offline against a stub that records what the client would have sent. The same calls against a real cluster are the
/// <see cref="ElasticsearchProviderTests"/>.
/// </summary>
public sealed class ElasticsearchPitTranslationTests
{
    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        public string? SeenBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            SeenBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<(HttpResponseMessage Response, Stub Inner)> SendAsync(
        HttpMethod method, string url, string? body, HttpStatusCode status, string answer)
    {
        var inner = new Stub(status, answer);
        using var invoker = new HttpMessageInvoker(new ElasticsearchPitConnection.TranslatingHandler(inner));
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return (await invoker.SendAsync(request, CancellationToken.None), inner);
    }

    [Fact]
    public async Task CreatingAPointInTime_GoesToThePitUrl_AndTheAnswersIdBecomesPitId()
    {
        var (response, inner) = await SendAsync(
            HttpMethod.Post, "http://127.0.0.1:9200/orders/_search/point_in_time?keep_alive=300s", null,
            HttpStatusCode.OK, """{"id":"abc==","_shards":{"total":1}}""");

        Assert.Equal(HttpMethod.Post, inner.Seen!.Method);
        Assert.Equal("/orders/_pit", inner.Seen.RequestUri!.AbsolutePath);
        Assert.Equal("?keep_alive=300s", inner.Seen.RequestUri.Query);
        using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("abc==", answer.RootElement.GetProperty("pit_id").GetString());
        Assert.False(answer.RootElement.TryGetProperty("id", out _), "the OpenSearch key replaces the Elasticsearch one");
        Assert.Equal(1, answer.RootElement.GetProperty("_shards").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task AFailedCreate_PassesThroughUntouched_WithItsStatusAndBody()
    {
        const string error = """{"error":{"type":"index_not_found_exception"},"status":404}""";
        var (response, _) = await SendAsync(
            HttpMethod.Post, "http://127.0.0.1:9200/orders/_search/point_in_time?keep_alive=300s", null, HttpStatusCode.NotFound, error);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(error, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ClosingAPointInTime_GoesToThePitUrl_WithTheElasticsearchBody()
    {
        var (_, inner) = await SendAsync(
            HttpMethod.Delete, "http://127.0.0.1:9200/_search/point_in_time", """{"pit_id":["abc=="]}""",
            HttpStatusCode.OK, """{"succeeded":true,"num_freed":1}""");

        Assert.Equal(HttpMethod.Delete, inner.Seen!.Method);
        Assert.Equal("/_pit", inner.Seen.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(inner.SeenBody!);
        Assert.Equal("abc==", body.RootElement.GetProperty("id").GetString());
        Assert.False(body.RootElement.TryGetProperty("pit_id", out _));
    }

    /// <summary>A base path in the setting (<c>http://proxy/es/</c>) is kept for both calls: the close used to be matched on the exact path and left untranslated.</summary>
    [Fact]
    public async Task WithABasePath_BothCallsAreTranslated_AndTheBasePathIsKept()
    {
        var (_, created) = await SendAsync(
            HttpMethod.Post, "http://proxy/es/orders/_search/point_in_time?keep_alive=300s", null, HttpStatusCode.OK, """{"id":"abc"}""");
        var (_, closed) = await SendAsync(
            HttpMethod.Delete, "http://proxy/es/_search/point_in_time", """{"pit_id":["abc"]}""", HttpStatusCode.OK, """{"succeeded":true}""");

        Assert.Equal("/es/orders/_pit", created.Seen!.RequestUri!.AbsolutePath);
        Assert.Equal("/es/_pit", closed.Seen!.RequestUri!.AbsolutePath);
        Assert.Equal("""{"id":"abc"}""", closed.SeenBody);
    }

    [Theory]
    [InlineData("""{"pit_id":["x"]}""")]
    [InlineData("""{"pit_id":"x"}""")]
    public void TheCloseBody_AcceptsAnArrayOrAString(string openSearchBody) =>
        Assert.Equal("""{"id":"x"}""", ElasticsearchPitConnection.TranslatingHandler.ToElasticsearchClose(openSearchBody));

    [Theory]
    [InlineData("POST", "http://127.0.0.1:9200/orders/_search", """{"size":10}""")]
    [InlineData("POST", "http://127.0.0.1:9200/_search", """{"pit":{"id":"abc","keep_alive":"5m"},"search_after":[1]}""")]
    [InlineData("POST", "http://127.0.0.1:9200/orders/_bulk?refresh=true", "{}")]
    [InlineData("GET", "http://127.0.0.1:9200/_cluster/health", null)]
    [InlineData("DELETE", "http://127.0.0.1:9200/orders", null)]
    public async Task EveryOtherRequest_IsForwardedUntouched(string method, string url, string? body)
    {
        var (response, inner) = await SendAsync(new HttpMethod(method), url, body, HttpStatusCode.OK, """{"id":"keep-me","hits":{}}""");

        Assert.Equal(new Uri(url), inner.Seen!.RequestUri);
        Assert.Equal(body, inner.SeenBody);
        Assert.Equal("""{"id":"keep-me","hits":{}}""", await response.Content.ReadAsStringAsync());
    }
}
