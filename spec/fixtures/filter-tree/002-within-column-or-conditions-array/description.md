# 002 — within-column OR, current `conditions[]` form

**Proves:** AR-5's `Or(nodes[])` composition from a single column's *current*-form combined filter model (`ICombinedSimpleModel` with a `conditions` array, per `iSimpleFilter.ts`) — two conditions on the same column, `operator: "OR"`.

**Requirement ids:** AR-5, AR-6

`region` carries `{filterType:"text", operator:"OR", conditions:[{type:"equals", filter:"west"}, {type:"equals", filter:"east"}]}`. This is the only filtered column, so the compiled filter tree's root is the `Or` node directly — AG Grid's implicit multi-column AND never applies (there is nothing else to AND it with), and the compiler must not introduce a redundant single-child `And` wrapper around it. Compare `003-within-column-or-legacy-condition1-condition2`: same logical filter, legacy wire encoding, byte-identical resulting `Or` node (only field names/values are shared, structure is identical).

**Merged filter tree (for reference, not a fixture file):**

```json
{
  "type": "or",
  "nodes": [
    {
      "type": "comparison",
      "field": "region",
      "op": "eq",
      "value": {
        "type": "string",
        "value": "west"
      }
    },
    {
      "type": "comparison",
      "field": "region",
      "op": "eq",
      "value": {
        "type": "string",
        "value": "east"
      }
    }
  ]
}
```
