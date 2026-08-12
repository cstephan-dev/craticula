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
All 20 read one base SQL table / Mongo collection / Search index, `sales_fact`. That name used to
appear only in the expected artifacts — `spec/grid-schema.schema.json` did not model the FROM
target at all, so it was recoverable from nothing but the artifacts themselves. #1638 added a
required `source` to the schema (SQL-0 in `spec/semantics.md`'s canonical-rendering addendum); each
fixture's `source` was set from its own artifact's `FROM` clause, so `sales_fact` is now declared
input rather than an unexplained constant in the output.

**Identifier quoting started here and is now the suite-wide rule.** These 20 artifacts were the only
quoted ones in the suite (`SELECT "region" AS "region", SUM("amount") AS "amount_sum" FROM
"sales_fact"`) against 144 unquoted ones elsewhere. #1638 resolved that contradiction in this
category's favour — always quote, including the table and every alias — and re-authored the other
144. **On quoting alone**, nothing here changed: this category is the reference for that form. It is
*not* aligned with the rest of the addendum, though — see the register below, and note that `020`'s
`params[0]` was separately corrected to AR-7's decimal-as-string (`"1000"`, not `1000`).

## Known open contradictions (owned by #1640)

In the spirit of the register `ag-grid-majors/README.md` carried before #1638 closed its entry: when
a port's grouped output disagrees with these artifacts on the two points below, that is a **known
defect in the oracle**, not necessarily a bug in the port. T-1's "if a fixture and an implementation
disagree, the implementation has a bug" presumes the fixtures agree with each other; on these two
points they do not, and #1638 deliberately refused to guess because both turn on a **BM-2 design
call** it does not own. Delete an entry when #1640 settles it — and re-author the affected artifacts
in the same commit. (b) is confined to this category; (a) reaches one artifact outside it,
`ag-grid-majors/004`, which is the suite's only other grouped query and follows this category's
conventions by design.

**(a) Grouped/aggregated `ORDER BY` keys omit `COLLATE craticula_ci` on case-insensitive `string`
columns.** No `ORDER BY` key in any of these 20 artifacts carries the collation, although every one
of them sorts on a case-insensitive `string` column (`region`/`segment`/`product`, and `id`, which is
a `string` in this category's schema). Everywhere else in the suite the same key does carry it —
`sort-paging/001` renders `ORDER BY "last_name" COLLATE craticula_ci ASC NULLS LAST` — and so does
`SqlQueryBuilder.RenderOrderBy`, unconditionally, via
`SemanticsResolver.ResolveSortOrGroupKeyCaseSensitivity`. Case `002` contradicts *itself* within one
statement: `WHERE "region" COLLATE craticula_ci = $1 ... ORDER BY "segment" ASC NULLS LAST`, i.e. the
same category of column filtered under the collation mechanism and sorted without it — precisely
what SEM-1's "never mix the two mechanisms for one field" forbids. `ag-grid-majors/004`
(`ORDER BY "department" ASC NULLS LAST`, grouped) has the same defect for the same reason.

*Why it was not simply fixed:* adding the collation to `ORDER BY` alone would leave `GROUP BY`
without it, and on a `deterministic = false` ICU collation `GROUP BY` is what decides **bucket
identity** — whether `"EMEA"` and `"emea"` are one group or two. That is a semantics decision about
grouping (BM-2), not a rendering tidy-up, and it has to be made for `GROUP BY`, `ORDER BY` and AGG-3's
drill-down `WHERE` *together* or the three will disagree again.

**(b) The drill-down conjunction renders unwrapped.** `003` and `005` emit
`WHERE "region" COLLATE craticula_ci = $1 AND "segment" COLLATE craticula_ci = $2` — bare — and `004`
the same with a third conjunct, while SQL-2 requires every composite to wrap (`(a AND b)`), as
`filter-tree/` and `operators/` do. (`001` has no drill-down filter and `002` has a single one, so
neither is affected: a lone predicate is not a composite.)

*Why it is genuinely ambiguous rather than simply wrong:* all three have `filterModel: null`. Their
entire `WHERE` is synthesised from `groupKeys` by BM-2's drill-down protocol, so there is no IR
`andNode` anywhere — and SQL-2 is stated as a rule about *IR constructs*, so it may legitimately not
apply. #1640 must decide whether the drill-down conjunction is materialised as a real `andNode`
(wrap, and these three artifacts change) or assembled as clause text (no wrap, and SQL-2 gains an
explicit exception naming this case). Either answer is fine; leaving it unstated is not, because a
compiler that reuses the filter-tree renderer for the drill-down gets the wrapped form for free and
will look wrong against these three.

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
