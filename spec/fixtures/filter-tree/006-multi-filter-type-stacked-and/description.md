# 006 — within-column AND via the `multi` filter type's `filterModels[]` stack

**Proves:** AR-5's `And(nodes[])` composition from AG Grid's third within-column combination mechanism — `filterType: "multi"` stacks several independent filter UIs on one column and always combines them with AND (never OR; that choice belongs to the wrapped filter's own combined form, not to `multi` itself). Also exercises SEM-2: `notContains` is a negative binary operator, so its own leaf compiles with the mandatory `OR <col> IS NULL` — visible in the Postgres artifact's second leaf — independently of anything the tree-level `Not` node does elsewhere in this category.

**Requirement ids:** AR-5, AR-6, SEM-2

`customerName` carries `{filterType:"multi", filterModels:[{filterType:"text", type:"contains", filter:"acme"}, {filterType:"text", type:"notContains", filter:"test"}]}`. Single filtered column, so the compiled root is the `And` node directly.

**Merged filter tree (for reference, not a fixture file):**

```json
{
  "type": "and",
  "nodes": [
    {
      "type": "comparison",
      "field": "customerName",
      "op": "contains",
      "value": {
        "type": "string",
        "value": "acme"
      }
    },
    {
      "type": "comparison",
      "field": "customerName",
      "op": "notContains",
      "value": {
        "type": "string",
        "value": "test"
      }
    }
  ]
}
```
