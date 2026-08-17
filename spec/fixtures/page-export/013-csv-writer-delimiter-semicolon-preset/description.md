# CSV writer — first-class semicolon delimiter preset

**Proves:** EXP-4's `;` delimiter preset ("Excel in French and most European locales expects `;` and
misparses `,`") — **byte for byte**, against `expected/export.csv`.

## Scenario

The same request and the same `rowStream.json` as fixture 012, and a `writerConfig.json` carrying
`{"delimiter": ";"}`. The delimiter is **not** part of the AG Grid wire contract (EXP-1 exposes no
such field on the request), which is why it lives in a separate writer-configuration file rather than
in `agGridRequest.json`.

Sharing 012's row stream is what makes this fixture load-bearing: the two expected files are the same
three rows under two delimiters, so the diff between them is exactly EXP-4's rule that **quoting
follows the active delimiter**.

- `customerName` = `Acme, Inc.` — quoted in 012, **bare** here. `,` is no longer a delimiter.
- `notes` = `Ship to dock 3; ask for Ana` — bare in 012, **quoted** here.
- `customerName` = `Bill "Big" Ross Ltd` and the LF-carrying `notes` are quoted in both: the quote
  character and the line endings are quoting triggers under every delimiter.

## What is deliberately NOT configured here: the decimal separator

`totalAmount` still renders `1234.50` with a `.`, because this file overrides the delimiter and
nothing else. EXP-4 makes `;` a **delimiter** preset; EXP-5 keeps number and date formatting on the
invariant culture until a per-field override says otherwise, and pairing the two *here* would make
this fixture's bytes depend on a coupling the TRD does not state — and would make one fixture test
two requirements at once.

The pairing is nonetheless the reason the preset exists, and it is legal: `;` delimiter + `,` decimal
separator is the classic French/European locale, and it is exactly what EXP-5's collision guard
permits *because* the delimiter is no longer `,`. **Fixture 026 is that pairing**, added by #1653 with
EXP-5's per-field overrides: this fixture's request, row stream and `writerConfig.json` byte for byte,
over a `schema.json` that declares `format.decimalSeparator: ","` on `totalAmount` (and a
`format.datePattern` on `orderDate`). The diff between the two expected files is therefore exactly
EXP-5's overrides, with EXP-4's quoting — this fixture's subject — held constant.

The collision the guard forbids is `,` delimiter + `,` decimal separator, from either source, and no
fixture can pin it: the writer refuses before producing any bytes, so there is no output file to
compare. That refusal is unit-tested in `Craticula.Export.Csv.Tests`, in both its writer-wide and its
per-field form; the category README records why it cannot be a fixture.

## Documented writer expectation (EXP-4) — asserted

- **Delimiter:** `;`, via the writer's first-class French/European-locale preset — an explicit
  configuration choice, not something inferred from the request or the schema.
- Everything else is unchanged from fixture 012: header row on, CRLF records, BOM on, RFC 4180
  quoting — with the trigger set following the delimiter, as above.
