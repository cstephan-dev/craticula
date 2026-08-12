# `dialect-parity/` — dialect-specific mechanics + cross-backend parity showcases (SEM-1/2/3/7)

Per `spec/fixtures/README.md`'s category index, this directory proves two distinct things, kept
structurally separate by coverage tier (see each case's own `description.md` for its tier and the
specific SEM rule/mechanism it pins):

1. **Dialect-specific mechanics** (`001`-`005`): a single backend's own native rendering of a
   semantics rule — MySQL's `ISNULL` null-ordering emulation, SQL Server's `OFFSET`/`FETCH NEXT`
   paging syntax, SQLite's default ASCII-case-insensitive `LIKE`, Postgres's `pg_trgm`-index-eligible
   `ILIKE`. One artifact per fixture, targeting only the dialect under test.
2. **Cross-backend parity showcases** (`006`-`010`): the same rule proved to reach an *identical
   logical outcome* on **every** backend — all four SQL dialects (Postgres/MSSQL/MySQL/SQLite) +
   MongoDB + Search — through each backend's own native mechanism. 5 fixture directories × 6
   backend artifacts = 30 artifact files, still only 5 fixture cases.

The remaining cases (`011`-`020`) are general/semantic fixtures per the suite's default coverage
policy (Postgres + MongoDB + Search): the SEM-7 `contains`-vs-tokenized-`match` trap (correct
artifact + anti-pattern documentation), the `searchMode: "analyzed"` escape hatch, and further
SEM-1/2/7 mechanics not otherwise exercised by `001`-`013`.

All cases model the same `employees` domain `sort-paging/` uses (`id` int64 stableKey, `lastName`
string, `age` int64, `salary` decimal, `department` string), plus a few fixture-local additions
(`employeeCode`, `jobTitle`, `remarks`) introduced only where a case's own mechanic needs a field
with a distinct configuration (case-sensitive opt-in, `searchMode: "analyzed"`) that would be
confusing to bolt onto a field reused elsewhere in this category.

## Cases

