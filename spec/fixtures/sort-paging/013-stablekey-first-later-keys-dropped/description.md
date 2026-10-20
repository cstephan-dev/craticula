# 013 — sort starts with the stable key: the keys after it are dropped

**Proves:** SEM-6's reduction "everything after the first occurrence of the stable key is dropped". The request sorts by the stable key `id` descending and then by `age`; `id` is unique, so `age` can never break a tie and the effective sort is `id desc` alone — nothing is appended either. A compiler that only checks whether the sort *ends* in the stable key compiled `ORDER BY id DESC, age ASC, id ASC`, which SQL Server rejects (error 169, a column named twice) and which a MongoDB `$sort` document cannot even hold. The SQL Server artifact is carried here because that is the arm that failed; the rows cannot tell keeping `age` from dropping it (`id` is unique, so `age` never decides anything); only the artifacts and the SQL Server and MongoDB runs discriminate, and the dataset's ages run against its ids so the expected order is visibly the `id` order.

**Requirement ids:** SEM-6, SEM-3, AR-9.

**Resolved IR sort array (for reference, not a fixture file):**
```json
[
  {
    "field": "id",
    "direction": "desc",
    "nullsPosition": "first"
  }
]
```
