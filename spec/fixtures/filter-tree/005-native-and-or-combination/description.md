# 005 — AND and OR combined, entirely from native `filterModel` (no `customFilters`)

**Proves:** AR-5's `And`/`Or` composed *together* in one tree straight from AG Grid's own wire format: one column's within-column OR (current `conditions[]` form) sits alongside two other columns' simple filters, all three ANDed implicitly across columns. Complements `001`/`002`/`003`/`004`, which each isolate AND or OR alone.

**Requirement ids:** AR-5, AR-6

`region` carries the same OR-of-two-equals as `002`; `status` and `quantity` each carry an independent simple filter. The compiled tree is a top-level `And` whose first child is itself an `Or` node — proving the adapter nests rather than flattens when a within-column combined filter meets AG Grid's own cross-column AND.

**Merged filter tree (for reference, not a fixture file):**

```json
{
  "type": "and",
  "nodes": [
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
    },
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
    }
  ]
}
```
