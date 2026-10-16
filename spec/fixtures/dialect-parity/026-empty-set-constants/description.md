# 026 — the empty-set constants, spelled once for every dialect

**Proves:** SEM-8. `(age IN [] OR age NOT IN [])` is a constant false OR a constant true, so every row matches. No SQL dialect has a portable boolean literal in predicate position (SQL Server and Oracle have none), so each renders `1 = 0` and `1 = 1`; none emits `IN ()`, which is a syntax error, or binds an empty array. Mongo renders `$in: []` and `$nin: []`, Search `match_none` and `match_all`. The compilers do not fold the constants away.
