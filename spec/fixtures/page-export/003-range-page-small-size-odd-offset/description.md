# Range page — small page size, odd offset

**Proves:** AG-3 range paging is not tied to any "round" page size or offset — `(endRow -
startRow)` is the page size, computed generically, not assumed to be a UI-grid-default like 100.

## Scenario

`startRow: 42`, `endRow: 45` (page size 3), sorted ascending by `id` (the schema's own
`stableKey`, used here simply as a deterministic sort key — not itself a SEM-6 tiebreaker
demonstration, which lives in the `sort-paging` category, since the requested sort already ends
in `id` and the compiler has nothing to append).

## Key expectation

`page: { mode: "range", startRow: 42, endRow: 45 }`. `(endRow - startRow) = 3` is the exact
requested page size, regardless of how "unaligned" the offset is with any natural page boundary.

**Null-ordering note (SEM-3), unconditional even for a single raw-field sort key.** `id` carries
its own explicit `nullsPosition: "last"` (the adapter's pinned default for `asc`) exactly like any
other sort key would, regardless of the fact that it also happens to be the `stableKey` — SEM-3
requires the marker on every `sortItem`, on every backend: Postgres' `NULLS LAST`, Mongo's
null-bucket `$addFields`/`$sort`/`$unset` emulation (targeting `_id`, this schema's declared
`physical.mongo.path` for `id` — never a bare `id` field, which doesn't exist in the collection's
documents), and Search's `sort[].missing: "_last"`.
