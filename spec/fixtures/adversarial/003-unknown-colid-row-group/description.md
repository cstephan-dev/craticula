# 003 — unknown colId in `rowGroupCols`

## What makes this adversarial
`rowGroupCols` includes a `ColumnVO` whose `id`/`field` is `"regionHidden"`, a colId
absent from `schema.json`'s `fields`. Grouping is exactly the kind of aggregate-disclosure
surface the allowlist exists to close off, per SC-1/AR-8.

## Which validation stage must catch it
**Allowlist.** Same check as 001/002, applied to the group spec's column list before any
group-by compilation is attempted — a group column that was never allowlisted must never reach
the aggregation/group-key coercion logic.

## Requirement ids
SC-1, SEC-1, AR-8 (group spec) gated by the allowlist.
