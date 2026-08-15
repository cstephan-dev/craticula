# 016 — SEM-1 case-insensitive `in` (set membership)

**Proves:** SEM-1's default CI applies to the `setOperator` `in` (`$defs/setOperator`) exactly as to
`eq` (`006`): `lastName in ["smith", "jones"]`, case-insensitive.

- **Postgres**: the same `craticula_ci` collation as `006`'s `eq` and `015`'s `gt`, applied via
  `= ANY($1)` against a single array parameter — the same shape `operators/035-in-string`
  established for `in` — instead of `ILIKE ANY (ARRAY[...])`. Per SEM-1's mechanism note, `in`/
  `notIn` use the collation mechanism exactly like `eq`, never `ILIKE`'s pattern-match form (which
  would silently treat a literal `%`/`_` inside a value as a wildcard).
- **MongoDB**: the same query-level `collation: { locale: "en", strength: 2 }` option as `006` —
  SEM-1 explicitly names `$in` among the operators this collation covers.
- **Search**: OpenSearch/Elasticsearch's **plural** `terms` query has no `case_insensitive`
  parameter at all — only the **singular** `term`/`wildcard`/`prefix`/`regexp` query types support
  it (SEM-1's own enumeration). `in` therefore decomposes into a `bool`/`should` of one `term` query
  per value, each carrying its own `case_insensitive: true` — semantically "any of these values,
  case-insensitively", reached without ever using the unsupported plural form.

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6.

**Coverage tier:** all seven backends — all five SQL dialects + MongoDB + Search. Promoted from
Postgres-only in #1639 (D2): this is SQL-4's dividing line made visible — Postgres binds ONE array
parameter (`= ANY($1)`), the other four bind N scalars (`IN (?, ?)`), so even `params.length`
differs across the five arms. It also pins SQLite's one internal asymmetry: `x IN (...)` resolves
its collating sequence from `x` alone, so `COLLATE NOCASE` sits on the **column** here where the
scalar comparison in `015` puts it on the value.

**Requirement ids:** SEM-1, SEM-6 (tiebreaker, incidental), AR-6.
