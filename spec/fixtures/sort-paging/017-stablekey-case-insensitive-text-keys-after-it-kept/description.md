# 017 — a case-insensitive text stable key: the keys after it are kept, the exact tiebreaker is appended

**Proves:** the exception to SEM-6's rule 2 (Bug #2565). The stable key `code` is a text field with no `text` setting, so under SEM-1 the user's own key on it compares **case-insensitively**: `'aB3'` and `'Ab3'` tie under it, the sort is not total, and dropping `age` after it would leave exactly the ties the exact tiebreaker exists to break. So nothing after the key is dropped and the exact tiebreaker is appended as before, on the field's exact sub-field (`code.keyword` on Search, no collation on Postgres). The resulting `ORDER BY` names `code` twice, once collated and once exact.

**Known limits, unchanged by #2565:** SQL Server refuses a column named twice in one `ORDER BY` (error 169), and MongoDB refuses two `$sort` keys on one field (MDB-10), so this shape has Postgres and Search artifacts only, and the execution-equality tier runs it on those engines (PostgreSQL, OpenSearch, Elasticsearch). The dataset puts the two case-variants of one code on different ages, so the user's second key decides their order identically on every engine.

**Requirement ids:** SEM-6, SEM-1, SEM-3, AR-9.

**Resolved IR sort array (for reference, not a fixture file):**
```json
[
  { "field": "code", "direction": "asc", "nullsPosition": "last" },
  { "field": "age", "direction": "asc", "nullsPosition": "last" },
  { "field": "code", "direction": "asc", "nullsPosition": "last" }
]
```
The last entry is the appended tiebreaker (exact); the first is the user's own key (case-insensitive).
