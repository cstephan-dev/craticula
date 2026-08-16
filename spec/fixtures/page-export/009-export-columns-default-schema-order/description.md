# exportColumns absent — default to schema order

**Proves:** EXP-1's `exportColumns` default leg: "when `exportColumns` is absent, all schema
fields flagged exportable are emitted in schema order, headers from schema labels."

## Scenario

`exportFormat: "csv"`, `exportScope: "all"`, `exportColumns` **absent** entirely (not an empty
array — `ir.schema.json`-adjacent conventions elsewhere in this suite always give "absent" and
"empty" distinct meaning, and EXP-1's wording is specifically about absence).

## Note — this fixture's schema declares no `label`, by choice

The normative rule is **EXP-1(b)** in `spec/semantics.md`: absent `exportColumns`, the projection is
every field the schema declares **that is `exportable`**, in declaration order, with headers from
each field's `label` falling back to its colId. Both properties exist —
`spec/grid-schema.schema.json` declares `exportable` and `label` per field, and `FieldConfig`
carries them.

This fixture's own `schema.json` declares neither on any field, which is a *choice*, not a gap: with
`exportable` defaulting to `true` it pins the all-exportable seven-column case (`id, orderDate,
customerName, totalAmount, status, region, notes`), and with no `label` anywhere the headers are the
bare colIds. Fixture **023** is the counterpart that declares `"exportable": false` on one field and
carries the suite's only `label` — a header appears in no compiled artifact either way, since it is
the CSV writer's (EXP-4, #1651/#1652).

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
