# 017 — a case-insensitive text stable key: the keys after it are kept, the exact tiebreaker is appended

**Proves:** the exception to SEM-6's rule 2 (Bug #2565). The stable key `code` is a text field with no `text` setting, so under SEM-1 the user's own key on it compares **case-insensitively**: `'aB3'` and `'Ab3'` tie under it, the sort is not total, and dropping `age` after it would leave exactly the ties the exact tiebreaker exists to break. So nothing after the key is dropped and the exact tiebreaker is appended as before, on the field's exact sub-field (`code.keyword` on Search, no collation on Postgres). The resulting `ORDER BY` names `code` twice, once collated and once exact (Bug #2809 gave the two arms that cannot list a column twice their own spelling, below).

**How each arm spells the repeated key (Bug #2809, SEM-6):** SQL Server refuses an `ORDER BY` expression named twice (error 169), so its artifact renders the tiebreaker case-sensitively (`[code] COLLATE Latin1_General_CS_AS`) **and** puts that collation inside the tiebreaker's null-bucket `CASE`; MongoDB cannot hold one path twice in a `$sort` (MDB-10), so its artifact sorts the tiebreaker on a synthetic `__codeExactKey` field holding `{$toHashedIndexKey: "$code"}`. Postgres and Search are as before. The execution-equality tier runs the shape on all of them (PostgreSQL, SQL Server, MongoDB, OpenSearch, Elasticsearch). The dataset puts the two case-variants of one code on different ages, so the user's second key decides their order identically on every engine; which of two case variants that tie on every earlier key comes first is not promised and no row here depends on it.

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
