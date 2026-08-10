# 007 — multi-key sort (3 keys), order preserved, each key's own `nullsPosition` default

**Proves:** the same AR-9/SEM-3 guarantees as 006 scaled to three `sortModel` entries with mixed
directions (`asc`, `desc`, `asc`) across three different field types, to confirm the per-key default
is computed independently for every key regardless of how many precede it or what type they are.
Also SEM-6: the tiebreaker (`id`) is appended as the fourth and final key.

**Requirement ids:** SEM-3, SEM-6, AR-9, AG-3.

**Resolved IR sort array (for reference, not a fixture file):**
```json
[
  { "field": "department", "direction": "asc", "nullsPosition": "last" },
  { "field": "age", "direction": "desc", "nullsPosition": "first" },
  { "field": "hireDate", "direction": "asc", "nullsPosition": "last" },
  { "field": "id", "direction": "asc", "nullsPosition": "last" }
]
```

Field types under test for the primary sort keys: `string` (department), `int64` (age), `date`
(hireDate).
