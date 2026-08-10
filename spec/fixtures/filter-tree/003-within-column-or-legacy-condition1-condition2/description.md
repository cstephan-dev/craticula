# 003 — within-column OR, legacy `condition1`/`condition2` form

**Proves:** AR-5's `Or(nodes[])` composition from a single column's *legacy* (pre-v29) combined filter model — two named slots (`condition1`/`condition2`) instead of the current form's array, per AG-1's requirement that the adapter still parse this shape. Deliberately mirrors `002-within-column-or-conditions-array` field-for-field and value-for-value so the two fixtures prove the *same* IR shape comes out of *both* wire encodings — a compiler that special-cases one form and produces a structurally different tree for the other has a bug.

**Requirement ids:** AR-5, AR-6, AG-1

`region` carries `{filterType:"text", operator:"OR", condition1:{type:"equals", filter:"west"}, condition2:{type:"equals", filter:"east"}}` — same column, same two values, same operator, just the legacy two-named-slots shape instead of an array. The resulting `Or` node below is identical to `002`'s.

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

**Cross-check:** this fixture's merged filter tree is structurally and value-for-value identical to `002-within-column-or-conditions-array`'s — confirming both wire forms compile to the same IR.
