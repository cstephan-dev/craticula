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

- **Postgres**: `("department" <> ALL($1) OR "department" IS NULL)` — a bare set-negative would
  silently drop every NULL row under three-valued logic, so the explicit `OR "department" IS NULL`
  is mandatory, and the whole disjunction is parenthesised per `spec/semantics.md`'s "Canonical SQL
  rendering" section. The set itself is **one array parameter** (`<> ALL($1)`), not N scalar
  placeholders — Postgres's array form, chosen so the placeholder count does not depend on the
  value count. The other three dialects render identically *in shape* but not in text: neither SQL
  Server, MySQL nor SQLite has `ANY(array)`, so their arms (added in #1639) render `NOT IN (...)`
  with N scalar parameters behind the same dialect method — which is why this fixture's four SQL
  arms do not even agree on `params.length`.
- **MongoDB**: `$nin` already matches missing/null-valued documents on its own, mirroring `$ne`'s
  behavior (`008`'s own note) — the compiler nonetheless emits the explicit `$or`-composed form for
  the same two reasons SEM-2 gives for `$ne`: parallel compiled shape across backends, and
  correctness inside a future `$expr` context.
- **Search**: a `bool`/`should` negating a plural `terms` query (department's set values) composed
  with the negated-`exists` null-inclusion clause.

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6.

**Coverage tier:** all six backends — all four SQL dialects + MongoDB + Search. Promoted from
Postgres-only in #1639 (D2): the negated half of SQL-4's split (`<> ALL($1)` vs `NOT IN (?, ?)`) on
a case-**sensitive** field.

**#1639's review pass added `COLLATE Latin1_General_CS_AS` to the `mssql` arm.** SEM-1 requires one
mechanism per field across every context, so a declared-sensitive field carries its collation on a
set membership exactly as it does on an `eq` — and on SQL Server the case-sensitive direction needs
a spelled mechanism, because the stock server collation folds case (see `014`, and the category
README's **D4**). The collation sits on the **column**, left of `NOT IN`, the same position `016`'s
CI arm puts it: an in-list resolves its comparison semantics from the left operand. The other three
arms stay bare, for the three different reasons `014`'s table records.

**Requirement ids:** SEM-2, SEM-6 (tiebreaker, incidental), SC-3 (case-sensitive opt-in, orthogonal
reinforcement), OQ-11 (`customFilters` usage).
