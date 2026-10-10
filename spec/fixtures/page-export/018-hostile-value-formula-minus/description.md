# Hostile export value — leading "-" (formula injection), and the scope of EXP-6

**Proves:** EXP-6 (CSV formula-injection hardening) + EXP-8 (hostile-value fixtures required) —
**byte for byte**, against `expected/export.csv`. This is **safe-handling, not rejection** — unlike a
SEC-6 adversarial fixture (a different category, a different fixture shape with
`expected/rejection.json`), a hostile export value is never rejected by the compiler. The value is a
perfectly legal `typedValue` (a `string`, per `ir.schema.json`'s `stringValue`) and compiles normally.

**It also proves the one thing no other fixture in the suite does: which cells EXP-6 applies to.** See
"The scope of the rule" below — that section is this case's real subject, and it is the reason this
fixture carries a **negative** `totalAmount` while no other case in the category does.

This case was prose until #1652. It carried a compiled artifact and a written-down expectation
because the writer had no hardening to grade; `Craticula.Export.Csv.FormulaInjectionHardening` now
exists, so the case carries the writer's other two inputs as well (`rowStream.json` — the rows an
execution adapter yields for this query — and no `writerConfig.json`, because the defaults are the
point) and its output is compared like every query arm in the suite. See the category README's "CSV
writer fixtures" note for the file shapes.

## Scenario

A text-`equals` filter on `notes` carries the value `"-2+3"` — chosen because
an `equals` filter's value is exactly the value any matching row's `notes` cell would contain,
so this fixture can derive the exported cell from the request itself without needing a seeded dataset
(this fixture carries no `dataset.json`/`expectedRows.json` yet: seeds are authored per fixture
under #1655 (`spec/semantics.md` XEQ-1), and a row stream is not a seed either way).

`exportFormat: "csv"`, `exportScope: "all"`, **no `writerConfig.json`**: EXP-6 is "default on,
configurable off", and the absence of any writer configuration is what makes these bytes an oracle
for the default rather than for a setting.

## What the row stream is

`rowStream.json` lists the rows in the order the compiled query yields them — `sortModel` is empty, so
SEM-3/SEM-6's `id ASC NULLS LAST` tiebreaker is the whole `ORDER BY`. `notes` is `"-2+3"` in **every**
row because the request's `equals` filter says so; that cell is *derived*, not chosen. The other six
cells are **chosen, not derived**, and one of them is chosen very deliberately:

- Row `1801`'s `totalAmount` is **`-500.25`, a negative `decimal`** — the only negative number in the
  entire fixture suite. It formats to a string whose first character is `-`, EXP-6's third trigger,
  and it must come out **unprefixed**. Its `notes` cell, whose first character is the same `-`, must
  come out **prefixed**. One record therefore states both halves of the rule.
- Row `1802`'s `totalAmount` is positive, so the two records differ in the sign of that column and in
  nothing else that matters — which is what makes the diff readable if the narrowing ever regresses.

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
1801,2026-03-16,Estuary Marine,-500.25,credited,EMEA,'-2+3
1802,2026-03-18,Farrow Textiles,640.00,shipped,AMER,'-2+3
```

Read the first data record right to left: `'-2+3` is prefixed, `-500.25` four fields earlier is not.
Neither is quoted — the apostrophe is not an RFC 4180 quoting trigger and neither is anything else in
either value.

## The scope of the rule — every cell but the machine-formatted ones, and why

EXP-6 reads "values starting with `=`, `+`, `-`, `@`, tab, or CR are prefixed with `'`", unqualified.
**#1652 narrowed that**, and this fixture is what pins the narrowing:

> A value the writer formatted from a `decimal`, a `double`, an integer, a `date`, a `datetime`, a
> `time` or a `boolean` is **never** prefixed. **Every other cell is**, and **every header** is
> (arbitrary client text by EXP-1(b), and always a string).
>
> The rule is normative as that **exclusion**, not as "a cell whose CLR value is a `string`". The two
> readings agree on every type either one names, and differ only on a type neither anticipated — which
> is exactly where an adapter bug lands. A text column handed over as a driver's wrapper (a
> `BsonString`, a `JsonElement`) is not a `string`, yet `CsvValueFormatter` renders its text correctly
> through its `ToString()` tail; under the string-test reading that cell is written **unhardened**, and
> the failure is invisible — the file is well formed, every character is where it belongs, and the
> security control is simply absent. Excluding by default means an unrecognised type is hardened, so
> the worst an adapter bug costs is a spurious `'` on a cell: visible, and not a vulnerability.

The reason is the character this fixture is named after: `-` is also the leading character of every
negative number. Applied literally, EXP-6 turns `-500.25` into `'-500.25`, which a spreadsheet
displays as text, will not sum, and cannot coerce back without the reader knowing this writer's
convention — corrupting, in the ordinary case, the very data the export exists to deliver. And it buys
nothing: OWASP's formula injection is about content a **user authored**, whereas a value that reached
the writer as a `decimal` was produced by the database and re-rendered by EXP-5, so there is no user
text in it to neutralize. The discriminator is the CLR type of the value, not the schema's declared
`FieldType`, for the reason `CsvValueFormatter` gives — the adapter already decided how to materialize
the column, and letting the schema win over the actual value is how the two disagree silently.

**Nothing else in the suite carries a negative number**, so without this row the narrowing would ship
graded only by a unit test written by the implementation's own author. `docs/TRD-grid-query-compiler.md`
§6.1 EXP-6 is the narrowing's normative home; `spec/semantics.md` deliberately does not restate
EXP-4..EXP-8 at all.
