// Demo API: AG Grid server-side row model requests in, PostgreSQL or MongoDB queries out, through the Craticula library.
//
//   docker compose up -d                  (in examples/demo: PostgreSQL 16 on :55433, MongoDB 7 on :57017)
//   dotnet run --project Api -- seed      (fills both databases with 20,000 synthetic orders, then exits)
//   dotnet run --project Api              (the API on http://127.0.0.1:5080; override the databases with DEMO_PG / DEMO_MONGO)
//
// Routes: POST /api/{postgres|mongo}/rows | values | export.csv | query, and GET /api/health.
using Demo.Api;
using Demo.Api.Seed;

// `seed` fills both databases and exits; anything else starts the API.
if (args.Length > 0 && args[0] == "seed")
{
    return await SeedCommand.RunAsync();
}

DemoApp.Build(args).Run();
return 0;
