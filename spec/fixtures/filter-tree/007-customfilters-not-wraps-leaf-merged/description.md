# 007 — `customFilters` NOT wraps a single leaf, merged with a native column filter

**Proves:** AR-5's `Not(node)` composition — AG Grid's own wire `filterModel` has no native negation concept (OQ-11's premise: the README's own wire-shape reference calls this out explicitly), so the only way to exercise `Not` at all is the `customFilters` extension. This is the shallowest case: NOT directly wraps a leaf comparison, merged as a sibling of one native `filterModel` column.

**Requirement ids:** AR-5, OQ-11

Native `filterModel` filters `status = "active"`. `customFilters` is `{type:"not", node:{type:"comparison", field:"region", op:"eq", value:{type:"string",value:"blocked"}}}`. Per the Custom Filter Merge component (`docs/architecture/c4-components-core-ir.md`), the host fragment is merged into the adapter-produced tree *before* allowlist/limit validation runs, indistinguishable from a normal filter from that point on — this fixture's merge rule (see `filter-tree/README.md`) is `And([nativeTree, customFragment])`.

**Merged filter tree (for reference, not a fixture file):**

```json
{
  "type": "and",
  "nodes": [
    {
      "type": "comparison",
      "field": "status",
      "op": "eq",
      "value": {
        "type": "string",
        "value": "active"
      }
    },
    {
      "type": "not",
      "node": {
        "type": "comparison",
        "field": "region",
        "op": "eq",
        "value": {
          "type": "string",
          "value": "blocked"
        }
      }
    }
  ]
}
```
