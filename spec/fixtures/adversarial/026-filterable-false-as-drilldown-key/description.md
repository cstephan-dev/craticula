# 026 — `filterable:false` colId consumed as a `groupKeys` drill-down key

## What makes this adversarial

`region` is declared `groupable: true` (plain bucketing by region is allowed) but
`filterable: false`. The request does not send a `filterModel` condition on `region` at all — it
drills into one specific bucket via `rowGroupCols: [region]` + `groupKeys: ["West"]`, AG Grid's
normal protocol for "show me the next level down inside region=West".

`DrillDownFilter.Synthesize` turns that consumed key into a literal `region = 'West'` comparison
node, compiled into every backend's `WHERE`/`$match`/filter clause exactly like an ordinary
`filterModel` condition would be — the wire channel differs, the operation does not. A schema
declaring `filterable: false` on `region` is a host policy against a client narrowing results to one
specific value of that column; `groupKeys` is not an exemption from that policy, it is a second way
to express the same narrowing.

## Which validation stage must catch it

**Allowlist.** `FieldAllowlistValidator.ValidateStructuralColumns` (#1907) checks
`FieldConfig.Filterable` on the CONSUMED prefix of `group.GroupCols` — i.e. exactly the columns that
have a corresponding `groupKeys` entry and are therefore about to become a drill-down predicate.
An unconsumed `GroupCols` entry (grouping declared, no key chosen yet) is deliberately NOT checked:
bucketing across every value is what `groupable: true` alone already permits. Rejected in EVERY
`GridSchemaMode`, mirroring #1872's `setValuesSpec.colId` treatment: dropping a consumed key would
silently return a different bucket's rows mislabeled as the one the client asked for.

## Requirement ids

SC-1, SC-2, SEC-1, GRP-1/GRP-2/GRP-5 (drill-down consumes `groupKeys` in `GroupCols` order).
