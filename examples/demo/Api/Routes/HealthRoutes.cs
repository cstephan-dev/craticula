using Demo.Api.Grid;

namespace Demo.Api.Routes;

public static class HealthRoutes
{
    public static void Map(IEndpointRouteBuilder app) =>
        // Checks nothing heavy: no database round trip. It only says which providers this host serves.
        app.MapGet("/api/health", (ProviderRegistry providers) =>
            Results.Json(new { status = "ok", providers = providers.Names.Order() }));
}
