# 002 — single-key sort, int64 field, asc, adapter-pinned `nullsPosition: "last"`

**Proves:** SEM-3 on a numeric field type: the adapter's direction→position default
(`asc` → `"last"`) does not depend on the field's declared type — `sortModel` gives the adapter no
signal regardless of what type the sorted column is. Also SEM-6: the sort (`age` only) doesn't end
in the declared `stableKey` (`id`), so the compiler appends it with its own `nullsPosition: "last"`.

**Requirement ids:** SEM-3, SEM-6, AR-9, AG-3.

**Resolved IR sort array (for reference, not a fixture file):**
```json
[
  { "field": "age", "direction": "asc", "nullsPosition": "last" },
  { "field": "id", "direction": "asc", "nullsPosition": "last" }
]
```

Field type under test for the primary sort key: `int64`.
