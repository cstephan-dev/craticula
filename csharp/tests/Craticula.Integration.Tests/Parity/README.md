# Cross-backend parity property (T-3, #2269)

One seeded dataset, N generated AG Grid requests, seven engines: every engine must return the **same sequence of `id`
(stableKey) values** for every request, window included, on all 21 engine pairs. The property, its limits and the
register of excluded constructs are stated once, in `spec/semantics.md` PAR-1..PAR-4. This folder is the code.

| Engine | Image |
|---|---|
| PostgreSQL, SQL Server, MySQL | the execution-equality tier's pinned images (`Sql/SqlEngine.cs`) |
| SQLite | in-process, no container |
| MongoDB, OpenSearch, Elasticsearch | the tier's pinned images (`Document/`) |

Oracle is not part of the property.

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
| `CRATICULA_PARITY_ROWS` | `150` | rows in the seeded dataset (ids are unique, drawn below `rows * 20`) |

A malformed value fails the run; it is never replaced by a default.

## How a run goes

1. All seven engines start concurrently and are seeded **once** with the parity schema (`ParitySchema`) and dataset
   (`ParityDataset`): one table, collection or index per engine. Nothing is recreated per case. Start + seed is timed
   apart from the case loop (a default run on the maker's machine: about 20 s start + seed, about 2 s of cases).
2. Each engine runs all cases in order (the engines concurrently): the case is parsed and validated by the real adapter
   and filter pipeline with SEC-3's default limits, compiled by the engine's own compiler, executed, and its `id` column read.
3. The id sequences are compared for every pair. A request an engine or its compiler refuses is a failure (PAR-1).
4. **Every** failing case is collected; the report prints at most 20 in full, then the total and the remaining indexes.

## Reading a failure

Each printed case carries: the seed and the case index, the disagreeing pairs, every engine's id sequence, the refusals
(with the exception text), the request JSON and the dataset (by seed and row count, and as `dataset.json` written to
`$TMPDIR/craticula-parity-dataset-<seed>-<rows>.json`). A case is a pure function of `(seed, index)`, so replay it with
`ParityRequestGenerator.Generate(seed, index)` over `ParityDataset.Generate(seed, rows)`; to keep it as a fixture, write
the request to `agGridRequest.json`, the dataset to `dataset.json`, `ParitySchema.Json` to `schema.json` and the expected
ids to `expectedRows.json` (PAR-4).

A mutation check that the harness can fail: `CRATICULA_XEQ_MUTATION=drop-collate` (the tier's fault injection) makes the
SQL engines compare strings case-sensitively, and the run fails with the cases that filter `label`.
