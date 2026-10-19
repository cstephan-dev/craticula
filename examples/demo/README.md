# Craticula demo

One AG Grid page that reads the same 20,000 synthetic orders from PostgreSQL (through Dapper or through Entity Framework Core), MongoDB or SQLite (a file, no container). A small C# API turns each grid
request into a native query with the Craticula library. To run it (three terminals, from this folder):

```bash
docker compose up -d && dotnet run --project Api -- seed   # terminal 1: databases, then fill them (PostgreSQL, MongoDB, the SQLite file)
dotnet run --project Api                                   # terminal 2: the API on http://127.0.0.1:5080
cd web && npm install && npm run dev                       # terminal 3: the page on http://localhost:5173
```

It works when http://localhost:5173 shows a grid with rows, and `bash smoke.sh` (run in a fourth terminal, while the API
is up) ends with `all 24 scenarios are equal on all 4 providers (postgres, ef, mongo, sqlite)`. (SQLite is a file the seed creates; see
[SQLite](#sqlite-the-sqlite-provider) for the two documented differences.) SQL Server and MySQL are optional providers that start only on request:
see [SQL Server](#sql-server-the-sqlserver-provider) and [MySQL](#mysql-the-mysql-provider).

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
- Free ports: 55433 (PostgreSQL), 57017 (MongoDB), 5080 (API), 5173 (page); 51433 (SQL Server) and 53306 (MySQL) only when you start those profiles

All connection values have defaults that match `docker-compose.yml`, so no environment variable is needed. The databases
use throw-away credentials (`demo` / `demo`) and listen on `127.0.0.1` only.

### Change a port

| Port | How |
|---|---|
| PostgreSQL, MongoDB | `DEMO_PG_PORT=55434 DEMO_MONGO_PORT=57018 docker compose up -d`, then give the same ports to the seed and the API with `DEMO_PG="Host=127.0.0.1;Port=55434;Username=demo;Password=demo;Database=demo"` and `DEMO_MONGO="mongodb://127.0.0.1:57018"` |
| SQL Server | `DEMO_SQLSERVER_PORT=51434 docker compose --profile sqlserver up -d`, then use the same port in `DEMO_SQLSERVER` (see [SQL Server](#sql-server-the-sqlserver-provider)) |
| MySQL | `DEMO_MYSQL_PORT=53307 docker compose --profile mysql up -d`, then use the same port in `DEMO_MYSQL` (see [MySQL](#mysql-the-mysql-provider)) |
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

5. Optional check without a browser: `bash smoke.sh` posts 24 fixed requests (`smoke/*.json`) to all four providers and prints
   one row per scenario. Exit code 0 means every answer is equal; 1 means a difference or a missing `400`; 2 means the
   environment is wrong (API down, a database down). `bash smoke.sh --api URL` points it at another API.

## Click-through checklist

Do these in the browser. Each should work on all the providers the API lists (PostgreSQL is the Dapper route, EF Core is the Entity Framework route).

1. **Switch provider.** Use the provider switch in the header (PostgreSQL, MongoDB, EF Core, SQLite). Apply a filter and a sort, then switch: the same
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

## SQLite (the `sqlite` provider)

The fourth provider reads the same orders from a **SQLite file** through `Craticula.Dapper` and `SqliteDialect`. There is no container and no
compose profile: SQLite is always on, and `/api/health` always lists `sqlite`. The code is `Api/Grid/SqliteGridService.cs`,
`Api/Seed/SqliteSeeder.cs` and `Api/Grid/SetValuesRunner.cs` (the compile-and-run helper `PostgresSetValues` now shares).

- **Where the file is.** `DEMO_SQLITE` if set, else `data/demo.sqlite` in this folder. `data/` is git-ignored: the file is generated, never commit it.
- **Create it.** `dotnet run --project Api -- seed sqlite` drops and recreates table `orders` (same rows and indexes as PostgreSQL) and prints
  `SQLite: 20000 rows in table orders (<path>).` Plain `seed` fills PostgreSQL, MongoDB and the SQLite file.
- **Missing file.** The API opens the file read-write but never creates it. If it is missing, or `seed sqlite` holds the lock, the provider answers
  503 `backend-unavailable` like a database that is down. Seed it, then reload.
- **Version.** SQLite 3.30 or later (the dialect's `NULLS FIRST/LAST`). The library bundled with `Microsoft.Data.Sqlite` is newer; the seed
  checks it and stops with a clear message if it is not.
- **Stored forms.** Dates are text `yyyy-MM-dd`, instants are text `yyyy-MM-ddTHH:mm:ss.fffZ` (exactly three digits and `Z`, UTC), booleans are 0 and 1,
  `amount` is a `NUMERIC` column.

**Two differences from PostgreSQL, both measured and both documented, not hidden**

1. **Floating-point sums.** SQLite stores `NUMERIC` as a floating-point number, so a grouped `amount` sum can differ from PostgreSQL's exact decimal in the last digits.
   `smoke.sh` compares keys ending `_sum` for `sqlite` (only) with a relative tolerance of 1e-9, `|a-b| <= 1e-9 * max(1, |a|, |b|)`; the average keeps its
   1e-6 tolerance, and every other value, the row counts and the CSV stay exact. When the tolerance was needed, `smoke.sh` prints one line saying where.
2. **ASCII-only case folding.** SQLite's `NOCASE` and `LIKE` fold ASCII letters only. A "contains" filter for `SÖHNE` finds `Müller, Söhne & Co` on PostgreSQL and
   not on SQLite (`Ö` and `ö` differ); `söhne` finds it on both. The smoke scenarios avoid this case; `Api.Tests` pins the actual behaviour.

**Package.** The demo adds `Microsoft.Data.Sqlite` 10.0.11 (the version the library's tests use; central version in `Directory.Packages.props`). It is a
new library, not named in the approved stack, and needs the Engineering Lead's approval (compliance rule 4); that approval is still pending at the time of writing.

## SQL Server (the `sqlserver` provider)

The fifth provider reads the same orders from **SQL Server 2022** through `Craticula.Dapper` and `MssqlDialect`. It is optional and heavy, so
it starts only on request and the API lists it only when `DEMO_SQLSERVER` is set. The code is `Api/Grid/SqlServerGridService.cs`,
`Api/Grid/SqlServerBinding.cs` (the `@p1..@pn` binding for the set-values call) and `Api/Seed/SqlServerSeeder.cs`.

**Licence.** `docker-compose.yml` sets `ACCEPT_EULA=Y`. **Running this profile accepts Microsoft's SQL Server licence** (Developer edition, free for
development and testing, not for production). If you do not accept it, do not start the profile. The `sa` password in the compose file
(`MSSQL_SA_PASSWORD`) is a local throw-away credential like the `demo` / `demo` ones; the port listens on `127.0.0.1` only.

**Cost.** The image is `mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04` (2.2 GB on disk, about 580 MB compressed to pull), the container wants about 2 GB of RAM, and it is ready a few seconds after it starts on a fast machine
(measured: 5 seconds from `up -d` to healthy; the healthcheck allows about three minutes for a slow one). Do not start it on a machine that is short of memory.

```bash
docker compose --profile sqlserver up -d --wait          # starts SQL Server only (the umbrella `engines` profile starts every optional engine: SQL Server and MySQL so far)
export DEMO_SQLSERVER="Server=127.0.0.1,51433;User Id=sa;Password=$(sed -n 's/.*MSSQL_SA_PASSWORD: *"\(.*\)"/\1/p' docker-compose.yml);Database=demo;TrustServerCertificate=true"
dotnet run --project Api -- seed sqlserver               # SQL Server: 20000 rows in table orders.
dotnet run --project Api                                 # /api/health now lists sqlserver
docker compose --profile sqlserver stop                  # when done: stops it, keeps the volume
```

- **The connection string.** `DEMO_SQLSERVER` has no default in code. `TrustServerCertificate=true` is needed because the container's certificate is
  self-signed and the driver encrypts by default (without it the login fails in the pre-login handshake, and the API answers 500, not 503, since it is a setting error). `Database=demo` is created by the seed.
  Change the port with `DEMO_SQLSERVER_PORT` on `docker compose` and in the string.
- **Seed.** `seed sqlserver` creates database `demo` if it is missing, drops and recreates table `orders` and loads the same rows with `SqlBulkCopy` in one transaction. It is not a default target:
  plain `seed` still fills PostgreSQL, MongoDB and the SQLite file.
- **Types.** `BIGINT`, `NVARCHAR(450)`, `DECIMAL(12,2)`, `DATETIME2(3)` (never the coarse `DATETIME`), `DATE`, `BIT`. The groupable columns (country, category, status) carry the binary collation
  `Latin1_General_100_BIN2` so group buckets and drill-down are exact (GRP-2); the other text keeps the server's case-insensitive default.
- **A malformed setting stops the API at startup** (`DEMO_SQLSERVER` or `DEMO_MYSQL` such as `Sever=x`): the message names the variable and does not repeat the string; `seed` reports that provider as failed and goes on. `ConnectRetryCount` is always forced to 0 for SQL Server, whatever the string says, so the 503 below is immediate after the timeout.
- **Stopped container.** The API answers 503 `backend-unavailable` after about 5 seconds (the connect timeout; a refused connection is retried until it expires).
  Only connection failures count (error numbers 2, 53, -1, -2, 258, 4060 and 11001, and a refused socket); any other SQL Server error is a 500.

**Differences from PostgreSQL, measured on SQL Server 2022 CU14 and handled in the open**

1. **`AVG` over a whole-number column returns an integer.** The library renders `AVG` bare, and SQL Server's `AVG(BIGINT)` is a `BIGINT`: the grouped `quantity` average is 25
   where PostgreSQL gives 25.418... `smoke.sh` (rule for `sqlserver` only, key `quantity_avg`) and `Api.Tests` require the baseline value truncated toward zero, exactly; the
   scenario "group by country" carries that average for this reason, and the line after the table names where the rule was used.
2. **`AVG` over the decimal column has six decimals** (12428.503751 against 12428.503751987...), inside the 1e-6 relative tolerance every provider's `_avg` already has.
3. **Datetimes need a stated kind.** `DATETIME2` has no zone, so the driver returns a `DateTime` of kind Unspecified and the JSON would lose the trailing `Z`.
   `SqlServerGridService` marks them UTC (the seed stores UTC instants), so the answer equals the others to the millisecond.
4. **CSV is walked a page at a time** (library defect #2510). `DapperExportStream`'s keyset query selects the sort key twice when it is also an export column (`SELECT [id], ..., [id] ... ORDER BY [id]`) and SQL Server
   answers error 209 "Ambiguous column name" for every export that includes `id`. This is a defect in the library's Dapper adapter on SQL Server (the Docker tier has no live
   test of it), reported with this demo; until it is fixed the service pages through the grid entry point (5,000 rows a chunk, ordered by the stable key). Rows and bytes equal the other providers.
5. **No difference for non-ASCII case.** `SÖHNE` and `söhne` both find `Müller, Söhne & Co`, as on PostgreSQL (the compiled `COLLATE Latin1_General_CI_AS` folds beyond ASCII; SQLite does not).

Set-values filters on dates and datetimes bind the values as strings, like the adapter, and SQL Server converts them (tested with a date and a datetime filter).

**Package.** The demo adds `Microsoft.Data.SqlClient` 7.1.0 (the library's version; central version in `Directory.Packages.props`) and pins `System.Security.Cryptography.Pkcs` 10.0.13, because SqlClient 7.1.0
depends on 9.0.18, which has a high and a moderate advisory (the restore fails with NU1903 otherwise; the library's integration tests carry the same pin). SqlClient is a new library for the demo, and its use under `examples/demo/Api` is
wider than the approved stack's wording for it (the integration tests); the Engineering Lead's approval is still pending at the time of writing.

## MySQL (the `mysql` provider)

An optional provider that reads the same orders from **MySQL 8.4** through `Craticula.Dapper` and `MySqlDialect`. It starts only on request and the API lists it only when
`DEMO_MYSQL` is set. The code is `Api/Grid/MySqlGridService.cs`, `Api/Grid/MySqlBinding.cs` (the bare-`?` binding for the set-values call) and `Api/Seed/MySqlSeeder.cs`.
**MariaDB is not covered**: its collations and some behaviour differ from MySQL's, and the library has no dialect fixtures for it. Use MySQL 8.4.

**Licence.** MySQL Community needs no licence acceptance. The `demo` / `demo` user in `docker-compose.yml` is a local throw-away credential like the PostgreSQL one; the port listens on `127.0.0.1` only,
and the root password is random and never used.

**Cost.** The image is `mysql:8.4.11` (the one the library's Docker tier uses; `docker images` shows 1.12 GB on disk). It was already on the machine where this was measured, so the pull time is not measured.
The first start of a new volume took 10.7 seconds from `up -d --wait` to healthy; a restart of the stopped container took 5.7 seconds; the idle container used about 425 MiB of RAM.

```bash
docker compose --profile mysql up -d --wait              # starts MySQL only (the umbrella `engines` profile starts every optional engine)
export DEMO_MYSQL="Server=127.0.0.1;Port=53306;User ID=demo;Password=demo;Database=demo"
dotnet run --project Api -- seed mysql                   # MySQL: 20000 rows in table orders.
dotnet run --project Api                                 # /api/health now lists mysql
docker compose --profile mysql stop mysql                # when done: stops it, keeps the volume
```

- **The connection string.** `DEMO_MYSQL` has no default in code. Change the port with `DEMO_MYSQL_PORT` on `docker compose` and in the string.
- **Seed.** `seed mysql` drops and recreates table `orders` in database `demo` (compose creates the database) and loads the same rows with batched multi-row `INSERT` statements in one transaction
  (`MySqlBulkCopy` would need `local_infile`, which MySQL 8.4 switches off). MySQL commits DDL on its own, so the indexes are built after the load. It is not a default target: plain `seed` still fills PostgreSQL, MongoDB and the SQLite file.
- **Types.** `BIGINT`, `VARCHAR(1000)` (utf8mb4), `DECIMAL(12,2)`, `DATETIME(3)`, `DATE`, `BOOLEAN`. Case sensitivity is a column setting on this engine, so the compiler writes no collation: the table default is the stock
  `utf8mb4_0900_ai_ci` (case- and accent-insensitive), and the groupable columns (country, category, status) are `utf8mb4_bin` so group buckets and drill-down are exact (GRP-2).
  The two text indexes use a 191-character prefix, because four bytes per character make a full `VARCHAR(1000)` index longer than InnoDB's 3,072-byte limit.
- **Stopped container.** The API answers 503 `backend-unavailable` at once (a refused connection, a stopped container, an unknown host and a connect timeout are all `MySqlException` number 1042). A refused login (1045) or a missing
  database (1044, 1049) is a setting error and stays a 500; `seed` reports those as a failure for that provider. Without a `Connection Timeout` in the string the service sets 5 seconds.

**Differences from PostgreSQL, measured on MySQL 8.4.11 and handled in the open**

1. **`AVG` over a whole-number column has four decimals.** MySQL adds `div_precision_increment` (4) decimals to the average of an integer type: the grouped `quantity` average is 25.3493 where PostgreSQL gives
   25.34926470588235. `smoke.sh` (rule for `mysql` only, key `quantity_avg`) and `Api.Tests` require the baseline value rounded half up to 4 decimals, exactly; the scenario "group by country" carries that average, and the line after
   the table names where the rule was used. It is the only key that needed a rule.
2. **`AVG` over the decimal column has six decimals** (12428.503752 against 12428.50375198728), inside the 1e-6 relative tolerance every provider's `_avg` already has.
3. **The stock collation ignores accents.** A contains filter for `SÖHNE`, `söhne` or `Söhne` finds `Müller, Söhne & Co` on MySQL and PostgreSQL; `sohne` and `SOHNE` find it **only on MySQL**
   (PostgreSQL's collation folds case, not accents; SQLite finds only `söhne` and `Söhne`). This is the engine's default family and what the library's fixtures declare, so the demo keeps it and documents it.
4. **Datetimes need a stated kind.** `DATETIME` has no zone, so the driver returns a `DateTime` of kind Unspecified and the JSON would lose the trailing `Z`. `MySqlGridService` marks them UTC (the seed stores UTC instants).
5. **CSV works through `DapperExportStream`** (the #2510 defect is SQL Server only). Its keyset query also selects the sort key twice (``SELECT `id`, ..., `id` ... ORDER BY `id` ``), which fails on SQL Server (see above), but MySQL accepts it.
   Rows and bytes equal the other providers, over the whole table (20,000 rows, several keyset chunks).

Set-values filters on dates and datetimes bind the values as strings, like the adapter, and MySQL converts them (tested with a date and a datetime filter).

**Package.** The demo adds `MySqlConnector` 2.6.2 (the library's version; MIT; central version in `Directory.Packages.props`). The restore shows no advisory. It is named in the approved stack for the integration tests;
its use under `examples/demo/Api` is wider than that wording, and the Engineering Lead's approval is still pending at the time of writing.

## Optional providers and seeding a subset

PostgreSQL, MongoDB, EF Core and SQLite are always on (SQLite is a file, so it needs no setting). Further engines are optional: an engine is switched on by its connection setting
(an environment variable), and without it the API does not register that provider, so `/api/health` does not list it and the
page does not offer it. This lets you run the demo with any subset of its databases. Two optional engines ship today, SQL Server (`DEMO_SQLSERVER`, profile `sqlserver`) and MySQL (`DEMO_MYSQL`, profile `mysql`); the
provider sections of this README name each engine's variable and compose profile. Compose profiles (`docker compose --profile <name> up -d`)
start the heavy engines only on request; the umbrella profile name is `engines`.

`seed` takes an optional list of providers:

```bash
dotnet run --project Api -- seed                  # the default: postgres, mongo and sqlite
dotnet run --project Api -- seed mongo            # only MongoDB
dotnet run --project Api -- seed sqlite           # only the SQLite file (no Docker needed)
dotnet run --project Api -- seed sqlserver        # only SQL Server (needs the profile and DEMO_SQLSERVER)
dotnet run --project Api -- seed mysql            # only MySQL (needs the profile and DEMO_MYSQL)
dotnet run --project Api -- seed nosuch           # exit 2, lists the known providers
```

Each provider is seeded on its own: a database that is down prints its error and the others still run. The exit code is 0 when
everything requested was seeded, 1 when a count is wrong, and 2 when a provider failed or the name is unknown.

`bash smoke.sh` compares every provider the API lists with the postgres baseline. To compare a chosen list, set
`DEMO_SMOKE_PROVIDERS=postgres,ef` (postgres is always required); a name the API does not list stops it with exit 2 and a clear message.

## Stop and reset

```bash
docker compose stop          # stop the databases, keep the data
docker compose --profile sqlserver --profile mysql stop   # stop the optional engines too (a plain `stop` leaves a profile service running)
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
| A red banner with `backend-unavailable` (HTTP 503) | The database for that provider is down or unreachable. Check `docker compose ps`, then reload. For `sqlite` the file is missing or locked: run `dotnet run --project Api -- seed sqlite`. |
| `seed sqlserver` says `DEMO_SQLSERVER is not set`, or a red banner for `sqlserver` | Start the profile (`docker compose --profile sqlserver up -d --wait`), set `DEMO_SQLSERVER` (see [SQL Server](#sql-server-the-sqlserver-provider)), run `seed sqlserver`, restart the API. |
| `sqlserver` answers 500 with a pre-login handshake error | The connection string lacks `TrustServerCertificate=true`. |
| The SQL Server container exits at start | It needs about 2 GB of RAM; check `docker logs demo-sqlserver-1`. |
| `seed mysql` says `DEMO_MYSQL is not set`, or a red banner for `mysql` | Start the profile (`docker compose --profile mysql up -d --wait`), set `DEMO_MYSQL` (see [MySQL](#mysql-the-mysql-provider)), run `seed mysql`, restart the API. |
| `seed mysql` says `Access denied` | `DEMO_MYSQL` has another user or password than the compose file (`demo` / `demo`), or a volume from an earlier run holds other credentials: remove only that container and its volume (`docker compose --profile mysql rm -sf mysql`, then `docker volume rm` of the `demo-mysql-data` volume from `docker volume ls`), never `down -v`, which also removes the PostgreSQL and MongoDB data. |
| A contains filter finds a name without the accent on `mysql` only | Expected: MySQL's default collation ignores accents (see [MySQL](#mysql-the-mysql-provider)). |
| `API: unreachable` in the header | The API is not running on port 5080. Start it (step 3). |
| A watermark and console warnings | Normal: AG Grid Enterprise evaluation mode (see above). |
| `smoke.sh` exits 2 | Read its message: it names the API address or the provider that failed. |

## Where the code is

| Path | What |
|---|---|
| `Api/` | ASP.NET Core minimal API: routes (`rows`, `values`, `export.csv`, `query`, `health`), the library calls (`Api/Grid/`: `PostgresGridService` for Dapper, `EfGridService` and `OrdersDbContext` for Entity Framework Core, `MongoGridService`, `SqlServerGridService`, `MySqlGridService`), the seed (`Api/Seed/`) |
| `Api.Tests/` | xUnit tests that start the real API against the databases and a temporary SQLite file (the ones that need PostgreSQL or MongoDB skip when they are down; the SQL Server and MySQL comparisons run when `DEMO_SQLSERVER` or `DEMO_MYSQL` is set and the engine answers, and skip with one reason otherwise) |
| `web/` | Vite + React + TypeScript page (see [web/README.md](web/README.md) for the AG Grid version and the page's requests) |
| `orders.schema.json` | The GridSchema: the columns the grid may touch, with their SQL and MongoDB mappings |
| `smoke.sh`, `smoke/` | The cross-provider check and its request files |
| `docker-compose.yml` | PostgreSQL 16 and MongoDB 7; SQL Server 2022 and MySQL 8.4 behind their own profiles and `engines` |

To run the API tests: `dotnet test Api.Tests` (needs the seeded databases from step 1 and 2).
