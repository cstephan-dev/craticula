# 016 — `"regex"` filter type, outside the closed 17-operator set

## What makes this adversarial
The `name` text filter's `type` is sent as `"regex"`. AR-6 pins a **closed** set of 17
operators (`eq, neq, gt, gte, lt, lte, contains, notContains, startsWith, endsWith, blank,
notBlank, in, notIn, inRange, true, false`) — `"regex"` is not among them. This is not an
accident of naming: SEC-5 explicitly excludes regex-based operators from v1 entirely because of
their ReDoS surface, so a client requesting one must be rejected outright, not silently
downgraded to e.g. `contains`.

## Which validation stage must catch it
**Request parsing.** The `type` value never maps to any member of `spec/ir.schema.json`'s
`operator` enum, so this fails before an IR comparison node can even be constructed — it never
reaches allowlist/limits/coercion, which all assume a well-formed operator already.

## Requirement ids
AR-6 (closed 17-operator set), SEC-5 (regex operators excluded from v1), SEC-6 (adversarial
fixtures for out-of-set operators).
