using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Demo.Api;
using Demo.Api.Grid;
using Demo.Api.Seed;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Demo.Api.Tests;

/// <summary>Starts the real API (same <see cref="DemoApp.Build"/> as Program.cs) on a free loopback port, once for the
/// whole test run, and talks to it over HTTP. No test-host package is needed.</summary>
public sealed class ApiHostFixture : IAsyncLifetime
{
    private WebApplication? app;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>The temporary SQLite file this host serves as <c>sqlite</c>; seeded before the host starts, deleted on dispose.</summary>
    public string SqlitePath { get; } = Path.Combine(Path.GetTempPath(), $"craticula-demo-test-{Guid.NewGuid():N}.sqlite");

    public async Task InitializeAsync()
    {
        // SQLite needs no server, so every test run has the sqlite provider; the file never touches the demo's data/ folder.
        await SqliteSeeder.SeedAsync(SqlitePath, OrderGenerator.Generate());
        app = DemoApp.Build([], builder =>
        {
            builder.Configuration["Urls"] = "http://127.0.0.1:0";
            builder.Configuration[DemoSettings.SqliteEnvVar] = SqlitePath;
        });
        await app.StartAsync();
        Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await app!.StopAsync();
        await app.DisposeAsync();
        File.Delete(SqlitePath);
    }

    /// <summary>A second API whose PostgreSQL data source points at a port nothing listens on.</summary>
    public static async Task<(WebApplication App, HttpClient Client)> StartWithClosedPostgresAsync()
    {
        var closedPort = FreePort();
        var app = DemoApp.Build([], builder =>
        {
            builder.Configuration["Urls"] = "http://127.0.0.1:0";
            // The last NpgsqlDataSource registration is the one PostgresGridService receives.
            builder.Services.AddSingleton(NpgsqlDataSource.Create(
                $"Host=127.0.0.1;Port={closedPort};Username=demo;Password=demo;Database=demo;Timeout=2"));
        });
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    /// <summary>A host with extra optional providers and any configuration the test sets (for example a provider's setting),
    /// without touching the real environment variables. No database is needed to start it.</summary>
    public static async Task<(WebApplication App, HttpClient Client)> StartAsync(
        IReadOnlyList<OptionalProvider>? optionalProviders = null, Action<WebApplicationBuilder>? configure = null)
    {
        var app = DemoApp.Build([], builder =>
        {
            builder.Configuration["Urls"] = "http://127.0.0.1:0";
            configure?.Invoke(builder);
        }, optionalProviders);
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> PostJsonAsync(string provider, string route, string body)
    {
        using var response = await Client.PostAsync(
            $"/api/{provider}/{route}", new StringContent(body, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    public async Task<(HttpStatusCode Status, string ContentType, string? Disposition, byte[] Bytes)> PostRawAsync(
        string provider, string route, string body)
    {
        using var response = await Client.PostAsync(
            $"/api/{provider}/{route}", new StringContent(body, Encoding.UTF8, "application/json"));
        return (
            response.StatusCode,
            response.Content.Headers.ContentType?.ToString() ?? "",
            response.Content.Headers.ContentDisposition?.ToString(),
            await response.Content.ReadAsByteArrayAsync());
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiHostFixture>;
