# 024 — `filterable:false` colId referenced by `filterModel`

## What makes this adversarial
`filterModel` filters on `costBasis`, a colId that IS present in `schema.json`'s `fields` but
declared `filterable: false` (SC-2). Unlike 001-003 (a colId absent from the allowlist entirely),
this colId exists and is otherwise perfectly usable (sortable, aggregatable elsewhere) — the host
has deliberately excluded it from the filter surface, and the allowlist must honour that exclusion
rather than treating "present in the schema" as "usable for anything."

## Which validation stage must catch it
**Allowlist.** `FieldAllowlistValidator.Validate` (#1872) checks `FieldConfig.Filterable` for
every colId a filter condition references, not just colId membership — a known-but-non-filterable
colId is treated exactly like an unknown one for Strict/Lenient purposes (SC-1's Lenient may still
silently strip it in Lenient mode, since dropping a condition only widens the result).

## Requirement ids
SC-1, SC-2, SEC-1 (allowlist + capability flag).
