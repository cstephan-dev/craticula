# CSV writer — EXP-5's per-field format overrides, in the locale the `;` preset exists for

**Proves:** EXP-5's second half — *"per-field overrides in schema config (date pattern, decimal
separator)"* — **byte for byte**, against `expected/export.csv`; and that a format override reaches
**no compiler**, since this case's `expected/postgres.artifact.json` is byte-identical to fixture
013's.

Authored by **#1653**, which is the story that added the schema member (`fields.<colId>.format`) and
the writer's per-field rendering. It is the fixture the category README had reserved since #1651: the
**legal** `;` delimiter + `,` decimal separator pairing — the French/European locale EXP-4's preset is
named for — which could not be a fixture while formatting was writer-wide, because pairing the two in
013 would have made *that* fixture's bytes depend on a coupling the TRD does not state. Here the
coupling is stated, in the two places the TRD puts it: the delimiter in the host's writer
configuration (EXP-4), the separator and the date pattern in the schema (EXP-5).

## Scenario

Fixture 013's request, row stream and `writerConfig.json`, unchanged and byte-identical. The **only**
input that differs is `schema.json`, which adds a `format` member to exactly two fields:

| Field | Override | Why this field |
|---|---|---|
| `orderDate` | `"datePattern": "dd/MM/yyyy"` | the only `date`-typed field in the projection |
| `totalAmount` | `"decimalSeparator": ","` | the only field with a fractional part to separate |

The other five fields declare no `format` at all, which is what makes this case an oracle for
*per-field*: `id` is an integer and keeps no separator, and `customerName`/`status`/`region`/`notes`
are strings the writer passes through untouched. An implementation that applied either override
file-wide would still produce a well-formed file — and would fail here, because `id` would not change
and nothing else can show that.

The two overrides sit on **different fields of the same row**, on purpose. One override alone cannot
distinguish "resolved per column" from "resolved once and applied to whatever matches"; two, landing
in two different columns of every record while five columns are untouched, can.

`dd/MM/yyyy` is written in the LDML field letters every platform's formatter shares, which is what
makes these bytes reproducible by a language port — see `spec/grid-schema.schema.json`'s
`datePattern` description for that constraint and for the one specifier (an offset, `z`/`K`) a
portable pattern must avoid.

## What the row stream is

`rowStream.json`, **byte-identical to 013's** (and therefore to 012's), in the order the compiled
query's `ORDER BY` yields: `order_date ASC`, then the SEM-6 `id` tiebreaker. `status` is `"shipped"`
in every row because the request's `equals` filter says so; every other cell is *chosen, not derived*,
and was chosen by 012 to exercise RFC 4180 quoting under two delimiters. Reusing it unchanged is the
point: the diff between this file and 013's `expected/export.csv` is **exactly** EXP-5's two
overrides, with EXP-4's quoting held constant.

## Documented writer expectation (EXP-5) — asserted, not prose

`expected/export.csv` is compared **byte for byte**, BOM included. Against 013's bytes, and against
nothing else:

- `orderDate` renders `05/01/2026`, `05/01/2026`, `14/02/2026` instead of the invariant ISO
  `2026-01-05`/`2026-02-14`. The `/` is neither the delimiter nor an EXP-6 trigger, so no quoting or
  prefixing changes with it.
- `totalAmount` renders `1234,50`, `99,00`, `0,00` instead of `1234.50`/`99.00`/`0.00`. The scale is
  preserved exactly as before — the separator swap happens on the already-formatted digits, so
  `1234,50` keeps the trailing zero a `NUMERIC(10,2)` column declared.
- **Nothing is quoted that was not quoted in 013.** `,` is not the delimiter here, which is precisely
  what makes this pairing legal: under the default `,` delimiter the same schema is EXP-5's collision
  and the writer refuses the export before its first byte, naming this field.
- The header row, the BOM, the CRLF records and the three quoted fields (`notes` on rows 1001 and
  1002, `customerName` on 1002) are identical to 013's — a format override changes how a value is
  rendered and nothing about how the record is framed.

## What is deliberately NOT here

- **No Mongo or Search arm.** Like every writer-facing case (012-020, 022, 025), the behaviour under
  test is at the writer layer, so one representative compiled backend is enough.
- **No `hardenFormulaInjection`, no BOM or header-row override.** `writerConfig.json` carries the
  delimiter and nothing else, exactly as 013's does; every other byte-level property of this file is a
  writer default, and would fail here if one of them changed.
- **The illegal pairing has no fixture and cannot have one** — a refused export produces no file to
  compare. Both the writer-wide collision and the per-field one (this schema under a `,` delimiter,
  which names the offending field in its message) are unit-tested in `Craticula.Export.Csv.Tests`.
