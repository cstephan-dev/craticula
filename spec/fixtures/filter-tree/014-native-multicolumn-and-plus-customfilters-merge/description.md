# 014 — native multi-column And (3 columns) merged with a customFilters fragment

**Proves:** The full merge order end to end (`docs/architecture/c4-components-core-ir.md`'s Custom Filter Merge component): three native `filterModel` columns compile to their own `And` subtree exactly as in `001`, a `customFilters` fragment is merged in on top, and the *combined* tree — not either half alone — is what allowlist/limit validation would check as one unit.

**Requirement ids:** AR-5, OQ-11

Native `filterModel` filters `status = "active"`, `quantity > 10`, `region = "west"` (own `And` subtree, mirroring `001`). `customFilters` is `{type:"not", node:{field:"customerName",op:"contains",value:"test"}}`. Merged root is `And([nativeAndSubtree, customFragment])` — the native subtree is nested as a whole child, not flattened into the merge's own `And`, since the merge component treats the native tree as an opaque fragment regardless of its own shape.

**Merged filter tree (for reference, not a fixture file):**

```json
{
  "type": "and",
  "nodes": [
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
          "type": "comparison",
          "field": "quantity",
          "op": "gt",
          "value": {
            "type": "int64",
            "value": 10
          }
        },
        {
          "type": "comparison",
          "field": "region",
          "op": "eq",
          "value": {
            "type": "string",
            "value": "west"
          }
        }
      ]
    },
    {
      "type": "not",
      "node": {
        "type": "comparison",
        "field": "customerName",
        "op": "contains",
        "value": {
          "type": "string",
          "value": "test"
        }
      }
    }
  ]
}
```
