# `sort-paging/` — SEM-3 null ordering, SEM-6 stableKey tiebreaker, AR-9, AG-3

Per `spec/fixtures/README.md`'s category index, this directory proves: AR-9 (sort array shape and
order preservation), SEM-3 (every `sortItem` carries an explicit `nullsPosition`, and the adapter's
pinned default when AG Grid's `sortModel` gives no signal — `"last"` on `asc`, `"first"` on `desc`),
SEM-6 (the schema-declared `stableKey` is appended as a trailing tiebreaker whenever the user's own
sort doesn't already end in it, itself carrying an explicit `nullsPosition: "last"`), and AG-3 /
EXP-2 (`startRow`/`endRow` → `$defs/rangePage`, vs. the unpaginated `$defs/nonePage`).

Backend coverage: every case in this category is a general/structural fixture per the suite's
coverage policy — Postgres + MongoDB + one shared Search artifact, no dialect-specific or
cross-backend-parity showcase cases.

All cases model a shared `employees` domain: `id` (int64, the `stableKey`), `lastName` (string),
`age` (int64), `hireDate` (date), `salary` (decimal), `isActive` (boolean), `department` (string),
`createdAt` (datetime) — this lets the multi-key cases plausibly combine several columns without
inventing a new domain per case.

## Cases

| # | Slug | Proves | Requirement ids |
|---|---|---|---|
| 001 | `single-key-string-asc-nulls-last-default` | Single-key `asc` sort, no null signal from `sortModel`, adapter pins `nullsPosition: "last"`; tiebreaker appended | SEM-3, SEM-6, AR-9, AG-3 |
| 002 | `single-key-int64-asc-nulls-last-default` | Same default on an `int64` field, confirming the mapping is type-independent | SEM-3, SEM-6, AR-9, AG-3 |
| 003 | `single-key-date-asc-nulls-last-default` | Same default on a `date` field | SEM-3, SEM-6, AR-9, AG-3 |
| 004 | `single-key-string-desc-nulls-first-default` | Single-key `desc` sort, adapter pins `nullsPosition: "first"` | SEM-3, SEM-6, AR-9, AG-3 |
| 005 | `single-key-int64-desc-nulls-first-default` | Same `desc` default on an `int64` field | SEM-3, SEM-6, AR-9, AG-3 |
| 006 | `multi-key-two-fields-order-preserved` | 2-key `sortModel` (string asc, decimal desc): array order preserved, each key's own default computed independently | AR-9, SEM-3, SEM-6, AG-3 |
| 007 | `multi-key-three-fields-order-preserved` | 3-key `sortModel` across string/int64/date, mixed directions | AR-9, SEM-3, SEM-6, AG-3 |
| 008 | `stablekey-tiebreaker-appended-decimal-primary` | Tiebreaker appended after a non-unique `decimal` primary sort key | SEM-6, SEM-3, AR-9, AG-3 |
| 009 | `stablekey-tiebreaker-appended-boolean-primary` | Tiebreaker appended after a two-valued `boolean` primary sort key, `desc` direction | SEM-6, SEM-3, AR-9, AG-3 |
| 010 | `stablekey-tiebreaker-not-appended-sort-ends-in-key` | Negative case: `sortModel` already ends in `stableKey` — no duplicate tiebreaker appended | SEM-6, SEM-3, AR-9, AG-3 |
| 011 | `page-range-with-tiebreaker` | `page: { mode: "range" }` at a non-zero offset (`startRow: 20, endRow: 40`), tiebreaker makes the offset read deterministic | AG-3, SEM-6, SEM-3, AR-9 |
| 012 | `page-none-unpaginated-export` | `page: { mode: "none" }` via `exportScope: "all"` (EXP-1): full unpaginated artifact, no row-range clause in any backend | EXP-1, EXP-2, SEM-3, SEM-6, AR-9 |

## Notes on artifact construction

- **Postgres**: native `ORDER BY "<col>" ASC|DESC NULLS LAST|FIRST` per key, comma-separated, ending
  in the tiebreaker's own `"id" ASC NULLS LAST`. Identifiers are always quoted, including the table
  — see `spec/semantics.md`'s "Addendum — canonical SQL rendering" (SQL-1); all 12 artifacts here
  were re-authored to it in #1638, a quoting-only change with no rendering rewrite. A `string`-typed sort key (`lastName`, `department` —
  cases 001, 004, 006, 007, 010, 011) additionally carries `COLLATE craticula_ci` on its own key,
  per SEM-1's "same collation mechanism across every context on that field" (the identical
  collation `eq`/`in` would use, not `ILIKE`) — the `id` tiebreaker never needs this since
  `stableKey` is `int64` here. Paging renders as `LIMIT $1 OFFSET $2` with `params`
  `[endRow - startRow, startRow]`; case 012 omits `LIMIT`/`OFFSET` and both params entirely.
- **MongoDB**: SEM-3's null-bucket emulation (no native "nulls last/first" `$sort` behavior) — a
  single `$addFields` stage computes one `__<field>NullBucket` per sort key (`1` if null, `0`
  otherwise), then a single `$sort` stage orders by `(bucket, field)` pairs **in that order** for
  every key, so the bucket always resolves null-vs-non-null placement before the field's own value
  breaks ties among non-null rows (SEM-3's "must NOT drop the original field as secondary key").
  The bucket's own sort sign is `1` (ascending) when `nullsPosition` is `"last"` and `-1`
  (descending) when it is `"first"`, independent of the field's own direction sign. This fixture
  suite implements the canonical stage order SEM-3's own worked example shows; SEM-6's worked
  example in `spec/semantics.md` shows a simplified key order for the same mechanism since that
  document states its snippets are illustrative, not literal compiler output. Every pipeline's
  `$sort` stage is immediately followed by a `$unset` stage dropping every `__<field>NullBucket` key
  it used, per SEM-3's mandatory bucket-cleanup rule. Range paging renders as trailing
  `$skip`/`$limit` stages; case 012 omits both. Cases with a `string`-typed sort key (001, 004, 006,
  007, 010, 011) use the artifact's wrapper-object form (`{"pipeline": [...], "collation": {"locale":
  "en", "strength": 2}}`, per `mongo-artifact.schema.json`) instead of the bare-array form, so the
  query-level collation reaches `$sort` the same way it would reach `$eq`/`$in` on that field.
- **Search (OpenSearch/Elasticsearch, shared per OQ-7)**: the native `sort` array's `missing`
  parameter (`"_last"`/`"_first"`) needs no emulation. Sorting on a `string`-typed field uses its
  schema-declared `normalizedSubField` (e.g. `lastName.normalized`), never `keywordSubField` —
  SEM-1's own gap: a `range`/ordering context on ES/OpenSearch has no `case_insensitive` parameter,
  so case-insensitive ordering needs the ICU-folded normalized sub-field instead of the plain
  `.keyword` exact-match sub-field `term`/`in` use. Range paging renders as `from`/`size`; case 012
  omits both. Every case includes `{ "query": { "match_all": {} } }` since none of these cases
  exercise `filterModel`.
