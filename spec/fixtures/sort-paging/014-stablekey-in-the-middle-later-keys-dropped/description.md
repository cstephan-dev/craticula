# 014 — the stable key sits in the middle of the sort: the keys after it are dropped

**Proves:** the same reduction as 013 when the stable key is not first. The key before it (`age`) is kept, `id desc` is kept as the user wrote it (direction and null placement are theirs, not the tiebreaker's), and `lastName`, which follows the stable key, is dropped; no tiebreaker is appended because the sort reached the stable key. The dataset ties on `age`, so the order inside a tie comes from `id desc`.

**Requirement ids:** SEM-6, SEM-3, AR-9.

**Resolved IR sort array (for reference, not a fixture file):**
```json
[
  {
    "field": "age",
    "direction": "asc",
    "nullsPosition": "last"
  },
  {
    "field": "id",
    "direction": "desc",
    "nullsPosition": "first"
  }
]
```
