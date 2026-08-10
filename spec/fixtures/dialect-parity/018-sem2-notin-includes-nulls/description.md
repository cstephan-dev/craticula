# 018 — SEM-2 null-inclusion on `notIn`

**Proves:** SEM-2's null-inclusion rule applied to `notIn` (`$defs/setOperator`), the operator
explicitly named in SEM-2's own Cross-links section alongside `neq`/`notContains`. `department` is
configured case-**sensitive** (SC-3 opt-in, as in `014`) so this fixture stays orthogonal to SEM-1 —
mixing case-folding into a null-inclusion fixture would obscure which rule is under test (mirroring
`008`'s reasoning for choosing a numeric field there; here the field is naturally a string, so the
opt-in achieves the same isolation).

AG Grid's native `set` filter (`iSetFilter.ts`) carries only an inclusion list (`values`) — there is
no wire-level invert/exclude flag, so no native `filterModel` shape can express `notIn` (the same
structural gap `operators/036-notin-string` documents). This fixture reaches `notIn` via the
`customFilters` (OQ-11) wire extension instead, with `filterModel: null`.

- **Postgres/SQL Server/MySQL/SQLite** (Postgres shown; the other three dialects render
  identically in shape): a bare `NOT IN (...)` would silently drop every NULL row under
  three-valued logic — the explicit `OR department IS NULL` is mandatory.
- **MongoDB**: `$nin` already matches missing/null-valued documents on its own, mirroring `$ne`'s
  behavior (`008`'s own note) — the compiler nonetheless emits the explicit `$or`-composed form for
  the same two reasons SEM-2 gives for `$ne`: parallel compiled shape across backends, and
  correctness inside a future `$expr` context.
- **Search**: a `bool`/`should` negating a plural `terms` query (department's set values) composed
  with the negated-`exists` null-inclusion clause.

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6.

**Coverage tier:** general/semantic fixture — Postgres + MongoDB + Search.

**Requirement ids:** SEM-2, SEM-6 (tiebreaker, incidental), SC-3 (case-sensitive opt-in, orthogonal
reinforcement), OQ-11 (`customFilters` usage).
