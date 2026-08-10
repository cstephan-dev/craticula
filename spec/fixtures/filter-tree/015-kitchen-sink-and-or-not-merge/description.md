# 015 — capstone: native And+Or combined with a customFilters Not(And) fragment, merged as one tree

**Proves:** Integrative case tying every piece of this category together in one fixture: a native within-column `Or` (`region`) ANDed with another native column (`status`), merged with a `customFilters` fragment that itself negates a two-leaf `And` (`quantity` range emulated as two leaves) — And, Or, and Not all present, from both the native wire format and the `customFilters` extension, combined by the same merge rule as every other merged fixture in this category.

**Requirement ids:** AR-5, AR-6, OQ-11

Native `filterModel`: `region` OR-of-two-equals (west/east, as in `002`/`005`) ANDed with `status = "active"`. `customFilters`: `Not(And(quantity >= 100, quantity <= 999))`. Merged root: `And([nativeTree, customFragment])`, reaching depth 4 (root And -> nativeTree And -> its Or child -> leaf; independently, root And -> customFragment Not -> its And child -> leaf).

**Merged filter tree (for reference, not a fixture file):**

```json
{
  "type": "and",
  "nodes": [
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
        }
      ]
    },
    {
      "type": "not",
      "node": {
        "type": "and",
        "nodes": [
          {
            "type": "comparison",
            "field": "quantity",
            "op": "gte",
            "value": {
              "type": "int64",
              "value": 100
            }
          },
          {
            "type": "comparison",
            "field": "quantity",
            "op": "lte",
            "value": {
              "type": "int64",
              "value": 999
            }
          }
        ]
      }
    }
  ]
}
```
