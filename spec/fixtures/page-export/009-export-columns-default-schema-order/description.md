# exportColumns absent — default to schema order

**Proves:** EXP-1's `exportColumns` default leg: "when `exportColumns` is absent, all schema
fields flagged exportable are emitted in schema order, headers from schema labels."

## Scenario

`exportFormat: "csv"`, `exportScope: "all"`, `exportColumns` **absent** entirely (not an empty
array — `ir.schema.json`-adjacent conventions elsewhere in this suite always give "absent" and
"empty" distinct meaning, and EXP-1's wording is specifically about absence).

## Note — a gap in `grid-schema.schema.json` this fixture works around

`spec/grid-schema.schema.json` (SC-1..SC-5), as authored ahead of the real `GridSchema` model
(#1631), has **no per-field `exportable` flag** and **no per-field `label`** yet — both of
which EXP-1's default wording assumes exist ("fields flagged exportable", "headers from schema
labels"). Until #1631 adds them, this fixture treats every field present in `schema.fields` as
exportable by default, in the object's own key order (`id, orderDate, customerName, totalAmount,
status, region, notes`), and uses the bare `colId` as the header (no schema label to draw from).
This is a documented assumption, not a claim that the schema already carries these flags.

## Key expectation

The compiled artifact's projection is **all seven** schema fields, in schema-declaration order —
contrast fixture 008's caller-chosen three-field subset and order.

## Aliasing note — Postgres physical-to-logical column names

Three of the seven physical columns differ from their logical field name (`order_date` ->
`orderDate`, `customer_name` -> `customerName`, `total_amount` -> `totalAmount`); the compiled
`SELECT` list aliases each of those three back to its logical name (`order_date AS orderDate`,
etc.), the same "alias only when the physical name differs" convention as
`group-aggregation/`'s 005-006 fixtures and this category's own fixture 008. The remaining four
(`id`, `status`, `region`, `notes`) already match their logical name and need no alias.

## Mongo `$project` — physical-path mapping + allowlist enforcement

The compiled Mongo pipeline ends with a `$project` stage listing exactly these seven logical
field names, mapping `id`/`customerName` back from their declared physical paths (`_id`,
`customer.name`) via the expression form of `$project` (`"id": "$_id"`, `"customerName":
"$customer.name"`) — the shorthand inclusion form cannot rename a field while including it, and
projecting `_id`/`customer.name` unmapped would leak Mongo's own internal field name /
a nested shape instead of the flat logical field the grid expects. The `$project` also serves
EXP-1's allowlist-enforcement role: without it, the row stream would carry every field present in
the underlying document, not just the schema's exportable set.

## SEM-6 tiebreaker + SEM-3 null-ordering (category-wide fix)

`sortModel` is empty here, so the compiler appends the schema's `stableKey` (`id`) as the sole
sort key, per SEM-6's unconditional rule — regardless of export scope. Every backend renders its
own explicit null-ordering marker (SEM-3): Postgres `NULLS LAST`, Mongo's null-bucket
`$addFields`/`$sort`/`$unset` emulation (targeting `_id`, run before the `$project` strips it),
Search's `sort[].missing: "_last"`.
