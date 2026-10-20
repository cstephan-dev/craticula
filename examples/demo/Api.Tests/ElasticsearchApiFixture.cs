using Demo.Api.Seed;
using Microsoft.AspNetCore.Builder;

namespace Demo.Api.Tests;

/// <summary>
/// An API host that serves <c>elasticsearch</c>: <c>DEMO_ELASTICSEARCH</c> is taken from the environment (through the host's
/// configuration) when the engine answers, so the main <see cref="ApiHostFixture"/> keeps listing the always-on providers.
/// Without the engine the host is not started and the tests that need it are skipped by <see cref="RequiresEngineFactAttribute"/>.
/// </summary>
public sealed class ElasticsearchApiFixture : IAsyncLifetime
{
    private WebApplication? app;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (EngineProbe.SkipReason(DemoSettings.ElasticsearchEnvVar) is not null)
        {
            return;
        }

        var setting = Environment.GetEnvironmentVariable(DemoSettings.ElasticsearchEnvVar)!;
        (app, Client) = await ApiHostFixture.StartAsync(null, builder => builder.Configuration[DemoSettings.ElasticsearchEnvVar] = setting);
    }

    public async Task DisposeAsync()
    {
        if (app is null)
        {
            return;
        }

        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

[CollectionDefinition("api-elasticsearch")]
public sealed class ElasticsearchApiCollection : ICollectionFixture<ElasticsearchApiFixture>;
