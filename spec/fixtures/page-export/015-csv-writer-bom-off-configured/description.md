# CSV writer — UTF-8 BOM explicitly disabled

**Proves:** EXP-4's BOM toggle is genuinely configurable ("Optional UTF-8 BOM, default **on**") — off
is a supported configuration, not merely a theoretical default-override. Compared **byte for byte**
against `expected/export.csv`, which starts with the header row's `i` and no `EF BB BF`.

## Scenario

Fixture 014's request and, byte for byte, fixture 014's `rowStream.json`, plus a `writerConfig.json`
carrying `{"bom": false}`. As with fixture 013's delimiter, BOM on/off is writer configuration
outside the AG Grid wire contract, so it lives in that file rather than in `agGridRequest.json`.

The pairing is deliberate and is asserted as such: `expected/export.csv` here is fixture 014's file
minus its first three bytes, and the conformance suite checks that relation directly. A test that
compared these files as *strings* would pass on a wrong BOM — .NET, Python and every editor strip or
transparently swallow a leading `U+FEFF` — which is why both files are read and compared as bytes.

## Documented writer expectation (EXP-4) — asserted

- **BOM:** explicitly configured off. The first bytes of the stream are the header row itself (or the
  first data row's, if the header row were also disabled). A downstream consumer that already handles
  UTF-8 without a BOM — most non-Excel pipelines, `git diff`-friendly output — is the intended use
  case.
- Everything else is unchanged from fixture 014: comma delimiter, header row on, CRLF records, and
  the same non-ASCII cells, which are what make the BOM's presence or absence matter at all.
