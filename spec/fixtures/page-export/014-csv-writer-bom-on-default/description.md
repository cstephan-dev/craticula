# CSV writer — UTF-8 BOM on (default)

**Proves:** EXP-4's default-on UTF-8 BOM ("Excel misreads UTF-8 without it"), as a documented
expectation.

## Scenario

Same request/artifact shape as fixture 012. The behavior under test (BOM emission) is a
byte-stream-level writer concern, not something any compiled artifact field encodes.

## Documented writer expectation (EXP-4)

- **BOM:** the UTF-8 byte-order-mark (`EF BB BF`) is written as the first three bytes of the
  output stream, **before** the header row, by default. This is an on-by-default behavior — a
  writer emitting a CSV/TSV export without an explicit opt-out must include it.
- Rationale documented in EXP-4 verbatim: Excel misreads BOM-less UTF-8 CSV files (particularly
  anything outside ASCII), so the safer default is BOM-on; a host that knows its consumer isn't
  Excel (e.g. a pure programmatic pipeline) can opt out — see fixture 015.
