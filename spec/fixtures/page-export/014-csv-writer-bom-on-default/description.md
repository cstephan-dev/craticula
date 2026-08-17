# CSV writer — UTF-8 BOM on (default)

**Proves:** EXP-4's default-on UTF-8 BOM ("Excel misreads UTF-8 without it") — **byte for byte**,
against `expected/export.csv`, whose first three bytes are `EF BB BF`.

## Scenario

The same request as fixture 012 and **no `writerConfig.json`**: the BOM is on because nothing turned
it off, which is the claim. Fixture 015 is this same fixture with `{"bom": false}`, over the same
`rowStream.json`, and the two expected files differ by exactly those three bytes — a property the
conformance suite asserts directly, not just by comparing each against its own file.

## What the row stream is

`rowStream.json`, ordered by the compiled query's `ORDER BY` (`order_date ASC`, then SEM-6's `id`).
`status` is `"shipped"` in every row, derived from the request's `equals` filter. The other cells are
**chosen, not derived**, and unlike fixture 012's they are chosen to be **non-ASCII** — `Éditions
Chêne`, `Кофе и Ко`, `お届け済み`, an em dash. A BOM fixture whose payload was pure ASCII would be a
fixture about three bytes that no consumer needed: it is precisely the multi-byte content that Excel
misreads without the mark, so the rows here are what makes the requirement's own justification
visible in the file.

No cell begins with `=`, `+`, `-`, `@`, tab or CR — see fixture 012 for why that matters.

## Documented writer expectation (EXP-4) — asserted

- **BOM:** `EF BB BF` written as the first three bytes of the stream, **before** the header row, by
  default. A writer emitting a CSV/TSV export without an explicit opt-out must include it.
- Everything else is fixture 012's: comma delimiter, header row on, CRLF records, RFC 4180 quoting
  (no trigger character occurs in these rows, so no field here is quoted — that is 012's job).
- A host that knows its consumer is not Excel can opt out — see fixture 015.
