# 007 — 70 leaves spread across a nested tree (tree-wide budget, not a per-node arity bound)

## What makes this adversarial
`spec/ir.schema.json`'s `andNode`/`orNode` description is explicit: "SEC-3's max-leaves
limit is a tree-wide budget enforced by the runtime validator, not a per-node arity bound — a
fixed maxItems on this array would neither express nor enforce that budget correctly." This case
proves that framing: the `customFilters` fragment is an `or` of two `and` nodes, each
holding only **35** leaves (well under any plausible per-node cap) — but **70** leaves
tree-wide, past the SEC-3 default of 64. A validator that only checked arity per `and`/`or`
node (35 <= 64) would wrongly accept this request; only a whole-tree leaf count catches it.

## Which validation stage must catch it
**Resource limits**, counting leaves across the *entire* tree, not per node.

## Requirement ids
SEC-3 (max leaf conditions = 64, tree-wide), AR-5, `spec/ir.schema.json`'s andNode/orNode
"no maxItems" rationale.
