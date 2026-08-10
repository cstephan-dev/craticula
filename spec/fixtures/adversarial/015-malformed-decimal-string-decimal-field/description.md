# 015 — malformed decimal string against a `decimal` field

## What makes this adversarial
`amount` is declared `type: "decimal"` in `schema.json` (`spec/ir.schema.json`'s
`decimalValue` requires `^-?[0-9]+(\.[0-9]+)?$`). The number filter's `filter` value is
sent as `"12.34.56"` — a string with two decimal points, matching neither a plain integer nor
a single-decimal-point number, so it cannot resolve into a valid `decimalValue` no matter how
lenient the parser is.

## Which validation stage must catch it
**Type coercion.** `amount` is allowlisted and this is a single well-formed condition far
under every SEC-3 limit; only the decimal-parsing step fails.

## Requirement ids
SEM-5 (type coercion, Strict mode), AR-7 (`decimal` carried as a numeric string specifically
to avoid float round-tripping loss — this fixture proves the corresponding parse failure path).
