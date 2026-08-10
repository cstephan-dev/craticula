# 012 — non-numeric string against an `int64` field

## What makes this adversarial
`quantity` is declared `type: "int64"` in `schema.json`. The number filter's `filter`
value is sent as the string `"not-a-number"` — AG Grid's own number filter is normally
numeric, but the wire payload is JSON text the server must still parse defensively; SEM-5
requires the adapter to *attempt* coercion (the way `"30"` legitimately coerces to `30`) and
to reject when coercion genuinely cannot succeed, rather than silently matching every row.

## Which validation stage must catch it
**Type coercion**, the third stage in the pinned order (allowlist -> limits -> coercion ->
static injection). `quantity` is allowlisted (passes allowlist) and this is a single condition
well within every SEC-3 limit (passes resource limits); only the coercion attempt against the
field's declared `int64` type fails.

## Requirement ids
SEM-5 (type coercion; Strict mode: uncoercible values are a request error, never a silent
full-table match), AR-7 (typed IR values).
