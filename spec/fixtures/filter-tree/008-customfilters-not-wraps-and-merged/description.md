# 008 — `customFilters` NOT wraps an And of two leaves, merged with a native column filter

**Proves:** AR-5's `Not(node)` composition one nesting level deeper than `007`: NOT wraps a two-leaf `And` subtree rather than a bare leaf, still injected via `customFilters` (OQ-11) and merged with a native column filter.

**Requirement ids:** AR-5, OQ-11

Native `filterModel` filters `status = "active"`. `customFilters` is `{type:"not", node:{type:"and", nodes:[{field:"region",op:"eq",value:"blocked"}, {field:"quantity",op:"lt",value:0}]}}`. Merge rule identical to `007`: `And([nativeTree, customFragment])`.

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
        "type": "and",
        "nodes": [
          {
            "type": "comparison",
            "field": "region",
            "op": "eq",
            "value": {
              "type": "string",
              "value": "blocked"
            }
          },
          {
            "type": "comparison",
            "field": "quantity",
            "op": "lt",
            "value": {
              "type": "int64",
              "value": 0
            }
          }
        ]
      }
    }
  ]
}
```
