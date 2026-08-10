# Export wire contract — exportFormat: "csv"

**Proves:** EXP-1's request extension, `exportFormat: "csv"` leg.

## Scenario

The wire request carries the EXP-1 extension fields (`exportFormat`, `exportScope`,
`exportColumns`) **alongside** the normal AG Grid SSRM fields (`filterModel`, `sortModel`,
`startRow`/`endRow`) — this is a wire-contract *extension*, not a replacement wire shape. Here
`exportFormat: "csv"`, `exportScope: "all"` (the EXP-1 default), and no `exportColumns` (all
exportable fields, schema order — see fixture 009 for that specific behavior). A status filter
(`status = "shipped"`) and an ascending `orderDate` sort are present and, per EXP-1, "`
filterModel` and `sortModel` are always honored" regardless of export mode.

## Key expectation

`exportFormat` only selects which serializer a downstream CSV/TSV writer (EXP-4) uses to turn
the streamed rows into bytes — it has **no effect on IR compilation**. The compiled artifact
below is identical in shape to what a non-export request with the same filter/sort/scope would
produce; `exportFormat` itself never reaches the compiler. `exportScope: "all"` still compiles
to `page: { mode: "none" }` (EXP-2) since `startRow`/`endRow` are `null` here.

## SEM-6 tiebreaker + SEM-3 null-ordering (category-wide fix)

The requested sort doesn't already end in the schema's `stableKey` (`id`), so the compiler
appends `{ field: "id", direction: "asc", nullsPosition: "last" }` as the final sort key on every
backend, after the user's own chosen key — per SEM-6's unconditional rule, which applies
regardless of export scope. Every sort key (the user's own and the appended tiebreaker) carries
its own explicit null-ordering marker (SEM-3): Postgres `NULLS LAST`/`NULLS FIRST`, Mongo's
null-bucket `$addFields`/`$sort`/`$unset` emulation, Search's `sort[].missing`.

## Mongo `$project` security fix

The compiled Mongo pipeline (after the `$match`, the null-bucket sort, and its `$unset`) ends
with a `$project` stage restricting the output to exactly the schema's exportable fields, mapping
`id`/`customerName` back from their declared physical paths (`_id`, `customer.name`) to their
logical output keys — without it the row stream would leak every field in the underlying
document, not just the `GridSchema` allowlist.
