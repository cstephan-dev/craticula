# Cross-backend parity property (T-3, #2269)

One seeded dataset, N generated AG Grid requests, eight engines: every engine must return the **same sequence of `id`
(stableKey) values** for every request, window included, on all 28 engine pairs. The property, its limits and the
register of excluded constructs are stated once, in `spec/semantics.md` PAR-1..PAR-4. This folder is the code.

| Engine | Image |
|---|---|
| PostgreSQL, SQL Server, MySQL | the execution-equality tier's pinned images (`Sql/SqlEngine.cs`) |
| SQLite | in-process, no container |
| MongoDB, OpenSearch, Elasticsearch | the tier's pinned images (`Document/`) |

Oracle joined the property with #2880 (OQ-6), the eighth engine (`gvenzl/oracle-free`, Docker like the others).

## Run it (needs Docker; the first run pulls the images)

```bash
cd csharp
dotnet test tests/Craticula.Integration.Tests --filter "FullyQualifiedName~Containers.Parity" \
  --logger "console;verbosity=detailed"
```

The detailed logger prints the one-line summary on a pass too: seed, cases, rows, container start + seed time, case-loop
time, failing cases and how many cases returned rows. Without a Docker daemon the test is reported skipped with the
reason (`CRATICULA_INTEGRATION_DOCKER=off` forces it). The offline filter in `CLAUDE.md` excludes it
(`Integration.Tests.Containers`); the pure comparison and report code is tested offline in `ParityReportTests`.

| Variable | Default | Meaning |
|---|---|---|
| `CRATICULA_PARITY_SEED` | `20261010` | seed of the dataset and of every request |
| `CRATICULA_PARITY_CASES` | `200` | number of generated requests |
| `CRATICULA_PARITY_ROWS` | `300` | rows in the seeded dataset (ids are unique, drawn below `rows * 20`) |

A malformed value fails the run; it is never replaced by a default.

## How a run goes

1. All eight engines start concurrently and are seeded **once** with the parity schema (`ParitySchema`) and dataset
   (`ParityDataset`): one table, collection or index per engine. Nothing is recreated per case. Start + seed is timed
   apart from the case loop (a default run on the maker's machine: about 20 s start + seed, about 2.5 s of cases; 1000 cases take about 7 s).
2. Each engine runs all cases in order (the engines concurrently): the case is parsed and validated by the real adapter
   and filter pipeline with SEC-3's default limits, compiled by the engine's own compiler, executed, and its `id` column read.
3. The id sequences are compared for every pair. A request an engine or its compiler refuses is a failure (PAR-1).
4. **Every** failing case is collected; the report prints at most 20 in full, then the total and the remaining indexes.

## What the generator offers

Every operator on every field type it applies to, as a catalog of (field, operator) entries visited in turn, so a default run
reaches all of them. `in` and `notIn` are offered on all seven field types (decimal, date and datetime since Bug #2304 was fixed by SQL-4's array cast); the
ordering operators and `inRange` on the string field are excluded (PAR-3: OSE-6, SEM-1). A filter tree counts every level,
a NOT included, so no generated filter is deeper than 8 (SEC-3); `ParitySetOperatorTests` sweeps 100,000 cases for it and
compiles the set-operator requests on all compilers offline.

## Reading a failure

Each printed case carries: the seed and the case index, the disagreeing pairs, every engine's id sequence, the refusals
(with the exception text), the request JSON and the dataset (by seed and row count, and as `dataset.json` written to
`$TMPDIR/craticula-parity-dataset-<seed>-<rows>.json`). A case is a pure function of `(seed, index)`, so replay it with
`ParityRequestGenerator.Generate(seed, index)` over `ParityDataset.Generate(seed, rows)`; to keep it as a fixture, write
the request to `agGridRequest.json`, the dataset to `dataset.json`, `ParitySchema.Json` to `schema.json` and the expected
ids to `expectedRows.json` (PAR-4).

Replay one case: set `CRATICULA_PARITY_SEED` and `CRATICULA_PARITY_ROWS` to the printed values and read the request with
`ParityRequestGenerator.Generate(seed, index)`; the run for a wider look is `CRATICULA_PARITY_CASES=1000` with any seed.
Defaults: seed `20261010`, 200 cases, 300 rows. The report's `N of M cases returned rows` line says how much was compared.

## How strong the property is

A run that compared only empty windows would pass for nothing, so `ParityStrength` measures what the generated requests
do on in-process SQLite (no Docker), and `ParityStrengthTests` fails when the numbers fall. Measured for 1000 cases,
300 rows, five seeds (1, 7, 20261010, 424242, -9), after tuning and again on 2026-10-11 with set operators on all seven types (before tuning, at 150 rows: 62.9 % of cases returned rows on the default seed). The Default seed column is the 2026-10-11 measurement; the 91.4 % of 2026-10-10 fell to 88.6 % when `in` / `notIn` on date and datetime returned in the catalog (their operands rarely match):

| Measure | Default seed | Range over five seeds |
|---|---|---|
| Cases that return rows (guard: at least 85 %) | 88.6 % | 87.8 to 91.0 % |
| Mean ids per non-empty case (guard: at least 10) | 22.6 | 19.5 to 22.6 |
| Multi-key sorts with a first-key tie in the result (guard: at least 70 %) | 90.5 % | 87.9 to 92.6 % |
| Results whose first sort key holds NULLs next to values | 593 of 1000 | 593 to 626 |
| Windows that start inside the result (guard: at least 30 % of cases) | 44.5 % | 43.4 to 44.7 % |
| Windows that stop short of the result's end | 757 of 1000 | 757 to 780 |

What was tuned, all inside PAR-3 (no register row changed): window starts are mostly small (one in 50 starts at 200 on
purpose); equality operands rarely sit next to a pooled value; an AND of conditions on one column carries no blank test and
has two conditions; trees are mostly ORs with fewer leaves; the pools gained earlier dates and instants so `lt` and `lte`
have rows below them; the dataset has 300 rows.

## Mutations the property catches

`CRATICULA_XEQ_MUTATION` damages the SQL the SQL engines are sent (`Sql/SqlEngine.cs`, `SqlMutation`, test code only), and
the run fails. Default seed, 200 cases, re-measured on all eight engines on 2026-10-22 (#2880; the first measurement, on seven engines on 2026-10-10, gave 25, 136 and 127):

| Value | Damage | Failing cases |
|---|---|---|
| `drop-collate` | every `COLLATE` removed (MySQL: binary string columns) | 17 |
| `drop-nulls` | every `NULLS FIRST` / `NULLS LAST` removed (PostgreSQL, Oracle, SQLite) | 124 |
| `drop-tiebreaker` | the trailing stableKey sort key removed | 114 |

Never set one outside a check like this.
