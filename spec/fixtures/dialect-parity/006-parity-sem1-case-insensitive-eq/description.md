# 006 — Cross-backend parity showcase: SEM-1 case-insensitive `eq`

**Proves:** SEM-1's rule that `eq` is case-insensitive by default, reached identically across every
backend through the pinned backend-native mechanism from SEM-1's own worked examples (adapted from
`contains` to plain equality — no wildcard characters involved):

- **Postgres**: an explicit case-insensitive **collation** on the comparison —
  `COLLATE craticula_ci = :v` — never `ILIKE`. SEM-1's mechanism note pins collation, not
  pattern-match, for `eq`/`in`/ordering/sort; `ILIKE` is reserved for
  `contains`/`notContains`/`startsWith`/`endsWith` (`007`'s territory), where the operator itself
  is a pattern match. Reusing `ILIKE` here would silently widen an exact-match filter into a
  pattern match for any value containing a literal `%`/`_`.
- **SQL Server**: an inline `COLLATE Latin1_General_CI_AS` on the comparison.
- **MySQL**: relies entirely on the column's own declared `_ci` collation (e.g.
  `utf8mb4_0900_ai_ci`) — the compiled SQL carries no extra clause at all; the mechanism lives in the
  schema/DDL, not the query text (SEM-1's own wording: "column already uses a `_ci` collation").
- **SQLite**: an explicit `COLLATE NOCASE` — SQLite's default `=` comparison is `BINARY`, unlike its
  already-ASCII-CI `LIKE` (see `004`).
- **MongoDB**: a query-level `collation: { locale: "en", strength: 2 }` option (SEM-1's pinned
  mechanism for `$eq`/`$in`/ordering/sort — never `$regex`). Represented in this fixture's
  `mongo.artifact.json` via the `{ pipeline, collation }` wrapper shape (see
  `spec/fixtures/schemas/mongo-artifact.schema.json`'s `oneOf`), since MongoDB collation is a
  command-level option, not a pipeline stage.
- **OpenSearch/Elasticsearch**: a `term` query's `case_insensitive: true` parameter against the
  `.keyword` sub-field.

Every backend reaches the same logical outcome (`lastName` matches `"smith"`/`"Smith"`/`"SMITH"`)
through a different, index-friendly, native mechanism — none of them wraps the column in a bare
`lower()` (SEM-1's first Must-Not).

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`) — appended
unconditionally per SEM-6, exactly as `sort-paging`/`filter-tree` fixtures do for their own bare-`id`
tiebreaker, even though this fixture's own focus is SEM-1.

**Coverage tier:** cross-backend parity showcase — all five SQL dialects + Mongo + Search.

**Requirement ids:** SEM-1, SEM-6 (tiebreaker, incidental), AR-6.
