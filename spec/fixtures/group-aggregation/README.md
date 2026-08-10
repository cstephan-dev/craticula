# `group-aggregation/` fixtures

Backs **BM-2** (grouping over SSRM), **BM-3** (set-filter values query), and **AR-8** (group +
aggregation spec, closed v1 function set) from `docs/TRD-grid-query-compiler.md`. See
`spec/fixtures/README.md` for the general fixture shape and backend-coverage policy; every case
here is a **general/structural** fixture, so each carries `expected/postgres.artifact.json` +
`expected/mongo.artifact.json` + `expected/search.artifact.json` (no dialect-specific or
cross-backend-parity cases in this category).

All 20 cases share one `schema.json` ("sales" grid: `id` stableKey; `region`/`segment`/`product`
groupable strings; `fiscalYear` groupable int64; `amount` a decimal aggregatable over
`sum`/`min`/`max`/`avg`; `quantity` an int64 aggregatable over all five funcs including `count`).
The compiled artifacts illustrate one base SQL table/Mongo collection/Search index, named
`sales_fact` — `spec/grid-schema.schema.json` deliberately does not model the FROM
table/collection itself (SC-1..SC-5 scope, `#1631`'s concern), so this name is illustrative only,
chosen once and reused for consistency across the category.

## Aggregation semantics addendum (AGG-1..AGG-5) — applied category-wide

`spec/semantics.md`'s "Addendum — aggregation semantics" section pins five rules this category's
artifacts follow; a review pass re-aligned every fixture to them:

- **AGG-1** — `count` aggregates non-null occurrences of the counted field, never a bare row count.
  Mongo's `count` artifacts (011) use the null-safe `{"$sum": {"$cond": [...]}}` form, never
  `{"$sum": 1}`.
- **AGG-2** — `aggregation.alias` is set from the AG Grid `ColumnVO.id` iff it differs from
  `ColumnVO.field` (012, 014); when `id === field`, `alias` is absent and the compiler's own
  default naming applies (007-011, 013, 015).
- **AGG-3** — a `groupKeys` drill-down match (and any ordinary `eq` filter on the same field, e.g.
  016-019's set-values requests) follows the group column's own case-sensitivity (SEM-1): Postgres
  uses a case-insensitive collation (`COLLATE craticula_ci`, never `ILIKE`/`lower()` — SEM-1's
  collation-vs-pattern-match split), Mongo a query-level `collation` option, Search's `term` a
  `case_insensitive: true` parameter.
- **AGG-4** — a standalone `terms` aggregation backing a set-filter-values request (016-019) sets
  `"order": {"_key": "asc"}` explicitly, matching Postgres/Mongo's ascending-by-value order (never
  the engine's doc-count-descending default). Composite-aggregation `terms` *sources* (001-004,
  007-015, 020) need no such override — composite sources are inherently key-ordered.
- **AGG-5** — every composite aggregation's `missing_bucket: true` (group-completeness) carries a
  documented, accepted divergence: ES/OpenSearch's composite null bucket always sorts first,
  regardless of requested direction, unlike Postgres/Mongo which both honor it. Flagged in each
  affected case's `description.md`, not silently worked around.

## Design note — `groupKeys` uses the typed `typedValue` form, not raw scalars

Every grouped/leaf case's `agGridRequest.json` encodes `groupKeys` using the same
`$defs/typedValue` shape as `spec/ir.schema.json`'s `$defs/groupSpec.groupKeys` (e.g.
`{"type":"string","value":"EMEA"}`, or `{"type":"int64","value":2024}` in case 004) — **never** a
raw JSON scalar. This is a deliberate, documented deviation from AG Grid's own raw wire shape
(`groupKeys: string[]`, see the top-level `spec/fixtures/README.md`'s wire-shape section): a real
frontend adapter resolves each raw wire string into a typed value using the group column's
declared schema type before building the IR, exactly as it does for filter values (AR-7). A prior
review caught and fixed a regression where `ir.schema.json`'s `groupSpec.groupKeys` had been left
as raw untyped scalars instead of `typedValue` — these fixtures pin that fix at the request
boundary too, so a fixture consumer can build/check the IR directly against the typed contract
without having to separately reimplement (and get wrong) the string→typedValue resolution step,
which is out of scope for this compiler-focused fixture suite. Do not regress this: a `groupKeys`
entry that is a bare string/number here is a bug in the fixture, not a valid alternate form.

## Design note — BM-3's `setValuesRequest` is Craticula's own synthetic wire shape

`016`–`019` model the "get distinct values for a set filter" feature. This is **not** part of AG
Grid's own `IServerSideGetRowsRequest` wire contract — AG Grid Community has no fixed request/
response type for it at all; it delegates entirely to a host-supplied callback
(`SetFilterParams.values`) that receives the current grid state and calls back with whatever
values it wants, however it wants to fetch them. Craticula's compiler side (BM-3) needs *some*
concrete request shape to compile against, so this fixture suite defines one:

```json
{
  "setValuesRequest": {
    "colId": "region",
    "filterModel": { "<otherColId>": { "...": "..." } },
    "searchPrefix": "eu",
    "limit": 10000
  }
}
```

- `colId` (required) — the field whose distinct values are being resolved.
- `filterModel` (required, may be `{}`) — the grid's *current* filter model **minus** `colId`'s
  own filter, per BM-3's stated AG Grid convention (mirrors how AG Grid's real
  `SetFilterParams.filterModel` behaves for the equivalent client-side feature). Never includes an
  entry keyed by `colId` itself.
