# 001 — multi-column implicit AND (4 columns)

**Proves:** AR-5's `And(nodes[])` composition, and AG Grid's own implicit rule of ANDing together independent per-column filters set on different columns — the adapter never sees an explicit "AND these columns" instruction, it is simply the fact that `filterModel` has one entry per filtered column and every entry applies simultaneously.

**Requirement ids:** AR-5, AR-6

Four columns each carry a single, independent simple filter (`customerName contains "acme"`, `amount > 100`, `status = "active"`, and the boolean-filter-as-set shorthand `isActive = true` — per the README's boolean modeling note, a single-value `set` filter `{filterType:"set", values:["true"]}` on a `boolean`-typed field compiles to the `eq` comparison operator, not to `in`, since one value is not membership-testing). The compiled top-level node is a single `And` with exactly one leaf per column, in filterModel order.

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
      "field": "amount",
      "op": "gt",
      "value": {
        "type": "double",
        "value": 100
      }
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
      "field": "isActive",
      "op": "eq",
      "value": {
        "type": "boolean",
        "value": true
      }
    }
  ]
}
```

*Search-arm correction (#1644, the story that compiled this arm for the first time):* the `isActive`
clause was spelled `{"term": {"isActive": {"value": true}}}` and is now
`{"term": {"isActive": true}}`. It is the same construct `operators/044-true-boolean` pins from the
identical wire shape (a `set` filter carrying `["true"]`, which AR-6 resolves to the unary `true`
operator), and that fixture already used the short form — one IR construct rendered two ways from
identical inputs, which is the defect class the rendering addenda exist to end. **OSE-1** keys the
spelling on the field's declared type: the long form for a `string`, because only a string can ever
acquire SEM-1's `case_insensitive` parameter, and the short form for every other type. Thirteen
non-string `term` clauses in the suite already used it; this was the one that did not.
