# SQL execution-equality harnesses (T-1b, #2206)

Runs every fixture that carries `dataset.json` + `expectedRows.json` (`spec/semantics.md` XEQ-1..XEQ-4) on four real
engines, each through its own ADO driver, and compares the rows with `Oracle/RowComparer` (XEQ-3). A fixture runs on an
engine only if it has `expected/<backend>.artifact.json` (`postgres`, `mssql`, `mysql`, `sqlite`; `oracle` is out of scope, OQ-6).

| Engine | How it runs | Pinned image |
|---|---|---|
| PostgreSQL 16 | Testcontainers, Npgsql | `postgres:16.15` |
| SQL Server 2022 | Testcontainers, Microsoft.Data.SqlClient | `mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04` |
| MySQL 8.4 | Testcontainers, MySqlConnector | `mysql:8.4.11` |
| SQLite | in-process shared in-memory database, Microsoft.Data.Sqlite | none |

One container per engine for the whole run (an xUnit collection fixture, started on the first fixture, so a run without
Docker skips everything without pulling an image); a fresh table per fixture (`DROP TABLE IF EXISTS`, `CREATE TABLE`,
seed). The namespace is `Craticula.Integration.Tests.Containers.Sql`, so the offline filter leaves all of it out.

Run: `dotnet test tests/Craticula.Integration.Tests --filter "FullyQualifiedName~Containers.Sql"`.

`DapperPostgresExecutionTests` (#2315) runs the set-membership fixtures `operators/035..038` and `048..053` through the
`Craticula.Dapper` adapter (this project references it as a test host) on the same Postgres container: the adapter binds the
array as `text[]`, so it proves the dialect's `::numeric[]` / `::date[]` / `::timestamptz[]` cast (SQL-4) for a host port. Since Bug #2320 it also runs `operators/046`, `040`, `041` and `date-presets/001` (scalar comparisons, `inRange`, a date preset: the placeholder cast of SQL-8) and an export walk (`DapperExportStream`, chunk sizes 1 and 3) over a decimal, a date and a datetime sort key, so Dapper on Postgres is proven for set membership, scalar comparisons and the export keyset on all three types.

## What the harness does

1. `SqlFixtureCompiler` runs the fixture's own `agGridRequest.json` through `AgGridRequestParser` -> `GridFilterPipeline`
   -> `SqlQueryBuilder.Build` / `BuildSetValues` with the engine's dialect (a pinned clock when `requestContext.now` is set).
2. `SqlEngine` creates the table named by `schema.source` from the schema's physical SQL column names, seeds it from the
   dataset (logical colId -> physical column), and runs the compiled text with the compiled parameters.
3. `SqlExecutionRunner` maps the physical result onto the logical column list (XEQ-3): a column named after a schema field
   is that field, one named after a physical column (`SELECT *`) is the field that owns it, and on a grouped level an
   aggregate's compiled name (the alias when the column id differs from the field, else `<field>_<func>`, AGG-2/GRP-4)
   maps to the `valueCols` entry's `id`, which is the name `expectedRows.json` uses. Then it calls `RowComparer`.
4. An XEQ-4 `error` entry is asserted: the engine must refuse the query (no fixture has one for these four engines).

A difference fails with the fixture, the backend, the mismatch list, the SQL sent and the parameters. Nothing here edits
an expectation or loosens the comparer.

## DDL, and where each choice comes from

| Type | PostgreSQL | SQL Server | MySQL | SQLite |
|---|---|---|---|---|
| int64 | `BIGINT` | `BIGINT` | `BIGINT` | `INTEGER` |
| double | `DOUBLE PRECISION` | `FLOAT` | `DOUBLE` | `REAL` |
| decimal | `NUMERIC(18,4)` | `DECIMAL(18,4)` | `DECIMAL(18,4)` | `NUMERIC` (affinity converts the bound text decimal) |
| boolean | `BOOLEAN` | `BIT` | `BOOLEAN` | `INTEGER` |
| string | `TEXT` | `NVARCHAR(450)` | `VARCHAR(1000)` | `TEXT` |
| date | `DATE` | `DATE` | `DATE` | `TEXT` `yyyy-MM-dd` |
| datetime | `TIMESTAMPTZ` | `DATETIME2(3)` | `DATETIME(3)` | `TEXT` `yyyy-MM-ddTHH:mm:ss.fffZ` (exactly three fractional digits; SEM-4) |

Every column is nullable. Collation, per SEM-1 / SQL-6 / GRP-2:

- **PostgreSQL**: host DDL `CREATE COLLATION craticula_ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false)`
  (SEM-1's worked example). Columns keep the database's default (deterministic) collation, which is what GRP-2 and the
  stableKey tiebreaker rely on. The compiler spells `COLLATE craticula_ci` for the insensitive direction.
- **SQL Server**: the compiler spells both directions (`Latin1_General_CI_AS` / `_CS_AS`, SQL-6). Columns are the stock
  `SQL_Latin1_General_CP1_CI_AS`, except a **groupable** string column, which is `Latin1_General_100_BIN2`: GRP-2's
  accepted-divergence paragraph says a host that wants exact buckets on SQL Server must declare the groupable column with a
  binary collation, and the fixtures' expected buckets are exact.
- **MySQL**: SEM-1's insensitive mechanism is the column's own collation, so the table is
  `utf8mb4_0900_ai_ci` (the example SEM-1 names). SQL-6 and GRP-2 make two host obligations: a field declared
  `caseSensitivity: "sensitive"` and a **groupable** string column are declared `utf8mb4_bin`.
- **SQLite**: default `BINARY` sequence; the compiler spells `COLLATE NOCASE` (SQL-6).

## Test-only mutation (Definition of Done)

`CRATICULA_XEQ_MUTATION=drop-collate` removes every `COLLATE <name>` from the SQL before it is sent. MySQL spells none, so
its equivalent is declaring every string column `utf8mb4_bin`. With the switch on, tests fail on all four engines (63
failures against none in the normal run, 2026-10-10: PostgreSQL 32, SQLite 6, MySQL 14, SQL Server 11), which shows the tier
sees a silently ignored collation. Never set it outside that check.