- `searchPrefix` (optional) — the set filter's own inline search box text.
- `limit` (optional) — a client-requested cap; the server enforces its own configured maximum
  regardless of what's requested (see 019). When absent, this fixture suite's illustrative default
  cap is 1,000; case 019's illustrative configured maximum is 10,000.

The field is still named `agGridRequest.json` in each of these four directories (per the shared
fixture directory convention) even though its *content* is this synthetic `setValuesRequest`
object, not an `IServerSideGetRowsRequest` — called out again in each case's `description.md`.

## Cases

| # | Slug | Proves | Notes |
|---|---|---|---|
| 001 | `group-top-level-no-keys-consumed` | BM-2, AR-8 | 0 keys consumed, top group level, single default-aliased `sum` |
| 002 | `group-second-level-one-key-consumed` | BM-2, AR-8 | 1 key consumed, second level, one equality filter |
| 003 | `group-third-level-two-keys-consumed` | BM-2, AR-8 | 2 keys consumed, third level, two equality filters |
| 004 | `group-deepest-level-three-keys-consumed` | BM-2, AR-8 | 3 keys consumed (4-column group), non-string (`int64`) typed key |
| 005 | `leaf-query-two-group-cols-fully-consumed` | BM-2 | `groupKeys.length == rowGroupCols.length` (2/2), leaf rows, explicit sort + SEM-6 tiebreaker |
| 006 | `leaf-query-single-group-col-fully-consumed` | BM-2 | `groupKeys.length == rowGroupCols.length` (1/1), leaf rows, tiebreaker-only order |
| 007 | `aggregation-sum-only` | AR-8, BM-2 | single `sum` func |
| 008 | `aggregation-min-only` | AR-8, BM-2 | single `min` func |
| 009 | `aggregation-max-only` | AR-8, BM-2 | single `max` func |
| 010 | `aggregation-avg-only` | AR-8, BM-2 | single `avg` func |
| 011 | `aggregation-count-only` | AR-8, BM-2 | single `count` func |
| 012 | `aggregation-multiple-sum-avg` | AR-8, BM-2 | 2 funcs on the same field, distinct `ColumnVO.id` aliases (AGG-2) |
| 013 | `aggregation-multiple-sum-min-max` | AR-8, BM-2 | 3 funcs together, top-level group |
| 014 | `aggregation-alias-explicit` | AR-8 | `alias` present (`"totalRevenue"`) |
| 015 | `aggregation-alias-default-name` | AR-8 | `alias` absent, compiler default `<field>_<func>` |
| 016 | `set-filter-values-no-active-filters` | BM-3 | empty `filterModel`, no cap requested (illustrative default applies) |
| 017 | `set-filter-values-excludes-own-field-filter` | BM-3 | another column's filter honored, own field's filter never present |
| 018 | `set-filter-values-search-prefix` | BM-3 | `searchPrefix` narrows candidate values, case-insensitive prefix match |
| 019 | `set-filter-values-server-side-cap` | BM-3 | client requests a huge `limit`; server clamps to its configured max |
| 020 | `grouped-query-with-filter-and-sort` | BM-2, AR-5, AR-9, AR-8 | grouped query + row-level filter (pre-aggregation) + sort on the aggregate |
