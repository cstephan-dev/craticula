using System.Net;
using System.Text;
using System.Text.Json;
using Craticula.Core.Export;
using Craticula.Core.Grid;
using Demo.Api.Errors;
using Demo.Api.Grid;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Demo.Api.Tests;

/// <summary>
/// The optional-provider mechanism, proven with a fake engine: no database is needed. A test adds the fake through
/// <c>DemoApp.Build</c>'s <c>extraOptionalProviders</c> parameter and sets (or leaves out) its setting through
/// <c>builder.Configuration</c>, so the real environment variables are never touched and tests cannot leak into each other.
/// </summary>
public sealed class OptionalProviderTests
{
    private const string FakeEnvVar = "DEMO_FAKE_ENGINE";

    private static readonly OptionalProvider Fake = new(
        "fake", FakeEnvVar, (services, setting) => services.AddSingleton<IGridService>(new FakeGridService(setting)));

    private static async Task<string[]> HealthAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/health");
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. json.GetProperty("providers").EnumerateArray().Select(p => p.GetString()!)];
    }

    [Fact]
    public async Task Health_WithoutAnOptionalSetting_ListsOnlyTheAlwaysOnProviders()
    {
        var (app, client) = await ApiHostFixture.StartAsync([Fake]);
        await using (app)
        using (client)
        {
            Assert.Equal(["ef", "mongo", "postgres", "sqlite"], await HealthAsync(client));
        }
    }

    [Fact]
    public async Task Health_WithABlankOptionalSetting_StillListsOnlyTheAlwaysOnProviders()
    {
        var (app, client) = await ApiHostFixture.StartAsync([Fake], builder => builder.Configuration[FakeEnvVar] = "  ");
        await using (app)
        using (client)
        {
            Assert.Equal(["ef", "mongo", "postgres", "sqlite"], await HealthAsync(client));
        }
    }

    [Fact]
    public async Task Health_WithTheOptionalSetting_ListsTheFakeProvider()
    {
        var (app, client) = await ApiHostFixture.StartAsync([Fake], builder => builder.Configuration[FakeEnvVar] = "fake://somewhere");
        await using (app)
        using (client)
        {
            Assert.Equal(["ef", "fake", "mongo", "postgres", "sqlite"], await HealthAsync(client));
        }
    }

    [Fact]
    public async Task TheRealOptionalList_IsEmptyUntilAnEngineTaskAddsOne()
    {
        // Documents the extension point: the shipped list has no entry, so a default host serves exactly the four always-on providers.
        Assert.Empty(OptionalProviders.All);
        var (app, client) = await ApiHostFixture.StartAsync();
        await using (app)
        using (client)
        {
            Assert.Equal(["ef", "mongo", "postgres", "sqlite"], await HealthAsync(client));
        }
    }

    [Fact]
    public async Task AnExceptionDeclaredAsAnEngineFailure_Answers503BackendUnavailable()
    {
        EngineFailures.Declare<FakeEngineDownException>();
        var (app, client) = await ApiHostFixture.StartAsync([Fake], builder => builder.Configuration[FakeEnvVar] = "fake://somewhere");
        await using (app)
        using (client)
        {
            using var response = await client.PostAsync("/api/fake/rows", new StringContent(Requests.PageSize(10), Encoding.UTF8, "application/json"));
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("backend-unavailable", json.GetProperty("error").GetProperty("kind").GetString());
        }
    }

    [Fact]
    public async Task AnUndeclaredException_StillAnswers500()
    {
        var (app, client) = await ApiHostFixture.StartAsync(
            [Fake], builder => builder.Configuration[FakeEnvVar] = FakeGridService.ThrowUndeclared);
        await using (app)
        using (client)
        {
            using var response = await client.PostAsync("/api/fake/rows", new StringContent(Requests.PageSize(10), Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
    }

    [Fact]
    public async Task MongoDown_Answers503BackendUnavailable()
    {
        var port = ApiHostFixture.FreePort();
        var (app, client) = await ApiHostFixture.StartAsync(null, builder =>
            builder.Services.AddSingleton(new MongoDB.Driver.MongoClient(
                $"mongodb://127.0.0.1:{port}/?serverSelectionTimeoutMS=1000&connectTimeoutMS=1000").GetDatabase("demo")));
        await using (app)
        using (client)
        {
            using var response = await client.PostAsync("/api/mongo/rows", new StringContent(Requests.PageSize(10), Encoding.UTF8, "application/json"));
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("backend-unavailable", json.GetProperty("error").GetProperty("kind").GetString());
        }
    }

    [Fact]
    public void EngineFailures_RecognisesTheBuiltInConnectionExceptions()
    {
        Assert.True(EngineFailures.IsUnavailable(new Npgsql.NpgsqlException("down")));
        Assert.True(EngineFailures.IsUnavailable(new MongoDB.Driver.MongoClientException("down")));
        Assert.True(EngineFailures.IsUnavailable(new TimeoutException()));
        Assert.True(EngineFailures.IsUnavailable(new System.Net.Sockets.SocketException()));
        Assert.False(EngineFailures.IsUnavailable(new InvalidOperationException()));
    }

    private sealed class FakeEngineDownException(string message) : Exception(message);

    private sealed class FakeGridService(string setting) : IGridService
    {
        public const string ThrowUndeclared = "throw-undeclared";

        public string Name => "fake";

        public Task<GridPageResult> FetchRowsAsync(PreparedRows request, CancellationToken cancellationToken) =>
            setting == ThrowUndeclared
                ? throw new InvalidOperationException("not an engine failure")
                : throw new FakeEngineDownException("the fake engine is down");

        public Task<IReadOnlyList<object?>> FetchSetValuesAsync(PreparedSetValues request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ExportRow> StreamExportAsync(PreparedRows request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public CompiledQueryView Compile(PreparedRows request) => throw new NotSupportedException();
    }
}
