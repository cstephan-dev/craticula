# Export wire contract — exportFormat: "tsv"

**Proves:** EXP-1's request extension, `exportFormat: "tsv"` leg — proving the format leg is a
closed two-value choice (`"csv" | "tsv"`) that the compiler treats identically either way.

## Scenario

Same shape as fixture 004 but `exportFormat: "tsv"`, a different filter (`region in ["EMEA",
"APAC"]`) and no sort.

## Key expectation

Exactly like `"csv"`, `exportFormat: "tsv"` never reaches the compiler — the compiled artifact
is identical in shape to the equivalent non-export request. The only downstream difference (EXP-4,
out of this fixture's scope) is the writer choosing a tab delimiter instead of comma/semicolon.

## SEM-6 tiebreaker + SEM-3 null-ordering (category-wide fix)

`sortModel` is empty here, so — per SEM-6's own unconditional rule, which applies "regardless of
whether the fixture's `agGridRequest.json` carries a `sortModel` at all", including under export
scopes, which use the identical sort-compilation path as ordinary paged requests — the compiler
appends the schema's `stableKey` (`id`) as the sole sort key: `{ field: "id", direction: "asc",
nullsPosition: "last" }`. Every backend renders its own explicit null-ordering marker for this key
(SEM-3, also unconditional): Postgres `NULLS LAST`, Mongo's null-bucket `$addFields`/`$sort`/
`$unset` emulation, Search's `sort[].missing: "_last"`.

## Mongo physical-path note + `$project` security fix

`id`'s declared `physical.mongo.path` in this schema is `_id`, and `customerName`'s is
`customer.name` — the compiled Mongo pipeline's null-bucket stages reference `_id`, and its
trailing `$project` stage maps both fields back to their logical output keys
(`"id": "$_id"`, `"customerName": "$customer.name"`) while restricting the output to exactly the
schema's exportable fields — without it the row stream would leak every field in the underlying
document.
