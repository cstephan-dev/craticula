# 008 — a `set` filter with 1001 values (one beyond the boundary of 1000)

## What makes this adversarial
No `customFilters` needed here: AG Grid's own `set` filter type (`iSetFilter.ts`) already
carries a plain `values: string[]`, which the adapter maps straight to an `in` IR comparison
(per `spec/fixtures/README.md`'s wire-shape note). This case sends **1001** distinct values for
the `name` column's set filter — one past SEC-3's default max `in`-values of **1000**.

## Which validation stage must catch it
**Resource limits.** `name` is allowlisted and every value is a well-typed string, so this
request must clear allowlist and (later) coercion cleanly; only the in-values count check
rejects it.

## Requirement ids
SEC-3 (max in-values = 1000, default), AR-6 (`in`/`notIn` operators), `spec/ir.schema.json`'s
`setComparisonNode` ("no maxItems here... the runtime validator is the sole enforcement
point").
