# 004 — single-key sort, string field, desc, adapter-pinned `nullsPosition: "first"`

**Proves:** SEM-3's other half of the pinned adapter default: `sort: "desc"` in `sortModel` maps to
`nullsPosition: "first"`. Same absence of signal from AG Grid as the asc cases — `sortModel` is
still just `{ colId, sort }`. Also SEM-6: `lastName` alone doesn't end in the declared `stableKey`
(`id`), so the tiebreaker is appended — always `direction: "asc"`, `nullsPosition: "last"`,
regardless of the primary key's own direction/nulls choice.

**Requirement ids:** SEM-3, SEM-6, AR-9, AG-3.

**Resolved IR sort array (for reference, not a fixture file):**
```json
[
  { "field": "lastName", "direction": "desc", "nullsPosition": "first" },
  { "field": "id", "direction": "asc", "nullsPosition": "last" }
]
```

Field type under test for the primary sort key: `string`.
