# Range page — first page, size 50

**Proves:** AG-3 (startRow/endRow -> IR `pageSpec` `mode: "range"`), SEM-3's pinned adapter
default for `nullsPosition`.

## Scenario

A first-page grid load: `startRow: 0`, `endRow: 50` (page size 50), sorted ascending by
`orderDate`. No filter, no export fields on the request — this is the plain AG-3 paging
baseline the export cases in this same directory are contrasted against.

## Key expectation

The IR's `page` field is `{ mode: "range", startRow: 0, endRow: 50 }` (`ir.schema.json`
`$defs/rangePage`). Each backend renders that as its own native bounded-range mechanism:
Postgres `LIMIT/OFFSET`, Mongo `$skip`/`$limit`, Search `from`/`size`. The sort's
`asc` direction carries the SEM-3-pinned `nullsPosition: "last"` — on every backend, not just
Postgres: rendered as Postgres' native `NULLS LAST`, Mongo's null-bucket
`$addFields`/`$sort`/`$unset` emulation, and Search's `sort[].missing: "_last"`. A prior revision
of this fixture claimed "Mongo/Search null-ordering emulation is out of scope for this fixture" —
that was wrong: SEM-3 requires the marker on every `sortItem` on every backend unconditionally,
and this category's own fixtures are exactly where a compiler would produce it, `sort-paging/`'s
exhaustive coverage notwithstanding.

**SEM-6 tiebreaker.** The requested sort (`orderDate ASC`) doesn't already end in the schema's
`stableKey` (`id`), so the compiler appends `{ field: "id", direction: "asc", nullsPosition:
"last" }` as the final sort key on every backend — Postgres' `ORDER BY` gains a second key,
Mongo's `$sort` gains a second null-bucket/field pair, Search's `sort` array gains a second
element. Mongo's tiebreaker targets `_id` (the schema's declared `physical.mongo.path` for `id`
in this "orders" grid — see `schema.json`), not a bare `id` field that doesn't exist in the
collection's documents.
