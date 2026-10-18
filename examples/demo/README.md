# Craticula demo

One AG Grid page that reads the same 20,000 synthetic orders from PostgreSQL (through Dapper or through Entity Framework Core) or MongoDB. A small C# API turns each grid
request into a native query with the Craticula library. To run it (three terminals, from this folder):

```bash
docker compose up -d && dotnet run --project Api -- seed   # terminal 1: databases, then fill them
dotnet run --project Api                                   # terminal 2: the API on http://127.0.0.1:5080
cd web && npm install && npm run dev                       # terminal 3: the page on http://localhost:5173
```

It works when http://localhost:5173 shows a grid with rows, and `bash smoke.sh` (run in a fourth terminal, while the API
is up) ends with `all 24 scenarios are equal on all 3 providers (postgres, mongo, ef)`.

This is a showcase, not a published package. It references the library by project reference (`../../csharp/src`) until
the first release is on nuget.org. It is not part of `csharp/Craticula.sln`.

## A second page: AG Grid Community

http://localhost:5173/community.html is the same data on the **free AG Grid Community** edition (Infinite Row Model): no
license key, no watermark, no grouping, aggregation or set filter (those are Enterprise features). It sends the flat request
Community sends (`startRow`, `endRow`, `sortModel`, `filterModel`) and the API compiles it like any other. **Copy request** puts
the exact request body the grid sent on the clipboard: that is the first step of turning a real payload into a fixture (the columns
are the demo's, so the steps in `spec/fixtures/community-infinite/README.md` say how to map them).

## The "In range" filter

Both pages offer AG Grid's "In range" option on the number and date columns. The library does not accept a native range: it carries no
inclusivity, and guessing one would be silently wrong at the boundary (SEM-4). So the page translates it into the library's
`customFilters` form before it sends the request:

- **Both bounds are exclusive**, `from < value < to`. That is AG Grid's own client-side default (`inRangeInclusive: false`), so
  the demo shows what a client-side grid would show on the same data. (On the date-time column the page reads the date as UTC
  midnight, the zone the API uses; a client-side grid compares with the browser's local midnight, so the two agree in a UTC browser.)
- **One bound or none:** AG Grid itself applies "In range" only when both bounds are filled in (a half-filled range is not sent at all).
  For a filter model set through the grid's API with only one bound, the page sends `value > from` or `value < to`; with no bound
  it drops the condition.
- **Combined with other conditions:** joined by AND, the other conditions stay in the normal filter and only the range is translated.
  Joined by **OR**, the page shows an `unsupported-filter` message instead of guessing; use AND.
- **Types:** the bound is typed from the column. A whole-number column (`quantity`, `id`) needs whole numbers; `amount` takes plain
  decimals; a `date` column takes `YYYY-MM-DD` (or a midnight time; any other time is refused, never cut off); a `datetime` column reads the date as UTC, the zone the demo API uses.
- A native in-range request sent straight to the API (without the page's translation) is refused with `unknown-operator`; `smoke.sh` shows it.

The translation is in `web/src/inRange.ts`, shared by both pages and the CSV export.

## Prerequisites

- Docker with the Compose plugin
- .NET 10 SDK
- Node 20.19 or newer
- Free ports: 55433 (PostgreSQL), 57017 (MongoDB), 5080 (API), 5173 (page)

All connection values have defaults that match `docker-compose.yml`, so no environment variable is needed. The databases
use throw-away credentials (`demo` / `demo`) and listen on `127.0.0.1` only.

### Change a port

| Port | How |
|---|---|
| PostgreSQL, MongoDB | `DEMO_PG_PORT=55434 DEMO_MONGO_PORT=57018 docker compose up -d`, then give the same ports to the seed and the API with `DEMO_PG="Host=127.0.0.1;Port=55434;Username=demo;Password=demo;Database=demo"` and `DEMO_MONGO="mongodb://127.0.0.1:57018"` |
| API | `dotnet run --project Api --urls http://127.0.0.1:5081`, then change the `/api` target in `web/vite.config.ts` and pass `--api http://127.0.0.1:5081` to `smoke.sh` |
| Page | `npm run dev -- --port 5174` (the API's CORS list only matters if you skip the dev proxy) |

## Run it, step by step

1. Start the databases and wait until both are healthy:

   ```bash
   docker compose up -d
   docker compose ps          # postgres and mongo show "healthy" after about 10 seconds
   ```

2. Fill them. This drops and recreates the table and the collection, so you can run it as often as you like:

   ```bash
   dotnet run --project Api -- seed
   ```

3. Start the API (leave it running). `curl http://127.0.0.1:5080/api/health` answers `{"status":"ok",...}`:

   ```bash
   dotnet run --project Api
   ```

4. Start the page (leave it running) and open http://localhost:5173:

   ```bash
   cd web
   npm install
   npm run dev
   ```

5. Optional check without a browser: `bash smoke.sh` posts 24 fixed requests (`smoke/*.json`) to all three providers and prints
   one row per scenario. Exit code 0 means every answer is equal; 1 means a difference or a missing `400`; 2 means the
   environment is wrong (API down, a database down). `bash smoke.sh --api URL` points it at another API.

## Click-through checklist

Do these in the browser. Each should work on all three providers (PostgreSQL is the Dapper route, EF Core is the Entity Framework route).

1. **Switch provider.** Use the PostgreSQL / MongoDB / EF Core switch in the header. Apply a filter and a sort, then switch: the same
   rows appear.
2. **Sort.** Click a column header (Amount, Ordered at). Shift-click a second column to sort by both.
3. **Filters, one per type.** Text (Customer contains `GmbH`), number (Quantity greater than 8), date (Shipped on, Ordered
   at), boolean (Priority: tick `true`).
4. **Set filter.** Open the Country filter. The values come from the database. Untick some and see the grid narrow.
5. **Group.** Drag Country, then Category, into the row group bar above the grid. Drag Amount and Quantity into the values
   area and choose sum, avg and count. Open a group to see its children.
6. **Scroll or pages.** Scroll down in a long list. Blocks of 100 rows load as you go; the row count stays right. Click Pages
   to switch to numbered pages instead (100 rows each, with the total count and the page bar at the bottom). A filter or a
   sort returns to page 1. Click Pages again to go back to scrolling; your filters, sort and groups stay as they are.
7. **CSV export.** With a filter and sort active, click Export CSV. The file holds the filtered, sorted rows. A cell that
   starts with `=` is saved with a leading `'` so a spreadsheet does not run it as a formula.
8. **Query panel.** Click Query panel. It shows the SQL (PostgreSQL) or the aggregation pipeline (MongoDB) with its
   parameters for the current view.

## What to expect

- **A watermark.** The server-side row model is an AG Grid Enterprise feature. There is no license key in this repository,
  so the grid runs in evaluation mode: a watermark and console warnings are normal. To use your own key, create
  `web/.env.local` (git ignores it) with `VITE_AG_GRID_LICENSE=<your key>`.
- **No tree data, master/detail or pivot.** The library does not support them, so the page does not offer them: the Columns
  panel has no Pivot Mode switch. A pivot request sent straight to the API is refused with `malformed-request`.
- **"In range" with OR.** "In range" works (see the section above), but a range joined to another condition by OR is refused with
  an `unsupported-filter` message; join them with AND.

## Using Entity Framework Core (the `ef` provider)

The third provider runs the same grid through `Craticula.EntityFrameworkCore`: the library turns the request into an
`IQueryable` over your `DbContext`, and the Npgsql EF provider writes the SQL. This is the route to take if your code
already works with a `DbContext` (as it would with OData). The code is `Api/Grid/OrdersDbContext.cs` and `Api/Grid/EfGridService.cs`.

**1. The context.** The demo builds its EF model from the grid schema (`orders.schema.json`) with `GridSchemaPropertyBag`, so there is
no entity class: columns come from each field's `physical.sql.column`. The context implements `IGridSchemaModel`, and the
registration replaces EF's model-cache key factory so the cache is keyed by the schema:

```csharp
services.AddDbContextFactory<OrdersDbContext>((sp, o) => o
    .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
    .ReplaceService<IModelCacheKeyFactory, GridSchemaModelCacheKeyFactory>());
```

Use a factory, not a scoped context: the grid services are singletons. Also set `NonRetryingExecutionStrategy`
(`o.UseNpgsql(dataSource, n => n.ExecutionStrategy(d => new NonRetryingExecutionStrategy(d)))`): the Npgsql provider's default strategy
wraps a connection failure in an `InvalidOperationException` even without `EnableRetryOnFailure`, and a database that is down would then
answer 500 instead of 503.

**2. The engine facts.** The adapter cannot read these from EF, so you declare them once for PostgreSQL
(`OrdersDbContext.PostgresSemantics`; the library ships only a SQLite preset):

| Setting | Value on PostgreSQL | Why |
|---|---|---|
| `CaseInsensitiveCollation` | `craticula_ci` | the ICU collation the seed creates (`CREATE COLLATION IF NOT EXISTS craticula_ci ...`) |
| `CaseSensitiveCollation` | none | PostgreSQL's default collation already distinguishes case |
| `PatternMatchFoldsCase` | `false` | PostgreSQL `LIKE` never folds case; the adapter emits `lower(column) LIKE` |
| `PatternMatchTreatsLeftBracketAsMetacharacter` | `false` | only SQL Server does |
| `EmptyStringIsNull` | `false` | only Oracle does |

**3. The call.**

```csharp
await using var context = await contexts.CreateDbContextAsync(ct);
var page = await EfCoreGridQuery.FetchPageAsync(
    GridSchemaPropertyBag.Query(context, schema),   // the IQueryable
    new EfCoreQueryRequest { Schema = schema, Filter = filter, Sort = sort, Page = page, Group = group, Aggregations = aggregations },
    OrdersDbContext.PostgresSemantics, includeRowCount: true, ct);
```

CSV export uses `EfCoreExportStream.StreamAsync` the same way.

**What differs from the Dapper (PostgreSQL) route**

- **Query panel.** The adapter builds expression trees, not SQL text, so the panel shows EF's `ToQueryString()`. EF writes parameter values
  into that text, so it is not a parameterized statement like the PostgreSQL and MongoDB views, and the panel says so. Grouped
  and drill-down requests are composed inside the adapter, and the panel shows only a one-line comment for them.
- **Set filter.** The EF adapter has no distinct-values call, so the set filter on `ef` reuses the compiled statement of the Dapper route and
  runs it through Npgsql (`Api/Grid/PostgresSetValues.cs`).
- **Case-insensitive search.** "Contains", "starts with" and "ends with" use `lower(column) LIKE` on `ef` and `ILIKE` on the Dapper route.
  They agree on ASCII and on ordinary Unicode case pairs (the API tests cover a non-ASCII case; the smoke scenarios are ASCII) and can differ under an exotic collation.
- **Library version.** The EF adapter is new and, apart from this demo, has only been run against SQLite in the library's tests. The demo's
  smoke test is what shows EF and Dapper return the same rows, groups and CSV on this data.

**Package.** The demo adds `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 (central version in `Directory.Packages.props`). It is a new library and
needs the Engineering Lead's approval (compliance rule 4); that approval is still pending at the time of writing.

## Stop and reset

```bash
docker compose stop          # stop the databases, keep the data
docker compose down -v       # remove the databases and their data
docker compose up -d && dotnet run --project Api -- seed   # start again from a clean state
```

Stop the API and the page with Ctrl+C in their terminals.

## Troubleshooting

| What you see | What to do |
|---|---|
| `port is already allocated` or `address already in use` | Find the owner with `ss -ltnp \| grep :5080` (or `lsof -i :5080`), stop it, or change the port (see above). |
| `Cannot connect to the Docker daemon` | Start Docker Desktop or the Docker service, then run `docker compose up -d` again. |
| The grid is empty | The seed has not run, or you ran `docker compose down -v`. Run `dotnet run --project Api -- seed`. |
| A red banner with `backend-unavailable` (HTTP 503) | The database for that provider is down or unreachable. Check `docker compose ps`, then reload. |
| `API: unreachable` in the header | The API is not running on port 5080. Start it (step 3). |
| A watermark and console warnings | Normal: AG Grid Enterprise evaluation mode (see above). |
| `smoke.sh` exits 2 | Read its message: it names the API address or the provider that failed. |

## Where the code is

| Path | What |
|---|---|
| `Api/` | ASP.NET Core minimal API: routes (`rows`, `values`, `export.csv`, `query`, `health`), the library calls (`Api/Grid/`: `PostgresGridService` for Dapper, `EfGridService` and `OrdersDbContext` for Entity Framework Core, `MongoGridService`), the seed (`Api/Seed/`) |
| `Api.Tests/` | xUnit tests that start the real API against both databases (they skip when the databases are down) |
| `web/` | Vite + React + TypeScript page (see [web/README.md](web/README.md) for the AG Grid version and the page's requests) |
| `orders.schema.json` | The GridSchema: the columns the grid may touch, with their SQL and MongoDB mappings |
| `smoke.sh`, `smoke/` | The cross-provider check and its request files |
| `docker-compose.yml` | PostgreSQL 16 and MongoDB 7 |

To run the API tests: `dotnet test Api.Tests` (needs the seeded databases from step 1 and 2).
