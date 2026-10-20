# 015 — a field named twice: the later occurrence is dropped, then the tiebreaker is appended

**Proves:** SEM-6's reduction "a later occurrence of a field the sort already named is dropped". The request names `age` twice (`desc`, then `asc`); the first occurrence decides every tie the second could break, so the effective sort is `age desc` and, since the stable key was not reached, the tiebreaker `id asc` is appended as usual. The request is not rejected for naming a field twice.

**Requirement ids:** SEM-6, SEM-3, AR-9.

**Resolved IR sort array (for reference, not a fixture file):**
```json
[
  {
    "field": "age",
    "direction": "desc",
    "nullsPosition": "first"
  },
  {
    "field": "id",
    "direction": "asc",
    "nullsPosition": "last"
  }
]
```
