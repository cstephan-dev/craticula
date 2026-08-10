# 009 — a `customFilters` `notIn` comparison with 5000 values (deep bomb)

## What makes this adversarial
Where 008 pins the precise off-by-one via AG Grid's native `set` filter, this case uses
`customFilters` to inject a raw IR `notIn` comparison node directly, with **5000** typed
`int64` values against `quantity` — far beyond SEC-3's default max of 1000, and exercising
the `notIn` half of the `setOperator` enum rather than only `in`.

## Which validation stage must catch it
**Resource limits.** All 5000 values are well-typed `int64` typedValues and `quantity` is
allowlisted, so only the in-values budget check can reject this request — a large payload built
specifically to be otherwise unremarkable.

## Requirement ids
SEC-3 (max in-values = 1000, default), AR-6 (`notIn`), SEC-6 (oversized-limit adversarial
cases), OQ-11 (customFilters).
