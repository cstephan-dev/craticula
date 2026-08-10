# Hostile export value — leading tab (formula injection)

**Proves:** EXP-6 (CSV formula-injection hardening) + EXP-8 (hostile-value fixtures required).
This is **safe-handling, not rejection** — unlike a SEC-6 adversarial fixture (a different
category, a different fixture shape with `expected/rejection.json`), a hostile export value is
never rejected by the compiler. The value is a perfectly legal `typedValue` (a `string`, per
`ir.schema.json`'s `stringValue`) and compiles normally.

## Scenario

A text-`equals` filter on `notes` carries the value `"\tImportant client"` — a leading tab
character (`\t`) followed by otherwise unremarkable text. Chosen because an `equals` filter's
value is exactly the value any matching row's `notes` cell would contain, so this fixture can
show what would land in an exported cell without needing a seeded dataset
(`dataset.json`/`expectedRows.json` are deferred per docs/feat/1630/context.md decision #3 — no
database exists yet to derive real row content from).

**Category-completeness note:** fixtures 016-020 cover four of EXP-6's five documented trigger
characters (`=`, `+`, `-`, `@`) plus a leading CR (020) — this fixture adds the remaining trigger,
a leading tab, which none of 016-020 exercise on its own.

## Key expectation — the compiler passes the value through unchanged

The compiled artifact's parameter/query value for this filter is the **exact, unmodified** raw
string — no escaping, no prefixing, no rejection. Injection hardening is explicitly EXP-6's
concern, and EXP-6 places it at the CSV-writing layer ("CSV formula injection ... values ...
prefixed with `'` — default on, configurable off"), never at compile time. This fixture's
artifact intentionally demonstrates that the compiler has **no opinion** on this value's shape.

## Documented future writer expectation (EXP-6, no CSV writer exists yet)

When (and only when) this same raw string is written as a CSV cell, the writer MUST detect that
it starts with a hostile leading character and prefix the cell with a leading apostrophe (`'`)
before quoting/escaping per RFC 4180 — neutralizing the leading character's spreadsheet-formula
interpretation (Excel/Sheets/LibreOffice all treat a cell starting with `=`, `+`, `-`, `@`, a
tab, or a CR as a formula/command) while leaving the visible text otherwise intact. This
behavior defaults **on** and is configurable off (EXP-6).

## Null-ordering / tiebreaker note (SEM-3, SEM-6)

Same as every other fixture in this category: `sortModel` is empty, so the compiler appends the
schema's `stableKey` (`id`) as the sole sort key, with its own explicit null-ordering marker
(`NULLS LAST`) — unconditional, per SEM-3/SEM-6, regardless of `exportScope`.
