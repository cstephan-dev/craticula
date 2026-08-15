# 007 — Cross-backend parity showcase: SEM-1 case-insensitive `contains`

**Proves:** the same rule as `006`, on `contains` this time, which per SEM-7 carries its own,
substring-specific mechanism per backend (so this case exercises SEM-1 and SEM-7 together, exactly
as `spec/semantics.md`'s own SEM-1 worked example does — "`contains` on `name`, case-insensitive,
matching `'Bob Smith'` against pattern `'smith'`", reproduced here field-for-field as
`lastName`/`"smith"`):

- **Postgres**: `ILIKE '%' || :pattern || '%'` — a `pg_trgm`-index-eligible pattern (`005`), with the
  mandatory `ESCAPE` clause (SEM-7).
- **SQL Server**: the same inline `COLLATE Latin1_General_CI_AS` as `006`'s `eq` case, now around a
  `LIKE` pattern with its own mandatory `ESCAPE` clause.
- **MySQL**: no `COLLATE` clause (relies on the column's own `_ci` collation, as in `006`), `LIKE`
  wrapped in `CONCAT` for the wildcarded pattern, with its own mandatory `ESCAPE` clause.
- **SQLite**: already-ASCII-CI `LIKE` (`004`), no `COLLATE NOCASE` needed, still with the mandatory
  `ESCAPE` clause.
- **MongoDB**: `contains` has **no** collation-compatible substring operator (SEM-1's own text) —
  compiles to `$regex` with the `"i"` option instead of the `006`-style collation wrapper. The
  literal `"smith"` contains no regex metacharacters, so SEM-7's full-metacharacter escaping is a
  no-op here (see `020` for a case where it visibly isn't).
- **Search**: a `wildcard` query (not `term`) against the `.keyword` sub-field, `case_insensitive:
  true`, pattern `*smith*`.

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6 exactly as in `006`.

**Coverage tier:** cross-backend parity showcase — all five SQL dialects + Mongo + Search.

**Requirement ids:** SEM-1, SEM-7, SEM-6 (tiebreaker, incidental), AR-6.
