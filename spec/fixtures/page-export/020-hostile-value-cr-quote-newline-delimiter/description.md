# Hostile export value — leading CR, embedded quote/newline/delimiter

**Proves:** EXP-6 (CSV formula-injection hardening) + EXP-8 (hostile-value fixtures required) —
**byte for byte**, against `expected/export.csv`. This is **safe-handling, not rejection** — unlike a
SEC-6 adversarial fixture (a different category, a different fixture shape with
`expected/rejection.json`), a hostile export value is never rejected by the compiler. The value is a
perfectly legal `typedValue` (a `string`, per `ir.schema.json`'s `stringValue`) and compiles normally.

**It is also the suite's oracle for the composition of EXP-6 and EXP-4** — the only case where the
apostrophe prefix and RFC 4180 quoting both apply to the same cell, which is what fixes the
apostrophe's position relative to the opening quote. See "The composition" below.

This case was prose until #1652. It carried a compiled artifact and a written-down expectation
because the writer had no hardening to grade; `Craticula.Export.Csv.FormulaInjectionHardening` now
exists, so the case carries the writer's other two inputs as well (`rowStream.json` — the rows an
execution adapter yields for this query — and no `writerConfig.json`, because the defaults are the
point) and its output is compared like every query arm in the suite. See the category README's "CSV
writer fixtures" note for the file shapes.

## Scenario

A text-`equals` filter on `notes` carries the value `"\rHe said \"hello\", then\nleft"` — chosen because
an `equals` filter's value is exactly the value any matching row's `notes` cell would contain,
so this fixture can derive the exported cell from the request itself without needing a seeded dataset
(this fixture carries no `dataset.json`/`expectedRows.json` yet: seeds are authored per fixture
under #1655 (`spec/semantics.md` XEQ-1), and a row stream is not a seed either way).

`exportFormat: "csv"`, `exportScope: "all"`, **no `writerConfig.json`**: EXP-6 is "default on,
configurable off", and the absence of any writer configuration is what makes these bytes an oracle
for the default rather than for a setting.

## What the row stream is

`rowStream.json` lists the rows in the order the compiled query yields them — `sortModel` is empty, so
SEM-3/SEM-6's `id ASC NULLS LAST` tiebreaker is the whole `ORDER BY`. `notes` is the hostile value in
**every** row because the request's `equals` filter says so; that cell is *derived*, not chosen. The
other six cells are **chosen, not derived**, and here they are deliberately plain: everything
interesting about this file happens inside one field, and a second quoted field in the same record
would only make the byte diff harder to read.

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
whole file preceded by the default UTF-8 BOM (`EF BB BF`). `\r`, `\n` and the record separators are
written as escapes here; on disk they are the bytes themselves:

```
id,orderDate,customerName,totalAmount,status,region,notes\r\n
2001,2026-03-30,Ironvale Metals,349.99,shipped,EMEA,"'\rHe said ""hello"", then\nleft"\r\n
2002,2026-04-01,Juniper Labs,12.05,shipped,AMER,"'\rHe said ""hello"", then\nleft"\r\n
```

**Scope — every cell except the machine-formatted ones.** #1652 narrowed EXP-6's unqualified
"values starting with…" by *excluding* the cells the writer formatted from a numeric, date/time or
boolean value; every other cell is in scope, and a header always is (arbitrary client text, per
EXP-1(b)). `totalAmount` is a `decimal` and `orderDate` a `date`, so neither is ever prefixed however
it happens to format. The rule is deliberately NOT spelled "a cell whose CLR value is a `string`":
the two readings agree on every type either names and part company on an unanticipated one — a text
column arriving as a driver wrapper renders its text correctly but is not a `string`, and the
string-test reading would write it unhardened with nothing in the file looking wrong. `018` carries the negative decimal that makes the narrowing necessary, and
`docs/TRD-grid-query-compiler.md` §6.1 EXP-6 is where it is recorded normatively.

## The composition — two rules, one cell, and the apostrophe goes INSIDE the quotes

This value's **leading character is a CR** (`\r`), one of the two of EXP-6's six trigger characters
(`=`, `+`, `-`, `@`, tab, CR) not covered by fixtures 016-019 — the other is the tab, which fixture
022 carries — so the writer prefixes it with a leading apostrophe. It **also** embeds a double quote
(`"`), a comma (the default CSV delimiter), and a newline (`\n`) inside the value itself, so —
independently of the formula-injection concern — RFC 4180 (EXP-4) requires the writer to wrap the
field in double quotes and double every embedded `"`. Both rules apply to the same cell.

The order is fixed, and it is what this fixture exists to pin: **the prefix is applied to the field
text, before the quoting decision.** The apostrophe therefore becomes the field's first character and
the quotes wrap it:

```
"'\rHe said ""hello"", then\nleft"        ← correct: apostrophe inside the opening quote
'"\rHe said ""hello"", then\nleft"        ← malformed: not a CSV field at all
```

The second form is not a badly formatted field, it is an unparseable one: a reader sees a bare
apostrophe in an unquoted field, then a stray `"` mid-field, and RFC 4180 gives it no meaning. It
would also let the prefix change whether quoting was needed at all, which no ordering may do. The
writer's single choke point (`CsvWriter.AppendField`) applies the hardening above the quoting logic
for exactly this reason.
