# exportColumns — explicit subset with custom headers

**Proves:** EXP-1's `exportColumns: [{ colId, header? }]` leg, the "explicit subset, custom
order and headers" case.

## Scenario

`exportColumns` names exactly three of the schema's seven allowlisted fields, in a
caller-chosen order, each with a custom `header` string: `customerName` -> "Customer",
`totalAmount` -> "Total", `orderDate` -> "Order Date".

## Key expectation

The compiled artifact's projection is **exactly** `[customerName, totalAmount, orderDate]`, in
that order — not the schema's own field order (compare fixture 009) and not all seven fields.
Postgres' `SELECT` list, Mongo's `$project` stage, and (implicitly) the search artifact's
result set are all narrowed to this subset. The `header` strings ("Customer", "Total", "Order
Date") are **not** reflected anywhere in the compiled artifact: header-to-column mapping is
consumed only by the downstream CSV writer (EXP-4) when it emits the header row, never by the
compiler, which only ever resolves `colId` to a physical column/path.

The search artifact's shape schema (`search-artifact.schema.json`) has no source-filtering
property, so this fixture cannot show search-side column narrowing structurally; the search
artifact here is the same shape it would be without `exportColumns` — narrowing to the requested
fields is an execution-adapter-level source filter, not part of the compiled DSL captured here.

## Aliasing note — Postgres physical-to-logical column names

Postgres' physical column for `customerName` is `customer_name`, for `totalAmount` is
`total_amount`, and for `orderDate` is `order_date` (`schema.json`'s `physical.sql.column`) — the
compiled `SELECT` list aliases each one back to its logical camelCase name (`customer_name AS
customerName`, etc.), the same "alias only when the physical name differs from the logical field
name" convention `group-aggregation/`'s 005-006 fixtures use. Without the alias, the result set's
column names would be the physical snake_case names, not the logical field names a downstream
CSV header (or any other consumer keyed by `colId`) expects.

## Mongo physical-path note

`customerName`'s declared `physical.mongo.path` is `customer.name`, a nested path, not a bare
top-level `customerName` field. The `$project` stage therefore uses the expression form
(`"customerName": "$customer.name"`) to both read the nested physical field and rename it back to
the logical output key in one step — the shorthand inclusion form (`"customer.name": 1`) cannot
rename while including a nested field, and would leave the output shaped `{customer: {name:
...}}` instead of the expected flat `{customerName: ...}`.

## SEM-6 tiebreaker + SEM-3 null-ordering (category-wide fix)

`sortModel` is empty here, so — per SEM-6's unconditional rule — the compiler appends the
schema's `stableKey` (`id`) as the sole sort key, even though `id` isn't among the requested
`exportColumns` and therefore never appears in the projection: `ORDER BY`/`$sort`/`sort` can
reference a column that isn't in the final projection (`id` is available on the underlying table/
collection/index; only the compiled *output* is narrowed). Mongo's null-bucket/sort stages
reference `_id` (the schema's declared physical path for `id`) and run *before* the `$project`
stage strips it from the output — a stage ordering the pipeline depends on, not an incidental
detail.
