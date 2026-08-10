# 003 — SQL Server `OFFSET`/`FETCH NEXT` paging syntax, non-zero offset

**Proves:** BM-1's SQL Server-specific paging rendering (not itself a SEM rule): T-SQL has no
`LIMIT`/`OFFSET` keyword pair — its only native paging syntax is `OFFSET <n> ROWS FETCH NEXT <m> ROWS
ONLY`, and that clause is only legal with a preceding `ORDER BY`. The request pages at a non-zero
offset (`startRow: 20, endRow: 40` → page size 20), so the rendered `OFFSET`/`FETCH NEXT` values are
`20`/`20`, not `0`/`pageSize`.

The request's `sortModel` sorts by the stableKey itself (`id`), so SEM-6 appends no separate
tiebreaker — the sort already ends in `stableKey` (mirroring `sort-paging/010`'s negative case for
the same rule, reproduced here incidentally because `OFFSET`/`FETCH` needs an `ORDER BY` regardless).

Incidentally, this fixture also applies SEM-3's SQL Server-specific null-ordering emulation to that
one sort key, for rigor (`stableKey` is required to be unique, not `NOT NULL` — SEM-6's own text is
explicit that the compiler cannot skip specifying a null position for it just because a real
stableKey is unlikely to contain nulls in practice). T-SQL has no native `NULLS FIRST`/`NULLS LAST`
(unlike PostgreSQL/Oracle), **and** unlike MySQL's single-argument `ISNULL(col)` (a null-test
returning `1`/`0`, see `001`/`002`), T-SQL's own `ISNULL(expr, replacement)` is a **two-argument**
substitution/`COALESCE`-equivalent function — reusing that name for a boolean null test would be
wrong. SQL Server's compiler therefore emulates the prefix key with
`CASE WHEN col IS NULL THEN 1 ELSE 0 END` instead.

**Dialect-specific mechanic:** SQL Server only, per this suite's backend-coverage policy.

**Requirement ids:** AG-3 (paging syntax; the rule this case exists to pin), SEM-3, SEM-6
(both incidental).

**Resolved IR (for reference, not a fixture file):**
```json
{
  "sort": [ { "field": "id", "direction": "asc", "nullsPosition": "last" } ],
  "page": { "mode": "range", "startRow": 20, "endRow": 40 }
}
```
