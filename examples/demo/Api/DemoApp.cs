using Craticula.Core.Schema;
using Craticula.EntityFrameworkCore;
using Demo.Api.Errors;
using Demo.Api.Grid;
using Demo.Api.Routes;
using Demo.Api.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Driver;
using Npgsql;

namespace Demo.Api;

/// <summary>Builds the web application. Kept out of Program.cs so the tests can start the same host on a free port.</summary>
public static class DemoApp
{
    public const string SchemaFile = "orders.schema.json";

    private static readonly string[] AllowedOrigins =
        ["http://localhost:5173", "http://127.0.0.1:5173"];

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        // Loaded once; the schema is declared in a file and never taken from a request.
        var schema = GridSchema.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, SchemaFile)));
        var preparer = new GridRequestPreparer(schema, DemoLimits.Default);

        // Connections are lazy: the API starts with the databases down and answers 503 until they are up.
        var postgres = new NpgsqlConnectionStringBuilder(DemoSettings.Postgres) { Timeout = 5 };
        var mongoSettings = MongoClientSettings.FromConnectionString(DemoSettings.Mongo);
        mongoSettings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        mongoSettings.ConnectTimeout = TimeSpan.FromSeconds(3);

        builder.Services.AddSingleton(preparer);
        builder.Services.AddSingleton(NpgsqlDataSource.Create(postgres.ConnectionString));
        builder.Services.AddSingleton<IMongoDatabase>(
            new MongoClient(mongoSettings).GetDatabase(DemoSettings.MongoDatabase));
        builder.Services.AddSingleton(schema);
        // One context per call from a factory: the services are singletons. The same NpgsqlDataSource as the Dapper
        // provider; no EnableRetryOnFailure, so a down database stays an NpgsqlException (503) instead of a wrapped one.
        builder.Services.AddDbContextFactory<OrdersDbContext>((services, options) => options
            .UseNpgsql(services.GetRequiredService<NpgsqlDataSource>())
            .ReplaceService<IModelCacheKeyFactory, GridSchemaModelCacheKeyFactory>());
        builder.Services.AddSingleton<IGridService, PostgresGridService>();
        builder.Services.AddSingleton<IGridService, EfGridService>();
        builder.Services.AddSingleton<IGridService, MongoGridService>();
        builder.Services.AddSingleton<ProviderRegistry>();

        builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
            policy.WithOrigins(AllowedOrigins).WithMethods("GET", "POST").WithHeaders("Content-Type")));

        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 1_048_576);

        configure?.Invoke(builder);

        var app = builder.Build();

        app.UseMiddleware<ApiErrorMiddleware>();
        app.UseCors();

        HealthRoutes.Map(app);
        GridRoutes.Map(app);

        return app;
    }
}