| # | Slug | Coverage tier | Proves | Requirement ids |
|---|---|---|---|---|
| 001 | `mysql-isnull-null-order-desc-numeric` | dialect-specific (MySQL) | `ISNULL` null-ordering emulation, `desc` sort, `decimal` field | SEM-3, SEM-6 |
| 002 | `mysql-isnull-null-order-desc-string` | dialect-specific (MySQL) | Same emulation on a `string` field (type-independence) | SEM-3, SEM-6 |
| 003 | `mssql-offset-fetch-paging` | dialect-specific (SQL Server) | `OFFSET`/`FETCH NEXT` paging syntax at a non-zero offset; incidental `CASE WHEN` null emulation | AG-3, SEM-3, SEM-6 |
| 004 | `sqlite-ascii-case-insensitive-like` | dialect-specific (SQLite) | Default ASCII-CI `LIKE`, no `COLLATE` needed (contrast with `eq`'s `COLLATE NOCASE` in `006`) | SEM-1, SEM-7 |
| 005 | `postgres-ilike-pg-trgm-contains` | dialect-specific (Postgres) | `ILIKE`, `pg_trgm`-index-eligible pattern shape | SEM-1, SEM-7, PERF-4 |
| 006 | `parity-sem1-case-insensitive-eq` | parity showcase (all 6) | `eq` case-insensitive by default, one native mechanism per backend | SEM-1, AR-6 |
| 007 | `parity-sem1-case-insensitive-contains` | parity showcase (all 6) | `contains` case-insensitive by default, SEM-7's substring mechanism per backend | SEM-1, SEM-7, AR-6 |
| 008 | `parity-sem2-neq-includes-nulls` | parity showcase (all 6) | `neq` includes NULL rows, one composition per backend | SEM-2, AR-6 |
| 009 | `parity-sem3-null-order-asc` | parity showcase (all 6) | `nullsPosition: "last"` (adapter default on `asc`), one mechanism per backend | SEM-3, SEM-6, AR-9, AG-3 |
| 010 | `parity-sem3-null-order-desc` | parity showcase (all 6) | `nullsPosition: "first"` (adapter default on `desc`), mirror of `009` | SEM-3, SEM-6, AR-9, AG-3 |
| 011 | `sem7-contains-wildcard-correct` | general (PG+Mongo+Search) | Correct `wildcard` artifact for `contains` (positive half of the `012` pair) | SEM-7, SEM-1 |
| 012 | `sem7-contains-tokenized-match-antipattern` | general (PG+Mongo+Search) | The forbidden tokenized-`match` form, documented in prose only; `expected/` ships only the correct `wildcard` form | SEM-7 |
| 013 | `sem7-searchmode-analyzed-optin` | general (PG+Mongo+Search) | Explicit per-field `searchMode: "analyzed"` opt-in compiles `contains` to `match` on Search only | SEM-7, SC-3 |
| 014 | `sem1-case-sensitive-optin-eq` | general (PG+Mongo+Search) | Per-field case-**sensitive** opt-in: no CI mechanism applied at all | SEM-1, SC-3 |
| 015 | `sem1-case-insensitive-ordering-gt` | general (PG+Mongo+Search) | CI on an ordering comparison (`gt`) on a text field; ES's `physical.search.normalizedSubField` + value lowercasing (no `case_insensitive` on `range`); Postgres uses the canonical `craticula_ci` collation | SEM-1, OQ-11 |
| 016 | `sem1-case-insensitive-in-set` | general (PG+Mongo+Search) | CI on `in` via the `craticula_ci` collation (`= ANY($1)`), never `ILIKE ANY`; ES decomposes into `bool`/`should` of `term` queries (`terms` has no `case_insensitive`) | SEM-1, AR-6 |
| 017 | `sem2-notcontains-includes-nulls` | general (PG+Mongo+Search) | Null-inclusion on `notContains`, composed with SEM-1 CI + SEM-7 substring mechanism | SEM-2, SEM-1, SEM-7 |
| 018 | `sem2-notin-includes-nulls` | general (PG+Mongo+Search) | Null-inclusion on `notIn`, on a case-sensitive field (orthogonal to SEM-1); reached via the `customFilters` wire extension (AG Grid's `set` filter alone cannot express `notIn`) | SEM-2, SC-3, OQ-11 |
| 019 | `sem2-blank-notblank-exact-negation` | general (PG+Mongo+Search) | `notBlank` rendered as an explicit negation of `blank`'s own compiled form, never derived independently | SEM-2 |
| 020 | `sem7-mongo-regex-full-metacharacter-escaping` | general (PG+Mongo+Search) | A filter value exercising SEC-4's LIKE-wildcard set and the wider regex-metacharacter set side by side | SEM-7, SEC-4 |

## Notes on artifact construction

- **Parameter placeholder style** (this category's own convention, since no dialect-specific
  fixture existed before it to set one): Postgres positional `$1, $2, ...` (matching `sort-paging`'s
  own convention); SQL Server named `@p1, @p2, ...`; MySQL/SQLite positional `?`.
- **MySQL vs. SQL Server null-ordering emulation are not the same mechanism**: MySQL's single-argument
  `ISNULL(col)` is a null-test returning `1`/`0`; SQL Server's own `ISNULL(expr, replacement)` is a
  **two-argument** substitution/`COALESCE`-equivalent function, so reusing that name for a null-test
  would be a real bug, not a naming preference. SQL Server therefore emulates with
  `CASE WHEN col IS NULL THEN 1 ELSE 0 END` instead (`003`, `009`, `010`).
- **SQLite supports native `NULLS FIRST`/`NULLS LAST` since 3.30.0** — treated like Postgres in the
  parity showcases (`009`/`010`), not given a MySQL/MSSQL-style emulation.
- **MongoDB query-level `collation`** (SEM-1's mechanism for `$eq`/`$in`/ordering/sort) is a
  find()/aggregate() **command-level** option, not a pipeline stage — `mongo.artifact.json` uses the
  `{ pipeline, collation }` wrapper object added to `spec/fixtures/schemas/mongo-artifact.schema.json`
  for this category (`oneOf` alongside the original bare-array shape; every fixture not needing a
  non-default collation still uses the plain array). `contains`/`notContains` never use this wrapper
  — SEM-1 is explicit that `$regex` has no collation-compatible substring form.
- **Search's `terms` (plural) query has no `case_insensitive` parameter** — only `term`/`wildcard`/
  `prefix`/`regexp` (singular forms) do. Case-insensitive `in` (`016`) decomposes into a `bool`/
  `should` of per-value `term` queries rather than a single `terms` query.
- **Every SQL `LIKE`/`ILIKE` artifact in this category carries an explicit `ESCAPE '\'` clause** —
  including Postgres, whose own default escape character already happens to be `\`, per SEM-7's
  dialect-independent Must-Not.
- **Every MongoDB `$regex` artifact escapes the full regex-metacharacter class** (not just SEC-4's
  four LIKE/wildcard characters) and carries the `"i"` option — visibly exercised (not just a
  structural no-op) in `020`.
- **`eq`/`in` (exact-match and set-membership) on a case-insensitive field use a collation, never
  `ILIKE`** (SEM-1's mechanism-note split, pinned this session): Postgres uses this project's
  canonical `craticula_ci` ICU collation (`COLLATE craticula_ci = $1` for `eq`, `COLLATE
  craticula_ci = ANY($1)` against a single array parameter for `in` — the same array-parameter
  shape `operators/035-in-string` already established) — `ILIKE`/pattern-match is reserved for
  `contains`/`notContains`/`startsWith`/`endsWith` (`007`'s territory), where the operator itself
  is a pattern match. `015`'s ordering-comparison (`gt`) case uses the same `craticula_ci` name for
  consistency, and Search's case-insensitive-ordering sub-field is declared via
  `physical.search.normalizedSubField` (not an overload of `keywordSubField`).
- **Four artifacts in this category were re-authored in #1638 to the canonical SQL renderings**
  (`spec/semantics.md`, "Addendum — canonical SQL rendering"), because this category held every one
  of the suite's rendering minorities: `008` gained the mandatory parentheses around its
  null-inclusive disjunction (`("age" <> $1 OR "age" IS NULL)`, SQL-2); `017` moved the negation
  into the operator (`"lastName" NOT ILIKE ...`, SQL-3) and gained the same wrap; `018` moved from
  `NOT IN ($1, $2)` with two scalar parameters to `<> ALL($1)` with one array parameter (SQL-4) and
  gained the wrap; `019`'s `notBlank` became the literal negation of `blank`'s own compiled text,
  `NOT (("lastName" IS NULL OR "lastName" = ''))` (SQL-2). All 16 Postgres artifacts also gained
  identifier quoting — which matters most *here*, since this category is the one that declares
  camelCase physical columns (`lastName`, `employeeCode`, `jobTitle`) that unquoted PostgreSQL would
  fold to lowercase.
- **`008`'s `mysql`/`mssql`/`sqlite` artifacts gained the SQL-2 wrap too, and that was not optional.**
  Parenthesisation is *structural*: a query builder emits it and the `Dialect` interface has no say
  in it, so the previously bare `WHERE age <> ? OR age IS NULL` was unsatisfiable by construction on
  every dialect, not merely unusual. Do not "restore" it when re-authoring these three for #1639.
- **The 19 `mysql`/`mssql`/`sqlite` artifacts are still UNQUOTED, and that is a known gap, not a
  rule.** SQL-1 ("always quote") is universal and explicitly forbids quoting conditionally — the
  deferral is not about *whether* to quote but about *which character*: `"` on SQLite, `[…]` on SQL
  Server, backticks (or `"` under `ANSI_QUOTES`) on MySQL. Picking each is part of writing that
  engine's `Dialect`, i.e. **#1639**, which re-authors these 19 as it goes. Until then `006`-`010`'s
  six-artifact parity sets are temporarily mixed (quoted Postgres, unquoted others), and a port that
  quotes them and disagrees with the fixture is right while the fixture is stale — the opposite of
  T-1's usual reading, which is exactly why it is written down. (Contrast their `IN (...)` in-list
  form and their `ISNULL`/`CASE WHEN` null-ordering emulations, which genuinely *are* each dialect's
  own business rather than a deferred application of a universal rule.)
- **Every fixture's compiled statement includes SEM-6's `stableKey` tiebreaker and its own
  null-ordering marker (`NULLS LAST`/Mongo bucket/ES `missing`), unconditionally** — every
  `agGridRequest.json` in this category carries `startRow`/`endRow`, and every `schema.json`
  declares `stableKey: "id"`, so every compiled artifact (SQL/Mongo/Search alike) ends in the `id`
  tiebreaker and its paging clause, regardless of whether the fixture's own focus is sorting at
  all — mirroring `filter-tree/001`'s complete-statement convention for the identical request
  shape.
- **Every MongoDB `$sort` stage that uses a null-bucket key is immediately followed by a `$unset`
  stage dropping every `__<field>NullBucket` key it used**, per SEM-3's mandatory bucket-cleanup
  rule — the same convention `sort-paging/` and `filter-tree/` already apply. `006`/`007`/`009`/
  `010` unset both `__<field>NullBucket` and `__idNullBucket` (two sort keys); every other Mongo
  artifact in this category unsets only `__idNullBucket` (the bare `stableKey` tiebreaker being its
  only sort key).
