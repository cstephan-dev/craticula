# 011 — page size 1,000,000 (deep bomb)

## What makes this adversarial
`startRow: 0`, `endRow: 1000000` — a requested page size of one million rows, engineered to
force a backend to materialize (or attempt to materialize) an unbounded result set in one round
trip, the resource-exhaustion framing SEC-6 names for depth/limit bombs generally.

## Which validation stage must catch it
**Resource limits**, same check as 010, at a scale that also proves the check runs before any
attempt to execute/stream the page (EXP-3's streaming requirement governs *accepted* exports;
this request must never get that far).

## Requirement ids
SEC-3 (max page size = 1000, default), AG-3, SEC-6 (oversized-limit adversarial cases).
