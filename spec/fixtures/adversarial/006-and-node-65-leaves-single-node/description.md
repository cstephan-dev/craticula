# 006 — a single `and` node with 65 leaf comparisons (one beyond the boundary of 64)

## What makes this adversarial
Via `customFilters`, the injected fragment is a single `and` node whose `nodes` array holds
**65** leaf comparison nodes (`quantity eq 0`, `quantity eq 1`, ... `quantity eq 64`).
`spec/ir.schema.json` deliberately places no `maxItems` on `andNode.nodes` (SEC-3's limit is
host-configurable, so no fixed structural bound belongs in the schema) — this fixture is exactly
the case that schema-level absence of a bound defers to the runtime validator.

SEC-3's default max leaf conditions is **64**. This case is one leaf beyond that boundary.

## Which validation stage must catch it
**Resource limits.** All 65 leaves reference the allowlisted `quantity` field with well-typed
`int64` values, so allowlist and coercion both pass cleanly; only the leaf-count budget check
rejects this request.

## Requirement ids
SEC-3 (max leaf conditions = 64, default), AR-5, OQ-11.
