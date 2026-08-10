# Range page — mid-stream offset, size 100

**Proves:** AG-3 range paging at a non-zero offset, descending sort direction's SEM-3-pinned
`nullsPosition: "first"`.

## Scenario

A scrolled-down grid: `startRow: 500`, `endRow: 600` (page size 100), sorted descending by
`totalAmount`. No filter.

## Key expectation

`page: { mode: "range", startRow: 500, endRow: 600 }`. Postgres `OFFSET 500 LIMIT 100`;
Mongo `$skip: 500, $limit: 100`; Search `from: 500, size: 100`. The descending sort carries
`nullsPosition: "first"` (SEM-3's pinned adapter default for `desc`), rendered on every backend:
Postgres' native `NULLS FIRST`, Mongo's null-bucket emulation (bucket sort sign `-1`, since
`nullsPosition` is `"first"`), and Search's `sort[].missing: "_first"`.

**SEM-6 tiebreaker.** The requested sort (`totalAmount DESC`) doesn't already end in the schema's
`stableKey` (`id`), so the compiler appends `{ field: "id", direction: "asc", nullsPosition:
"last" }` as the final key on every backend — always `asc`/`"last"`, independent of the primary
sort's own direction, since the tiebreaker exists purely to break ties and has no direction the
user chose. Mongo's tiebreaker targets `_id` (this schema's declared `physical.mongo.path` for
`id`), not a bare `id` field.
