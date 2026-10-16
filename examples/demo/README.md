# Craticula demo

One AG Grid page that reads the same 20,000 synthetic orders from PostgreSQL or MongoDB. A small C# API turns each grid
request into a native query with the Craticula library. To run it (three terminals, from this folder):

```bash
docker compose up -d && dotnet run --project Api -- seed   # terminal 1: databases, then fill them
dotnet run --project Api                                   # terminal 2: the API on http://127.0.0.1:5080
cd web && npm install && npm run dev                       # terminal 3: the page on http://localhost:5173
```

It works when http://localhost:5173 shows a grid with rows, and `bash smoke.sh` (run in a fourth terminal, while the API
is up) ends with `all 19 scenarios are equal on both providers`.

This is a showcase, not a published package. It references the library by project reference (`../../csharp/src`) until
the first release is on nuget.org. It is not part of `csharp/Craticula.sln`.

## A second page: AG Grid Community

http://localhost:5173/community.html is the same data on the **free AG Grid Community** edition (Infinite Row Model): no
license key, no watermark, no grouping, aggregation or set filter (those are Enterprise features). It sends the flat request
Community sends (`startRow`, `endRow`, `sortModel`, `filterModel`) and the API compiles it like any other. The native "in range"
option is left out of the number and date filters, because the library rejects it (it carries no inclusivity); the main page does
the same. **Copy request** puts the exact request body on the clipboard: that is how a real payload becomes a fixture (see
`spec/fixtures/community-infinite/README.md`).

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

5. Optional check without a browser: `bash smoke.sh` posts 15 fixed requests (`smoke/*.json`) to both providers and prints
   one row per scenario. Exit code 0 means every answer is equal; 1 means a difference or a missing `400`; 2 means the
   environment is wrong (API down, a database down). `bash smoke.sh --api URL` points it at another API.

## Click-through checklist

Do these in the browser. Each should work on both providers.

1. **Switch provider.** Use the PostgreSQL / MongoDB switch in the header. Apply a filter and a sort, then switch: the same
   rows appear.
2. **Sort.** Click a column header (Amount, Ordered at). Shift-click a second column to sort by both.
3. **Filters, one per type.** Text (Customer contains `GmbH`), number (Quantity greater than 8), date (Shipped on, Ordered
   at), boolean (Priority: tick `true`).
4. **Set filter.** Open the Country filter. The values come from the database. Untick some and see the grid narrow.
5. **Group.** Drag Country, then Category, into the row group bar above the grid. Drag Amount and Quantity into the values
   area and choose sum, avg and count. Open a group to see its children.
6. **Scroll.** Scroll down in a long list. Pages load as you go; the row count stays right.
7. **CSV export.** With a filter and sort active, click Export CSV. The file holds the filtered, sorted rows. A cell that
   starts with `=` is saved with a leading `'` so a spreadsheet does not run it as a formula.
8. **Query panel.** Click Query panel. It shows the SQL (PostgreSQL) or the aggregation pipeline (MongoDB) with its
   parameters for the current view.

## What to expect

- **A watermark.** The server-side row model is an AG Grid Enterprise feature. There is no license key in this repository,
  so the grid runs in evaluation mode: a watermark and console warnings are normal. To use your own key, create
  `web/.env.local` (git ignores it) with `VITE_AG_GRID_LICENSE=<your key>`.
- **No tree data, master/detail or pivot.** The library does not support them, so the page does not offer them.
- **No "in range" option for numbers and dates.** The library takes ranges through its `customFilters` extension and not
  through AG Grid's own filter model, so the page offers the single-value operators only.

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
| `Api/` | ASP.NET Core minimal API: routes (`rows`, `values`, `export.csv`, `query`, `health`), the library calls, the seed (`Api/Seed/`) |
| `Api.Tests/` | xUnit tests that start the real API against both databases (they skip when the databases are down) |
| `web/` | Vite + React + TypeScript page (see [web/README.md](web/README.md) for the AG Grid version and the page's requests) |
| `orders.schema.json` | The GridSchema: the columns the grid may touch, with their SQL and MongoDB mappings |
| `smoke.sh`, `smoke/` | The cross-provider check and its request files |
| `docker-compose.yml` | PostgreSQL 16 and MongoDB 7 |

To run the API tests: `dotnet test Api.Tests` (needs the seeded databases from step 1 and 2).
