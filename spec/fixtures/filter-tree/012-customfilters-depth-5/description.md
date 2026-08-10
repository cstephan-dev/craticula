# 012 — deliberate nesting depth 5 via `customFilters` (no native filter)

**Proves:** SEC-3's configurable max filter tree depth (default 8) at a mid-range depth (5), alternating Not/And/Or down a single-child chain to the leaf — still well below the limit.

**Requirement ids:** AR-5, SEC-3, OQ-11

`customFilters` chain, root to leaf: `Not` (depth 1) -> `And` (depth 2) -> `Or` (depth 3) -> `Not` (depth 4) -> `Comparison(status eq "archived")` (depth 5). No native `filterModel`, so the compiled root is this fragment directly. **Depth reached: 5.**

**Merged filter tree (for reference, not a fixture file):**

```json
{
  "type": "not",
  "node": {
    "type": "and",
    "nodes": [
      {
        "type": "or",
        "nodes": [
          {
            "type": "not",
            "node": {
              "type": "comparison",
              "field": "status",
              "op": "eq",
              "value": {
                "type": "string",
                "value": "archived"
              }
            }
          }
        ]
      }
    ]
  }
}
```
