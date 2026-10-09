# Hostile export value — leading "+" (formula injection)

**Proves:** EXP-6 (CSV formula-injection hardening) + EXP-8 (hostile-value fixtures required) —
**byte for byte**, against `expected/export.csv`. This is **safe-handling, not rejection** — unlike a
SEC-6 adversarial fixture (a different category, a different fixture shape with
`expected/rejection.json`), a hostile export value is never rejected by the compiler. The value is a
perfectly legal `typedValue` (a `string`, per `ir.schema.json`'s `stringValue`) and compiles normally.

This case was prose until #1652. It carried a compiled artifact and a written-down expectation
because the writer had no hardening to grade; `Craticula.Export.Csv.FormulaInjectionHardening` now
exists, so the case carries the writer's other two inputs as well (`rowStream.json` — the rows an
execution adapter yields for this query — and no `writerConfig.json`, because the defaults are the
point) and its output is compared like every query arm in the suite. See the category README's "CSV
writer fixtures" note for the file shapes.

## Scenario

A text-`equals` filter on `notes` carries the value `"+1234567890"` — chosen because
an `equals` filter's value is exactly the value any matching row's `notes` cell would contain,
so this fixture can derive the exported cell from the request itself without needing a seeded dataset
(this fixture carries no `dataset.json`/`expectedRows.json` yet: seeds are authored per fixture
under #1655 (`spec/semantics.md` XEQ-1), and a row stream is not a seed either way).

It is also the most ordinary of the six values: an international phone number written the way people
write them. A leading `+` is a legacy Lotus 1-2-3 formula introducer that Excel still honours, so the
cell a user typed as a contact number is evaluated as an expression on open — which is why this
fixture matters more than the deliberately hostile ones.

`exportFormat: "csv"`, `exportScope: "all"`, **no `writerConfig.json`**: EXP-6 is "default on,
configurable off", and the absence of any writer configuration is what makes these bytes an oracle
for the default rather than for a setting.

## What the row stream is

`rowStream.json` lists the rows in the order the compiled query yields them — `sortModel` is empty, so
SEM-3/SEM-6's `id ASC NULLS LAST` tiebreaker is the whole `ORDER BY`. `notes` is `"+1234567890"` in
**every** row because the request's `equals` filter says so; that cell is *derived*, not chosen. The
other six cells are **chosen, not derived**: no filter constrains them, and they are deliberately
unremarkable so that the only difference between this file and an unhardened one is the `notes`
column.

## Key expectation — the compiler passes the value through unchanged

The compiled artifact's parameter/query value for this filter is the **exact, unmodified** raw
string — no escaping, no prefixing, no rejection. Injection hardening is explicitly EXP-6's
concern, and EXP-6 places it at the CSV-writing layer ("CSV formula injection ... values ...
prefixed with `'` — default on, configurable off"), never at compile time. This fixture's
artifact intentionally demonstrates that the compiler has **no opinion** on this value's shape.

## Writer expectation (EXP-6) — asserted

When this same raw string is written as a CSV cell, the writer detects that it starts with one of
EXP-6's six trigger characters and prefixes the cell with a leading apostrophe (`'`) **before**
quoting/escaping per RFC 4180 — neutralizing the leading character's spreadsheet-formula
interpretation (Excel/Sheets/LibreOffice all treat a cell starting with `=`, `+`, `-`, `@`, a
tab, or a CR as a formula/command) while leaving the visible text otherwise intact. This
behavior defaults **on** and is configurable off (EXP-6, `CsvWriterOptions.HardenFormulaInjection`).

`expected/export.csv` in full — three records, each terminated by CRLF **including the last**, the
whole file preceded by the default UTF-8 BOM (`EF BB BF`):

```
id,orderDate,customerName,totalAmount,status,region,notes
1701,2026-03-09,Caldera Systems,75.25,shipped,AMER,'+1234567890
1702,2026-03-11,Dunmore Supply,512.00,shipped,EMEA,'+1234567890
```

`'+1234567890` is written **unquoted**: the apostrophe is not an RFC 4180 quoting trigger and neither
is anything else in the value. Hardening and quoting are independent rules that compose, which `020`
is the oracle for.

**Scope — every cell except the machine-formatted ones.** #1652 narrowed EXP-6's unqualified
"values starting with…" by *excluding* the cells the writer formatted from a numeric, date/time or
boolean value; every other cell is in scope, and a header always is (arbitrary client text, per
EXP-1(b)). `totalAmount` is a `decimal` and `orderDate` a `date`, so neither is ever prefixed however
it happens to format. The rule is deliberately NOT spelled "a cell whose CLR value is a `string`":
the two readings agree on every type either names and part company on an unanticipated one — a text
column arriving as a driver wrapper renders its text correctly but is not a `string`, and the
string-test reading would write it unhardened with nothing in the file looking wrong. `018` carries the negative decimal that makes the narrowing necessary, and
`docs/TRD-grid-query-compiler.md` §6.1 EXP-6 is where it is recorded normatively.
