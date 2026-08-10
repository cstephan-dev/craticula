# 004 — within-column AND, legacy `condition1`/`condition2` form, `operator: "AND"`

**Proves:** AR-5's `And(nodes[])` composition from a single column's combined filter model using `operator: "AND"` rather than `"OR"` — the legacy `condition1`/`condition2` combined form is not OR-only; the same two-slot shape carries either operator, and the adapter must read the `operator` field rather than assuming OR whenever it sees a legacy combined model.

**Requirement ids:** AR-5, AR-6, AG-1

`amount` carries `{filterType:"number", operator:"AND", condition1:{type:"greaterThan", filter:50}, condition2:{type:"lessThan", filter:500}}` (a range emulated via two ANDed simple conditions, rather than AG Grid's dedicated `inRange` type). Single filtered column, so the compiled root is the `And` node directly.

**Merged filter tree (for reference, not a fixture file):**

```json
{
  "type": "and",
  "nodes": [
    {
      "type": "comparison",
      "field": "amount",
      "op": "gt",
      "value": {
        "type": "double",
        "value": 50
      }
    },
    {
      "type": "comparison",
      "field": "amount",
      "op": "lt",
      "value": {
        "type": "double",
        "value": 500
      }
    }
  ]
}
```
