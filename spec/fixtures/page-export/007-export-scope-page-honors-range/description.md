# Export scope "page" honors startRow/endRow

**Proves:** EXP-1's `exportScope: "page"` leg — "export exactly what the user sees".

## Scenario

Identical filter/sort/startRow/endRow to fixture 006, except `exportScope: "page"` instead of
`"all"`.

## Key expectation

The compiled IR's `page` is `{ mode: "range", startRow: 200, endRow: 250 }` — the *same*
range-page compilation an ordinary (non-export) request with these `startRow`/`endRow` values
would produce (compare fixture 002's shape). `exportScope: "page"` changes nothing about
compilation beyond selecting the range-page mode that was going to be used anyway; the only
difference from a non-export request is that the writer (EXP-4, out of scope here) serializes the
resulting rows to CSV/TSV instead of the JSON `LoadSuccessParams` envelope.

## SEM-3 null-ordering (category-wide fix)

`sortModel` requests `id ASC` — already the schema's `stableKey`, so no additional SEM-6
tiebreaker is appended. SEM-3's own null-ordering marker still applies unconditionally to this
sort key regardless, on every backend and under both range paging and `page: none` alike:
Postgres `NULLS LAST`, Mongo's null-bucket `$addFields`/`$sort`/`$unset` emulation, Search's
`sort[].missing: "_last"`.

## Mongo physical-path note + `$project` security fix

`id`'s declared `physical.mongo.path` in this schema is `_id` (the native Mongo primary key), not
a bare top-level `id` field — the compiled Mongo artifact's `$sort`/null-bucket stages reference
`_id` accordingly, never `"id"`. The pipeline also gains a trailing `$project` stage (after the
range-page `$skip`/`$limit`) restricting the output to exactly the schema's exportable fields,
mapping `id`/`customerName` back from their physical paths to their logical output keys —
without it the row stream would leak every field in the underlying document.
