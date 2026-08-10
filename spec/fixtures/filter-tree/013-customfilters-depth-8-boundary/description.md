# 013 — deliberate nesting depth 8 via `customFilters` — the SEC-3 default max-depth boundary, ACCEPTED

**Proves:** SEC-3's configurable max filter tree depth, **default 8**, exercised at *exactly* that boundary. This is deliberately not an adversarial/rejection case: depth 8 is the limit itself, so a depth-8 tree must be **accepted**, not rejected — a depth-9 tree that exceeds the limit belongs to the `adversarial/` category (depth-bomb rejection), out of scope here. Only `customFilters` can reach real depth at all (AG Grid's own `filterModel` is structurally shallow — see this category's README).

**Requirement ids:** AR-5, SEC-3, OQ-11

`customFilters` chain, root to leaf, alternating `Not`/`Or`/`And`: `Not` (depth 1) -> `Or` (depth 2) -> `And` (depth 3) -> `Not` (depth 4) -> `Or` (depth 5) -> `And` (depth 6) -> `Not` (depth 7) -> `Comparison(region eq "blocked")` (depth 8). No native `filterModel`, so the compiled root is this fragment directly. **Depth reached: 8 (the SEC-3 default max, ACCEPTED).**

**Merged filter tree (for reference, not a fixture file):**

```json
{
  "type": "not",
  "node": {
    "type": "or",
    "nodes": [
      {
        "type": "and",
        "nodes": [
          {
            "type": "not",
            "node": {
              "type": "or",
              "nodes": [
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
              ]
            }
          }
        ]
      }
    ]
  }
}
```
