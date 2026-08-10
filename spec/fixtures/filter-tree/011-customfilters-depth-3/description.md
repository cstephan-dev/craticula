# 011 — deliberate nesting depth 3 via `customFilters` (no native filter)

**Proves:** SEC-3's configurable max filter tree depth (default 8) at a shallow, unremarkable depth (3) — establishing the depth-counting convention (root node counts as depth 1) this category's boundary fixtures (`012`, `013`) build on, well below the limit so there is nothing adversarial about it.

**Requirement ids:** AR-5, SEC-3, OQ-11

`customFilters` is `And(nodes:[Not(node: Comparison(region eq "blocked"))])` — depth 1 (the `And` root) -> depth 2 (`Not`) -> depth 3 (the leaf `Comparison`). No native `filterModel`, so the compiled root is this fragment directly. **Depth reached: 3.**

**Merged filter tree (for reference, not a fixture file):**

```json
{
  "type": "and",
  "nodes": [
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
