# 025 — `filterable:false` colId as `setValuesRequest.colId`

## What makes this adversarial
The BM-3 set-values request targets `costBasis`, a colId present in `schema.json`'s `fields` but
declared `filterable: false` (SC-2). A set filter's value list IS the filter UI's own data source,
so enumerating a column's distinct values is exactly the operation `filterable: false` exists to
forbid — and, unlike 024's ordinary filter condition, no `GridSchemaMode` may drop this rejection:
the colId here is the entire question, not a narrowing condition, so there is no narrower answer
left to give once it is dropped.

## Which validation stage must catch it
**Allowlist.** `FieldAllowlistValidator.ValidateStructuralColumns` (#1872) checks
`FieldConfig.Filterable` for `setValuesSpec.ColId`, mirroring the existing `GroupSpec.GroupCols`/
`Aggregation.Field` capability checks exactly — rejected in EVERY mode, never stripped.

## Requirement ids
SC-1, SC-2, SEC-1, GRP-6/BM-3 (set-values allowlist + capability flag).
