# 008 — Cross-backend parity showcase: SEM-2 `neq` includes NULLs

**Proves:** SEM-2's rule that negative binary operators MUST include NULL-valued rows in their match
set, identically across every backend, mirroring `spec/semantics.md`'s own SEM-2 worked example
structurally (`neq: "Alice"` against a field with some NULL rows → `col <> :v OR col IS NULL`).

This fixture deliberately filters on `age` (`int64`), not a string field: SEM-2's null-inclusion
rule and SEM-1's case-insensitivity default are two independent, orthogonal rules, and mixing them
in one fixture (a case-insensitive `neq` on a string field) would obscure which rule is under test.
Choosing a numeric field sidesteps the interaction entirely — see `017` for a fixture that
deliberately combines SEM-1+SEM-2+SEM-7 on `notContains` instead.

- **Postgres/SQL Server/MySQL/SQLite/Oracle**: a bare `<>` would silently drop every NULL row under
  three-valued logic (SEM-2's core rationale) — the explicit `OR age IS NULL` is mandatory on all
  five dialects.
- **MongoDB**: `$ne` alone already matches missing/null-valued documents (MongoDB's own semantics).
  The compiler nonetheless emits the explicit `$or`-composed form, per SEM-2's own stated reasons:
  parallel compiled shape across backends, and correctness inside a future `$expr` context. This
  fixture's `mongo.artifact.json` deliberately does **not** add a defensive
  `{ age: { $ne: null } }` guard "for parity with SQL" — SEM-2's second Must-Not names that
  exact anti-pattern as a Mongo-specific regression (it would *exclude* null rows, the opposite of
  this rule).
- **Search**: a `bool`/`should` composing a negated `term` with an explicit negated-`exists` clause,
  reproduced field-for-field from SEM-2's own worked example.

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6.

**Coverage tier:** cross-backend parity showcase — all five SQL dialects + Mongo + Search.

**Requirement ids:** SEM-2, SEM-6 (tiebreaker, incidental), AR-6.
