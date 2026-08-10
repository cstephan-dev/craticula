# 014 — boolean-like values against an `int64` field

## What makes this adversarial
`quantity` is declared `type: "int64"`. The request sends a `set` filter with values
`["true", "false"]` — the exact string vocabulary `spec/fixtures/README.md`'s wire-shape note
prescribes for modeling AG Grid's absent native boolean filter (`values: ["true"]` /
`["false"]` / `["true","false"]`). Sent against a *boolean*-typed field this would coerce
cleanly; sent against `quantity` (`int64`), "true"/"false" have no numeric reading and
coercion genuinely cannot succeed.

## Which validation stage must catch it
**Type coercion.** `quantity` is allowlisted and a 2-value set filter is far under every
SEC-3 limit (both max in-values and max leaves); only the per-value coercion into `int64`
fails.

## Requirement ids
SEM-5 (type coercion, Strict mode), AR-7 (typed IR values — boolean and int64 are distinct
declared types, never inferred from wire strings), AG-1 (boolean-as-set-filter modeling note).
