# 016 — the stable key named twice: the later occurrence is dropped

**Proves:** both reductions at once on the same field: `id asc` is the first occurrence and reaches the stable key, so the later `id desc` is dropped and nothing is appended. The effective sort is `id asc`.

**Requirement ids:** SEM-6, SEM-3, AR-9.

**Resolved IR sort array (for reference, not a fixture file):**
```json
[
  {
    "field": "id",
    "direction": "asc",
    "nullsPosition": "last"
  }
]
```
