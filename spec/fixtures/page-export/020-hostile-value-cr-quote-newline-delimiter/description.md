# Hostile export value — leading CR, embedded quote/newline/delimiter

**Proves:** EXP-6 (CSV formula-injection hardening) + EXP-8 (hostile-value fixtures required).
This is **safe-handling, not rejection** — unlike a SEC-6 adversarial fixture (a different
category, a different fixture shape with `expected/rejection.json`), a hostile export value is
never rejected by the compiler. The value is a perfectly legal `typedValue` (a `string`, per
`ir.schema.json`'s `stringValue`) and compiles normally.

## Scenario

A text-`equals` filter on `notes` carries the value `"\rHe said \"hello\", then\nleft"` — chosen because
an `equals` filter's value is exactly the value any matching row's `notes` cell would contain,
so this fixture can show what would land in an exported cell without needing a seeded dataset
(`dataset.json`/`expectedRows.json` are deferred per docs/feat/1630/context.md decision #3 — no
database exists yet to derive real row content from).

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

## Additional note — this value doubles up on two EXP-6/EXP-4 concerns

This value's **leading character is a CR** (`\r`), one of the two of EXP-6's six trigger characters
(`=`, `+`, `-`, `@`, tab, CR) not otherwise covered by fixtures 016-019 — the other is the tab, which
fixture 022 carries — so the future writer
must still prefix it with a leading apostrophe. It **also** embeds a double quote (`"`), a comma
(the default CSV delimiter), and a newline (`\n`) inside the value itself — independent of the
formula-injection concern, RFC 4180 (EXP-4) requires the writer to wrap this field in double
quotes and double every embedded `"` (`""`) when serializing it, regardless of whether the
leading-character apostrophe prefix is also applied. Both rules apply to the same cell,
independently, in the future writer.
