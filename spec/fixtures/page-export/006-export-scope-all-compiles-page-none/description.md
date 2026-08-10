# Export scope "all" compiles to page: none

**Proves:** EXP-1's `exportScope: "all"` leg + EXP-2 (same IR, `page: none` mode).

## Scenario

The grid currently shows a scrolled-in page (`startRow: 200`, `endRow: 250` — as if the user
had scrolled to row 200 before triggering "Export all"), but `exportScope: "all"`. Per EXP-1,
`"all"` **ignores** `startRow`/`endRow` and streams the full filtered+sorted result.

## Key expectation

Despite `startRow: 200, endRow: 250` being present on the wire request, the compiled IR's
`page` is `{ mode: "none" }` (`ir.schema.json` `$defs/nonePage`), not `{ mode: "range",
startRow: 200, endRow: 250 }`. This is the adapter-level rule EXP-1 states explicitly:
`exportScope` — not the grid's current scroll position — decides pagination for an export
request. Contrast with fixture 007's `exportScope: "page"`, where the same `startRow`/`endRow`
values *are* honored.

## SEM-3 null-ordering (category-wide fix)

`sortModel` requests `id ASC` — which already *is* the schema's `stableKey`, so no additional
SEM-6 tiebreaker is appended (the sort already ends in a unique key). SEM-3's own null-ordering
marker still applies unconditionally to this sort key regardless: Postgres `NULLS LAST`, Mongo's
null-bucket `$addFields`/`$sort`/`$unset` emulation, Search's `sort[].missing: "_last"` — "the
field is probably never null in practice" (a surrogate/primary key usually isn't) is never a
license to omit the marker; there is no schema state (no declarable `NOT NULL` in
`grid-schema.schema.json`) that could justify skipping it.

## Mongo physical-path note + `$project` security fix

`id`'s declared `physical.mongo.path` in this schema is `_id` (the native Mongo primary key), not
a bare top-level `id` field — the compiled Mongo artifact's `$sort`/null-bucket stages reference
`_id` accordingly, never `"id"`. The pipeline also gains a trailing `$project` stage restricting
the output to exactly the schema's exportable fields (mapping `id`/`customerName` back from their
physical paths, `_id`/`customer.name`, to their logical output keys) — without it the row stream
would leak every field in the underlying document, not just the `GridSchema` allowlist.
