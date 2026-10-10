# Execution equality on the document engines (T-1b, #2207)

Runs the execution-equality tier (`spec/semantics.md` XEQ-1..XEQ-4) on MongoDB, OpenSearch and Elasticsearch:
seed the engine from a fixture's `dataset.json`, compile the fixture's request with `Craticula.Mongo` or
`Craticula.Search`, run it, map the engine's output onto the logical column list and compare with `RowComparer` against
`expectedRows.json` (or the fixture's XEQ-4 entry for that backend and engine).

Run it (needs Docker; the first run pulls the images):

```bash
cd csharp
dotnet test tests/Craticula.Integration.Tests --filter "FullyQualifiedName~Containers.Document"
```

The offline filter in `CLAUDE.md` excludes it (`Integration.Tests.Containers`). Without a Docker daemon every test is
reported skipped with the reason, never failed.

## Engines (one container per engine per run, a fresh collection or index per fixture)

| Engine | Image (pinned) | Setup |
|---|---|---|
| MongoDB | `mongo:7.0.14` (7.0 is the repo's documented version) | Testcontainers `MongoDbBuilder`; one database per fixture |
| OpenSearch | `opensearchproject/opensearch:2.19.1` | single node, `DISABLE_SECURITY_PLUGIN=true`, plain HTTP |
| Elasticsearch | `docker.elastic.co/elasticsearch/elasticsearch:8.17.4` | single node, `xpack.security.enabled=false`, plain HTTP |

Search goes over `HttpClient` (no client package): `PUT <index>`, `_bulk?refresh=true`, `POST <index>/_search` with
`SearchQuery.ToArtifact()`. `search.artifact.json` is one artifact that runs on both engines (OQ-7), so the same fixture
list is run twice and an XEQ-4 entry with `engine` applies to one of them.

## How a fixture is seeded and read

- **Mongo**: logical colId goes to `physical.mongo.path` (`id` to `_id`, `customerName` to the nested `customer.name`).
  `int64` is a BSON long, `double` a BSON double, `decimal` a `Decimal128`, `date` the plain ISO string (SEM-4),
  `datetime` a BSON Date, null an explicit BSON null. The pipeline and its query-level `collation` run through
  `aggregate`. Ordinary and export fetches project the logical columns explicitly (as `MongoGridQuery` does), so `_id`
  never leaks; grouped levels and BM-3 values read `_id` as the group key / value.
- **Search mapping** (SC-3 and the OSE field table): a `keyword` base field (`text` only for `searchMode: analyzed`);
  `keywordSubField` as a plain `keyword`; `normalizedSubField` as a `keyword` with a **lowercase** normalizer (SEM-1's
  schema names an ICU-folding one; no fixture depends on diacritic folding today, so the plugin is not needed);
  `int64` `long`, `double`/`decimal` `double` (the decimal choice `SearchJson.Encode` documents; `_source` keeps the
  exact token, sums come back as doubles), `date` `strict_date`, `datetime` `strict_date_optional_time`, `boolean`.
- **Search results**: hits read from `_source[physical.search.field]`; grouped levels from the composite buckets
  (key, then each metric by its output name; `min`/`max` on dates via `value_as_string`); BM-3 from the terms buckets.
- **Aggregate names**: the logical column is the `valueCols` id (`Aggregation.ColumnId`, XEQ-3); the engine's own key
  is `Aggregation.OutputName` (`amount_sum`).

### Documented limits the harness keeps, never skips

- **Composite paging (OSE-11(d)).** A grouped level compiles to `composite.size = endRow - startRow` with no `after`
  key and the compiler refuses a non-zero `startRow`, so the first response is the whole window. The harness asserts
  the bucket count fits the window; a grouped fixture that starts past row 0 fails as a compiler refusal rather than
  being skipped. Carrying `after` is the adapter's job (its tests live in `Craticula.OpenSearch.Client.Tests`).
- **`page: none` (EXP-2) on Search** compiles to a hit query with no `size`, which the engine answers with 10 hits.
  The harness adds `size: 10000` and `track_total_hits: true` (harness-side, the compiled query is untouched) and
  asserts the total fits, so a truncated export cannot pass as a short one.

## Result of the first run (2026-10-10)

481 tests: MongoDB 161 (159 pass, 2 fail), OpenSearch 160 (154 pass, 6 fail), Elasticsearch 160 (154 pass, 6 fail).
Every failure is a real engine result that differs from `expectedRows.json`; nothing was edited or loosened to pass.
The classification (input to #2208) is in the task report; the failing fixtures are:

| Fixture | Engines | Difference |
|---|---|---|
| `filter-tree/012-customfilters-depth-5` | mongo, opensearch, elasticsearch | `Not(And(Or(Not(eq))))`: the null row (id 4) is missing; the SQL artifact's null-inclusive outer `Not` (SEM-2) has no Mongo/Search counterpart |
| `dialect-parity/023-sem1-mixed-case-sensitivity-one-request` | mongo | the sensitive `employeeCode` comparison, spelled as `$expr`, is folded by the query-level collation (MDB-9's premise is false on 7.0) |
| `operators/012-gt-datetime`, `024-lte-datetime`, `041-inrange-datetime-exclusive-inclusive` | opensearch, elasticsearch | a whole-second bound (`...:00Z`) is rounded UP to `.999` by `gt`/`lte`, so a row 1 ms past the bound is wrongly in or out |
| `ag-grid-majors/004-v34-grouped-request-with-date-conditions`, `group-aggregation/021-group-column-sort-via-auto-column-colid` | opensearch, elasticsearch | AGG-5: the composite null bucket is first on `asc` and last on `desc`, but neither fixture declares the XEQ-4 entry |

## Decision: the live suites (`Craticula.MongoDB.Driver.Tests`, `Craticula.OpenSearch.Client.Tests`)

**Recommendation: keep them as adapter-level tests; do not fold them into this project.** The Tech Lead decides; this
change touches neither project.

- This tier tests the **compiled query's results**: it runs the compiler's output itself, over a harness the tier
  owns, so a divergence is evidence about the compiler or the spec. Its input is the shared fixture corpus.
- The live suites test the **adapters' own work**: BSON / `_source` to CLR materialization and the typed error paths,
  `FetchPageAsync` / `CountAsync` row counts and `lastRow`, the export streams, the PERF-2 PIT + `search_after` deep
  paging with a lowered window, the composite `after` splice for grouped blocks past the first, and the cursor cache and
  its cache key. None of that is reachable from a compiled artifact, and a fold would drag adapter internals
  (`InternalsVisibleTo`, the adapter packages) into a project whose point is that it depends on the compilers only (AR-3).
- Overlap is small: both run a handful of fixtures against a real engine. If the duplication matters later, the
  cheaper change is to let those two suites read their engine from a Testcontainers-started instance of the images
  above instead of a hand-started one (`CRATICULA_MONGO_TEST_URI` / `CRATICULA_OPENSEARCH_TEST_URI`), which keeps them
  separate and removes the manual setup from `CLAUDE.md`.
