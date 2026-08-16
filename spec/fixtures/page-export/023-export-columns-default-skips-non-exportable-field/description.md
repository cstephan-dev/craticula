# Default projection — a field declared `exportable: false` is left out

**Proves:** EXP-1(b)'s default projection is "every field the schema declares **that is exportable**,
in declaration order" — the `exportable` half of EXP-1's wording ("all schema fields flagged
exportable are emitted in schema order"), which no other fixture in the suite exercises.

## Why this fixture exists

`grid-schema.schema.json` has carried a per-field `exportable` flag (and a `label`) since #1631,
and `FieldConfig.Exportable` defaults to `true` — so **every** schema in the suite was implicitly
exercising only the `true` branch. Fixture 009 pins "all seven fields, in schema order" against a
schema where all seven are exportable, which cannot tell "honours the flag" apart from "ignores the
flag". This case declares `notes` with `"exportable": false` and pins the six-column projection that
follows, on all three backends.

## Scenario

`exportFormat: "csv"`, `exportScope: "all"`, **no** `exportColumns` (so the default projection
applies), and a `filterModel` on `notes` — the very field the export omits.

## Key expectations

1. **The projection is six columns, not seven.** `id, orderDate, customerName, totalAmount, status,
   region`, in the schema document's own declaration order; `notes` is absent from Postgres'
   `SELECT` list and from Mongo's `$project`, and Search names no output columns at all (OSE-13).
2. **`exportable: false` is not `filterable: false`.** `notes` is still filtered on, and the
   `WHERE`/`$match`/`query` clauses reference it exactly as they would for any other field. The two
   flags are independent: SC-2's `filterable` governs what may narrow the result set, EXP-1's
   `exportable` governs what appears in the exported file. The filter's own rendering is the same
   sensitive-string `equals` this category's 016-020/022 and 004 already pin (`"notes" = $1`,
   `{"notes": "priority"}`, `term` on the keyword sub-field with no `case_insensitive`).
3. **`label` is invisible in every compiled artifact.** `customerName` declares
   `"label": "Customer name"`, and no arm contains that string: a header is what the CSV writer
   (EXP-4, #1651/#1652) puts in the header row, never something a compiler renders — the same reason
   fixture 008's three custom `header` overrides appear in none of its artifacts.

## SEM-6 tiebreaker

`sortModel` is empty, so the compiler appends the schema's `stableKey` (`id`) as the sole sort key,
per SEM-6's unconditional rule. It happens to be exportable here, but that is incidental: a sort key
need not be in the projection at all (fixture 008 orders on an `id` it does not export).
