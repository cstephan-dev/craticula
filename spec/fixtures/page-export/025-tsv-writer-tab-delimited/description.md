# TSV writer — tab delimiter, byte-exact output

**Proves:** EXP-4's *configurable* delimiter reaches its third value — `exportFormat: "tsv"` is what
makes the writer emit tabs — and that RFC 4180 quoting follows the **active** delimiter: the tab is
quoted here, while the `,` and `;` that fixtures 012/013 quote are written bare.

Authored by **#1651**, because nothing pinned tab-delimited *output* bytes. Fixtures 004 and 005
prove `exportFormat` never affects **compilation** and deliberately stay that way — the format
field's whole effect is at the writer layer, and until this case existed that effect was asserted by
nothing. This fixture's `expected/postgres.artifact.json` is byte-identical to 012's, which is the
same claim 004/005 make, restated here so the two halves sit in one directory: identical query,
different bytes out.

## Scenario

Same request as fixture 012 (`exportScope: "all"`, no `exportColumns`, filter `status = "shipped"`,
sort `orderDate ASC`) with `exportFormat: "tsv"`. **No `writerConfig.json`** — the delimiter comes
from the wire's format, and everything else is a writer default (header row on, BOM on).

## What the row stream is

`rowStream.json`, in the order the compiled query's `ORDER BY` yields (`order_date ASC`, then the
SEM-6 `id` tiebreaker). `status` is `"shipped"` in every row because the request's `equals` filter
says so — any row this query returns has exactly that value in that cell. The other six cells are
**chosen, not derived**: no filter constrains them, and they are picked to exercise the delimiter's
effect on quoting.

- `notes` on row 3001 contains a **tab**, which the active delimiter makes a quoting trigger.
- `customerName` on the same row contains a `,` and `notes` on 3002 a `;` — both written **bare**,
  because neither is the delimiter here. That pair is the point of the fixture: quoting is a function
  of the configured delimiter, not of a fixed character.

## Craticula's TSV is RFC 4180 with a tab, not IANA `text/tab-separated-values`

That media type has no quoting mechanism at all; it simply forbids a tab inside a field. Adopting it
would force the writer either to reject rows or to silently mangle them. EXP-4 asks for one rule set
("RFC 4180: CRLF line endings, quoting/escaping of delimiter, quote, and newline characters") over a
**configurable** delimiter, so a field containing a tab is quoted exactly as a field containing a
comma is under CSV. This fixture is the byte oracle for that reading.

## Documented writer expectation (EXP-4) — asserted, not prose

`expected/export.tsv` is compared **byte for byte**, BOM included. The output is:

- three bytes `EF BB BF` (BOM on by default), then
- the header row `id`…`notes` tab-separated (header row on by default), then
- two data rows, each terminated by CRLF including the last.
