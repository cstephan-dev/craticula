# CSV writer — default comma delimiter, header row on

**Proves:** EXP-4's default delimiter (`,`), default header-row-on behavior, CRLF records and RFC
4180 quoting — **byte for byte**, against `expected/export.csv`.

This case was prose until #1651. It carried a compiled artifact and a written-down expectation
because no CSV writer existed; it now carries the writer's two other inputs as well (`rowStream.json`
— the rows an execution adapter yields for this query — and, for the fixtures that override a
default, `writerConfig.json`) and its output is compared like every query arm in the suite. See the
category README's "CSV writer fixtures" note for the file shapes and for which cases still document
rather than assert.

## Scenario

`exportFormat: "csv"`, `exportScope: "all"`, **no `writerConfig.json`** — the whole point of this
case is that the defaults are what produce these bytes, so configuring anything would hide the
default it exists to pin. The compiled artifact below (Postgres only — the behavior under test is
entirely at the writer layer, so a single representative backend suffices) is the filtered+sorted
row-stream source.

## What the row stream is

`rowStream.json` lists the rows in the order the compiled query's `ORDER BY` yields them
(`order_date ASC`, then SEM-6's `id` tiebreaker). `status` is `"shipped"` in every row because the
request's `equals` filter says so — an `equals` filter's value is exactly what any matching row's
cell contains. The other six cells are **chosen, not derived**: no filter constrains them, and they
are picked so that every RFC 4180 quoting trigger appears at least once.

- `customerName` on row 1001 contains the **delimiter** (`Acme, Inc.`) → quoted.
- `customerName` on row 1002 contains **double quotes** (`Bill "Big" Ross Ltd`) → quoted, with each
  inner quote doubled.
- `notes` on row 1002 contains a **line feed** → quoted, and the LF is preserved exactly as the value
  carried it. Only the *record* separator is CRLF; the writer never rewrites a value's own newlines.
- `notes` on row 1003 is **null** → an empty, unquoted field. A CSV file has no null.
- `notes` on row 1001 contains a `;`, which is **not** quoted here and is quoted in fixture 013 —
  the two files share a row stream, so the difference between them is the delimiter and nothing else.

No cell begins with `=`, `+`, `-`, `@`, tab or CR, and no `totalAmount` is negative. That is
deliberate: EXP-6's hardening (fixtures 016-020 and 022) prefixes such a value with `'`, and a trigger
character here would make these expected bytes say something about EXP-6 as well, which is not what
this case is for. It is also what kept these bytes unchanged when #1652 landed the prefixing — the
guarantee held.

## Documented writer expectation (EXP-4) — asserted

- **Delimiter:** `,` — the default, absent any preset override.
- **Header row:** on by default; the first record is the column headers, resolved the same way as
  fixture 009's default `exportColumns`-absent projection (schema field order, `colId` as header
  since this schema declares no `label`).
- **Line endings:** CRLF for every record including the header row **and the last row**. RFC 4180
  makes the final CRLF optional; emitting it always is the form that concatenates cleanly.
- **Quoting:** a field containing the delimiter, a double quote, or a CR/LF is wrapped in double
  quotes with embedded double quotes doubled (RFC 4180 §2 rules 5-7).
- **BOM:** on by default (fixtures 014/015 own that branch); `expected/export.csv` therefore starts
  with `EF BB BF`.
