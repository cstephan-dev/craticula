# 003 — single-key sort, date field, asc, adapter-pinned `nullsPosition: "last"`

**Proves:** SEM-3 on a `date`-typed field — same adapter default (`asc` → `"last"`) applies
regardless of field type, including date/datetime fields where "null" means a genuinely missing
date, not an epoch value. Also SEM-6: `hireDate` alone doesn't end in the declared `stableKey`
(`id`), so the compiler appends it with its own `nullsPosition: "last"`.

**Requirement ids:** SEM-3, SEM-6, AR-9, AG-3.

**Resolved IR sort array (for reference, not a fixture file):**
```json
[
  { "field": "hireDate", "direction": "asc", "nullsPosition": "last" },
  { "field": "id", "direction": "asc", "nullsPosition": "last" }
]
```

Field type under test for the primary sort key: `date`.
