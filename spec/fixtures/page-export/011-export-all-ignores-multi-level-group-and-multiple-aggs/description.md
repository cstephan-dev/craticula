# Export ignores multi-level group state and multiple aggregations — leaf rows only

**Proves:** the same EXP-1 carve-out as fixture 010, pushed harder: a two-level group with two
simultaneous aggregation functions, still flattened to leaf rows.

## Scenario

`rowGroupCols: [region, status]` (two levels), `valueCols` carries **two** aggregations on
`totalAmount` (`sum` and `avg`), `groupKeys: []`. `exportScope: "all"`. If this were a
non-export SSRM request, this would compile the first grouped level (`region`) with both
aggregations computed per group.

## Key expectation

Same as fixture 010: the compiled artifact is the flat, ungrouped leaf-row query — no `GROUP
BY`/`$group`/`aggs` for *either* aggregation function, and no per-`region` bucketing. Adding a
second group level and a second aggregation function changes nothing about export's leaf-row
carve-out; it is not a "shallow-group-only" exception, it is a blanket rule for any active group
state.

## SEM-6 tiebreaker + SEM-3 null-ordering (category-wide fix)

`sortModel` is empty here, so the compiler appends the schema's `stableKey` (`id`) as the sole
sort key, per SEM-6's unconditional rule — regardless of export scope or the active (but
ignored, per this fixture's own point) multi-level group state. Every backend renders its own
explicit null-ordering marker (SEM-3): Postgres `NULLS LAST`, Mongo's null-bucket
`$addFields`/`$sort`/`$unset` emulation, Search's `sort[].missing: "_last"`.

## Mongo physical-path note + `$project` security fix

Same fix as fixture 010: the compiled Mongo pipeline's null-bucket stages reference `_id` (`id`'s
declared physical path), and its trailing `$project` stage restricts the output to exactly the
schema's exportable fields, mapping `id`/`customerName` back to their logical output keys —
without it the row stream would leak every field in the underlying document.
