# 027 — zero-width page window

## What makes this adversarial
`startRow: 100`, `endRow: 100` — an empty window. `endRow` is exclusive, so the requested page size is 0.
Compiled, it would be `FETCH NEXT 0 ROWS` (rejected by SQL Server, error 10744), `LIMIT 0`, `$limit: 0` (an error
in MongoDB) or `size: 0`: five different outcomes for one request.

## Which validation stage must catch it
The request boundary (`requestParsing`, `malformed-request`), before any IR validation or compilation, identically
for every backend. The same rule refuses an inverted window (`endRow < startRow`).

## Requirement ids
AG-3, SEC-3, SEC-6 (see `semantics.md`, "The page window is non-empty").
