# CSV writer — UTF-8 BOM explicitly disabled

**Proves:** EXP-4's BOM toggle is genuinely configurable ("Optional UTF-8 BOM, default **on**")
— off is a valid, supported configuration, not merely a theoretical default-override.

## Scenario

Same request/artifact shape as fixture 012/014. As with fixture 013's delimiter preset, BOM
on/off is writer configuration, outside the AG Grid wire contract, so it is documented here in
prose rather than as an `agGridRequest.json` field.

## Documented writer expectation (EXP-4)

- **BOM:** explicitly configured off — the output stream's first bytes are the header row itself
  (or the first data row's bytes, if the header row is also disabled), with no `EF BB BF`
  prefix. A downstream consumer that already handles UTF-8 without a BOM (most non-Excel
  pipelines, `git diff`-friendly output, etc.) is the intended use case for this configuration.
