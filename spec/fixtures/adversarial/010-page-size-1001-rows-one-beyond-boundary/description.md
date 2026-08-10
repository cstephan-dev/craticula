# 010 — page size 1001 (one beyond the boundary of 1000)

## What makes this adversarial
`startRow: 0`, `endRow: 1001` — a requested page size (`endRow - startRow`) of **1001**
rows, one beyond SEC-3's default max page size of **1000**. `spec/ir.schema.json`'s
`rangePage` description notes this cap is deliberately not expressible as a structural
`maximum` (host-configurable), so this is purely a runtime-validator concern.

## Which validation stage must catch it
**Resource limits.** Nothing else in the request is adversarial (no filter, no sort, no group),
isolating the page-size check as the sole cause of rejection.

## Requirement ids
SEC-3 (max page size = 1000, default), AG-3 (page spec), `spec/ir.schema.json`'s `rangePage`.
