# 010 — `customFilters` NOT with no native `filterModel` at all — merge degenerates to the fragment itself

**Proves:** The Custom Filter Merge component's degenerate case: when the request carries no native filter (`filterModel: null`), the merged tree is the `customFilters` fragment itself, unwrapped — the merge must not introduce a spurious single-child `And` just because the merge *mechanism* generally produces an `And`. Confirms `Not` reaches the compiler as a first-class root node, not only as an And-child.

**Requirement ids:** AR-5, OQ-11

`filterModel` is `null`. `customFilters` is `{type:"not", node:{type:"comparison", field:"status", op:"eq", value:{type:"string",value:"archived"}}}`. There is no native tree to AND it with, so the compiled root is the bare `Not` node.

**Merged filter tree (for reference, not a fixture file):**

```json
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
```
