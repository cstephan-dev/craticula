# CSV writer — default comma delimiter, header row on

**Proves:** EXP-4's default delimiter (`,`) and default header-row-on behavior, as a documented
expectation — **not** a byte-exact CSV fixture. No CSV writer exists yet (this story delivers the
artifact-equality tier only, per docs/feat/1630/context.md decision #3); producing a literal
byte-exact CSV file would require running code that does not exist.

## Scenario

`exportFormat: "csv"`, `exportScope: "all"`, no writer configuration overrides. The compiled
artifact below (Postgres only — the interesting behavior for this fixture is entirely at the
writer layer, not the compiler layer, so a single representative backend suffices) is the plain
filtered+sorted row-stream source.

## Documented writer expectation (EXP-4, no writer exists to assert this today)

- **Delimiter:** `,` (comma) — the default, absent any `;` preset override.
- **Header row:** on by default — the first emitted line/record is the column headers, resolved
  the same way as fixture 009's default `exportColumns`-absent projection (schema field order,
  `colId` as header pending #1631's label field).
- **Line endings:** CRLF (`\r\n`) per RFC 4180, for every record including the header row.
- **Quoting:** any field value containing the delimiter, a double quote, or a CR/LF is wrapped in
  double quotes, with embedded double quotes doubled (`""`) — RFC 4180 §2 rules 5-7. See fixture
  020 for a concrete hostile value exercising this rule.
