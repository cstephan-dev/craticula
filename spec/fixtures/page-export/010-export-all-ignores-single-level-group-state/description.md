# Export ignores active single-level group state — leaf rows only

**Proves:** EXP-1's carve-out: "Export of a *grouped* view emits **leaf rows** under the current
filter+sort; group-structured and pivot exports are out of scope v1."

## Scenario

The grid is currently grouped by `region` (`rowGroupCols: [{ id: "region", field: "region" }]`)
with a `sum`-aggregated `totalAmount` value column (`valueCols: [{ id: "totalAmount", field:
"totalAmount", aggFunc: "sum" }]`), and `groupKeys: []` (the grid is showing the collapsed
top-level group rows — nothing drilled into yet). This is "active group state": if this were an
ordinary (non-export) SSRM request, `groupKeys.length (0) < groupCols.length (1)` would compile
a **grouped** query (`ir.schema.json`'s `groupSpec` description: next group column +
aggregations). The request also sets `exportScope: "all"`.

## Key expectation

Despite `rowGroupCols`/`valueCols`/`groupKeys` all being populated exactly as an active
grouped grid view would send them, the compiled artifact below is a **flat, ungrouped, leaf-level**
query — identical in shape to fixture 006/009's non-grouped export artifacts. There is no
`GROUP BY`, no Mongo `$group` stage, no search `aggs`. The compiler discards the group/agg
wire fields entirely for export compilation; the IR it builds has no `group` and no
`aggregations` field at all (both are optional/absent per `ir.schema.json`), only `filter`
(none here), `sort` (none here) and `page: { mode: "none" }`.

## SEM-6 tiebreaker + SEM-3 null-ordering (category-wide fix)

`sortModel` is empty here, so the compiler appends the schema's `stableKey` (`id`) as the sole
sort key, per SEM-6's unconditional rule — regardless of export scope or the active (but
ignored, per this fixture's own point) group state. Every backend renders its own explicit
null-ordering marker (SEM-3): Postgres `NULLS LAST`, Mongo's null-bucket
`$addFields`/`$sort`/`$unset` emulation, Search's `sort[].missing: "_last"`.

## Mongo physical-path note + `$project` security fix

`id`'s declared `physical.mongo.path` is `_id`, and `customerName`'s is `customer.name` — the
compiled Mongo pipeline's null-bucket stages reference `_id`, and its trailing `$project` stage
restricts the output to exactly the schema's exportable fields, mapping `id`/`customerName` back
to their logical output keys via the expression form of `$project`. Without it, the row stream
would carry every field in the underlying document, not just the schema's exportable set — the
same allowlist-enforcement concern fixture 008 already demonstrates for its explicit
`exportColumns` subset.
