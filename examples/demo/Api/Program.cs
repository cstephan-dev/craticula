// Demo API: AG Grid server-side row model requests in, PostgreSQL, MongoDB or SQLite queries out, through the Craticula library.
//
//   docker compose up -d                  (in examples/demo: PostgreSQL 16 on :55433, MongoDB 7 on :57017)
//   dotnet run --project Api -- seed      (fills PostgreSQL, MongoDB and the SQLite file with 20,000 synthetic orders, then exits;
//                                          `seed mongo` or `seed postgres sqlite` fills only the named providers)
//   dotnet run --project Api              (the API on http://127.0.0.1:5080; override the databases with DEMO_PG / DEMO_MONGO, the SQLite file with DEMO_SQLITE)
//
// Routes: POST /api/{postgres|ef|mongo|sqlite}/rows | values | export.csv | query, and GET /api/health.
using Demo.Api;
using Demo.Api.Seed;

// `seed [provider ...]` fills the databases and exits; anything else starts the API.
if (args.Length > 0 && args[0] == "seed")
{
    return await SeedCommand.RunAsync(args[1..]);
}

DemoApp.Build(args).Run();
return 0;
