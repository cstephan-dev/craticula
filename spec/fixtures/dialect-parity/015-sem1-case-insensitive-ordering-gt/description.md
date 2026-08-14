# 015 — SEM-1 case-insensitive ordering comparison (`gt`) on a text field

**Proves:** SEM-1's explicit carve-out for the four ordering operators on a text field: "For the
ordering operators (`gt`/`gte`/`lt`/`lte`) on a text field, ES/OpenSearch has no `case_insensitive`
flag on `range` — case-insensitive ordering there means comparing against a normalized (lowercased)
`keyword` sub-field maintained at index time via an analyzer/normalizer, since there is no
query-time equivalent." Filter: `lastName > "Smith"`, case-insensitive (the field's default).

- **Postgres**: reaches the same CI ordering semantics as `006`'s `eq` case, applied to `>` instead
  of `=`, via an inline case-insensitive collation — this project's canonical, host-created ICU
  collation `craticula_ci` (the same name `006`/`016` use for their own exact-match/set-membership
  CI mechanisms; see `spec/semantics.md`'s SEM-1 mechanism note), never `ILIKE`, since `ILIKE` is a
  pattern-match operator and does not apply to ordering comparisons at all. No value transformation:
  `"Smith"` is passed through as typed, the collation does the folding at comparison time.
- **MongoDB**: the same query-level `collation: { locale: "en", strength: 2 }` option as `006` —
  SEM-1 explicitly names `$gt`/`$lt` among the operators this collation covers. No value
  transformation here either.
- **Search**: no `case_insensitive` parameter exists for `range` — the compiler instead targets a
  dedicated sub-field declared via `GridSchema`'s `physical.search.normalizedSubField` (distinct
  from `keywordSubField`, the `.keyword` exact-match sub-field used for `term`/`wildcard` CI ops
  elsewhere in this suite — see `spec/grid-schema.schema.json`), maintained via a lowercasing
  normalizer at index time, **and** lowercases the comparison value itself before querying
  (`"Smith"` → `"smith"`) to match that normalizer's index-time transformation — the one mechanism
  in this whole suite where the compiler transforms a value rather than relying purely on a
  query-time flag.

AG Grid's own text filter UI has no ordering-operator vocabulary at all (`iTextFilter.ts`'s closed
`type` enum — `equals`/`notEqual`/`contains`/`notContains`/`startsWith`/`endsWith`/`blank`/
`notBlank` — has no `greaterThan`/etc.; only number/date filters expose those). This fixture
therefore reaches the `gt` operator on a string field via the `customFilters` wire extension
(OQ-11) instead of a native `filterModel` entry, since AG Grid's own wire format structurally cannot
produce this IR shape.

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6.

**Coverage tier:** all six backends — all four SQL dialects + MongoDB + Search. Promoted from
Postgres-only in #1639 (D2): an ordering comparison is where the collation's *position* diverges
most visibly — the column side on Postgres and SQL Server, the **value** side on SQLite
(`"lastName" > ? COLLATE NOCASE`), and nowhere at all on MySQL.

**Requirement ids:** SEM-1, SEM-6 (tiebreaker, incidental), OQ-11 (`customFilters` usage).
