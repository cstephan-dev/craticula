# 005 — single-key sort, int64 field, desc, adapter-pinned `nullsPosition: "first"`

**Proves:** SEM-3's `desc` → `"first"` default on a numeric field, confirming the mapping is keyed
purely off `sortModel`'s `sort` direction, not off the field's declared type. Also SEM-6: `age`
alone doesn't end in the declared `stableKey` (`id`), so the tiebreaker is appended with its own
`nullsPosition: "last"`.

**Requirement ids:** SEM-3, SEM-6, AR-9, AG-3.

**Resolved IR sort array (for reference, not a fixture file):**
```json
[
  { "field": "age", "direction": "desc", "nullsPosition": "first" },
  { "field": "id", "direction": "asc", "nullsPosition": "last" }
]
```

Field type under test for the primary sort key: `int64`.
