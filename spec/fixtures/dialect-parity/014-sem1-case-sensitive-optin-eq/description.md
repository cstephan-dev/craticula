# 014 — SEM-1 per-field case-**sensitive** opt-in, `eq`

**Proves:** SEM-1's SC-3 per-field override in the opposite direction from `006`: when a field's
`text.caseSensitivity` is explicitly `"sensitive"`, **none** of the CI mechanisms from `006` apply —
no `ILIKE`, no CI collation, no Mongo `collation` option, no ES `case_insensitive` parameter.

**But "no CI mechanism" is not the same as "nothing at all", and the difference is per engine.** The
compiler must actually *achieve* case-sensitive comparison, not merely decline to fold case: an
engine whose own default already distinguishes case needs no clause, and an engine whose default
does **not** must spell one, or the declaration is silently ignored. The four SQL arms therefore do
not agree, which is the whole point of promoting this case to parity coverage:

| Dialect | Renders | Because its default is |
|---|---|---|
| Postgres | nothing | deterministic / case-sensitive |
| SQLite | nothing | `BINARY` (`004`) |
| SQL Server | `COLLATE Latin1_General_CS_AS` | **case-INsensitive** (`SQL_Latin1_General_CP1_CI_AS` ships as the stock server collation) |
| MySQL | nothing — **host DDL obligation** | **case-INsensitive** (`utf8mb4_0900_ai_ci` family), and no portable case-sensitive collation name exists to emit |

This is the deliberate per-field override SEM-1's second Must-Not protects: a compiler must NOT
default to case-sensitive "because the engine's own default happens to be case-sensitive" — but
case-sensitive comparison IS correct once a field is explicitly configured that way (SC-1's
allowlist-and-config model puts that decision in `schema.json`, never inferred implicitly).

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6.

**Coverage tier:** all six backends — all four SQL dialects + MongoDB + Search. Promoted from
Postgres-only in #1639 (D2), and the first fixture in the suite to reach the **case-sensitive**
scalar path at all.

**#1639's review pass changed this fixture's `mssql` arm**, and the reason is worth recording: the
original four arms were all bare, on the reading that "case-sensitive" means "emit no CI mechanism".
That reading is wrong on SQL Server, whose stock collation folds case — a bare `=` there returns
exactly the rows the host's `caseSensitivity: "sensitive"` declaration was written to exclude, in
SQL that reads as though it were correct. It is SEM-1's second Must-Not with the sign flipped, and
the same class of silent failure **D3** pays an operator switch to prevent on SQLite. The compiler
seam could not express it until then: it carried a single boolean, which spells "text, compared
case-sensitively" and "not text at all" identically — and SQL Server rejects a `COLLATE` clause on
an `int` column, so the bare branch could not simply start emitting one. See the category README's
**D4**.

**MySQL's arm stays bare, and that is a deferral rather than an answer.** Its columns carry a `_ci`
collation by default, so honouring this schema requires a `_bin`/`_as_cs` **column** collation (host
DDL, PERF-4) — no inline `COLLATE` is emitted because MySQL's case-sensitive collation names are
charset-specific rather than one portable spelling, and inventing one would put an unverifiable
token into the oracle. The statement itself says nothing either way; the execution-equality tier
(T-1b, #1624) is what will pin the behaviour.

**Requirement ids:** SEM-1, SC-3, SEM-6 (tiebreaker, incidental).
