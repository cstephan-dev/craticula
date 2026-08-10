# 009 — stableKey tiebreaker appended, boolean primary sort key, desc

**Proves:** SEM-6's tiebreaker mechanic combined with SEM-3's `desc` → `"first"` default on the
primary key, and a `boolean` field type — a two-valued field is the most extreme case of a
non-unique sort key (at most two distinct groups: `true`/`false`), making the tiebreaker's job
(ordering the many ties within each group deterministically) maximally visible. The appended
`id` tiebreaker still gets its own independent default (`direction: "asc"`,
`nullsPosition: "last"`) regardless of the primary key's `desc`/`"first"` choice.

**Requirement ids:** SEM-6, SEM-3, AR-9, AG-3.

**Resolved IR sort array (for reference, not a fixture file):**
```json
[
  { "field": "isActive", "direction": "desc", "nullsPosition": "first" },
  { "field": "id", "direction": "asc", "nullsPosition": "last" }
]
```

Field type under test for the primary sort key: `boolean`.
