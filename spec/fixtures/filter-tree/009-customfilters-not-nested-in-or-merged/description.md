# 009 — `customFilters` NOT nested inside an Or, at a different tree position than `007`/`008`

**Proves:** AR-5's `Not(node)` composition at a third, distinct nesting position: rather than NOT sitting as a direct child of the top-level And (as in `007`/`008`), it is nested one level further down as a child of an `Or`, alongside a plain leaf sibling. Rounds out "different nesting positions" for NOT across this category.

**Requirement ids:** AR-5, OQ-11

Native `filterModel` filters `status = "active"`. `customFilters` is `{type:"or", nodes:[{type:"not", node:{field:"region",op:"eq",value:"blocked"}}, {field:"region",op:"eq",value:"archived"}]}` — i.e. "region is not blocked, or region is archived". Merge rule identical to `007`/`008`: `And([nativeTree, customFragment])`.

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
      "type": "or",
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
        },
        {
          "type": "comparison",
          "field": "region",
          "op": "eq",
          "value": {
            "type": "string",
            "value": "archived"
          }
        }
      ]
    }
  ]
}
```
