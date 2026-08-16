# `group-aggregation/` fixtures

Backs **BM-2** (grouping over SSRM), **BM-3** (set-filter values query), and **AR-8** (group +
aggregation spec, closed v1 function set) from `docs/TRD-grid-query-compiler.md`. See
`spec/fixtures/README.md` for the general fixture shape and backend-coverage policy; every case
here is a **general/structural** fixture, so each carries `expected/postgres.artifact.json` +
`expected/mongo.artifact.json` + `expected/search.artifact.json`. #1640 added
`mssql`/`mysql`/`sqlite` arms to the sixteen BM-2 cases as well and #1641 to the five BM-3 ones (see
"Per-dialect arms" below) — not because any of them is dialect-*specific*, but because BM-2's DoD is
"identical grouping protocol verified across all four dialects" and three of the four had no artifact
to verify against. #1642 then added an `oracle` arm to every one of them, so **every case here is
seven-backend**.

All 23 cases share one `schema.json` ("sales" grid: `id` stableKey; `region`/`segment`/`product`
groupable strings; `fiscalYear` groupable int64; `amount` a decimal aggregatable over
`sum`/`min`/`max`/`avg`; `quantity` an int64 aggregatable over all five funcs including `count`).
All 23 read one base SQL table / Mongo collection / Search index, `sales_fact`. That name used to
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
now aligned with the rest of the addendum too — the two points on which it was not are settled below
— and note that `020`'s `params[0]` was separately corrected to AR-7's decimal-as-string (`"1000"`,
not `1000`).

## Settled by #1640 (BM-2) — the register that used to live here

This section held two "known open contradictions" that #1638 deliberately refused to guess at,
because both turned on a **BM-2 design call** it did not own. #1640 settled both, re-authored the
affected artifacts in the same commit, and — the part that matters for a port — put the answers in
`spec/semantics.md` as the normative **grouping addendum (GRP-1..GRP-5, joined by GRP-6 in #1641)**
rather than leaving them in this file. Read those rules, not the summary below; the summary exists so a reader who
remembers the old artifacts can tell what changed and why.

**(a) Grouped `ORDER BY` keys omit the collation → GRP-2: a grouping request is exact on the
protocol's own keys.** The `GROUP BY` key, the drill-down `WHERE` and a **grouped level's**
`ORDER BY` keys render with **no case-folding mechanism at all**, so `"EMEA"` and `"emea"` are two
buckets with two unambiguous labels. The three have to agree or a child level stops summing to its
parent's aggregate, and "collate everywhere" is not available: `SELECT "segment" … GROUP BY
"segment" COLLATE craticula_ci` groups by an expression the select list no longer contains and the
engines reject it. So the bare `ORDER BY` keys the **grouped** artifacts always had are now correct
**by rule**; what changed is the other end — `002`-`012` and `015` **lost** the `COLLATE
craticula_ci` their drill-down predicates carried, Mongo lost the query-level `collation`, and the ES
drill-down `term`s lost `case_insensitive: true`. A user's own filter is untouched: `017`'s
`WHERE "segment" COLLATE craticula_ci = $1` is a filter, not a group key, and still collates.
`ag-grid-majors/004`'s **artifacts** needed no edit; its stated rationale did, and #1640's review
corrected it there and in that category's README.

> **Narrowed by #1640's review: the drill-down LEAF's `ORDER BY` is not one of the protocol's keys.**
> #1640's first draft keyed the `ORDER BY` half of GRP-2 on "the request carries a `groupSpec`",
> which swept `005`/`006` in with the grouped levels. A leaf has no `GROUP BY` and no buckets — `005`
> sorts on `product`, which is neither a group column nor a group key — so nothing about bucket
> identity is at stake and **SEM-1 governs it**. It was a live defect, not an inconsistency: a leaf
> filtering and sorting one field would have emitted a collated `WHERE` beside a bare `ORDER BY`, the
> mix SEM-1's third Must-NOT forbids. Re-authored: `005`/`006`'s `postgres` (`COLLATE
> craticula_ci`), `mssql` (`COLLATE Latin1_General_CI_AS`, inside the null-bucket pair) and `sqlite`
> (`COLLATE NOCASE`) arms; their **`mysql` arms are unchanged**, because on MySQL case-insensitive
> *is* the column's own declared collation and there is nothing to spell (SQL-6). Their **search**
> arms move to a `search.normalizedSubField` (`product.normalized` — `005`'s user key; the
> tiebreaker's own `id` is settled separately just below), which SEM-1 requires for case-insensitive
> ordering on ES/OpenSearch and which that schema now declares.
> Their **mongo** arms carry no `collation`, and on `005` that is the accepted divergence GRP-2 now
> records: Mongo's `collation` is query-level, so a leaf cannot collate its `$sort` without also
> folding the drill-down `$match` that reproduces the bucket — and bucket identity wins. **On `006`
> it is not the divergence at all**, and `spec/semantics.md` listed it as one until #1643's review:
> `006` carries no `sortModel`, so its only key is the appended tiebreaker, which the paragraph below
> settles as exact on *every* backend. "Suppressed a collation it wanted" and "never wanted one" are
> different compiler states; only `005` is in the first. *(This sentence read "their mongo arms are
> also unchanged" until the same pass. Both arms did change in #1643 — GRP-5's `$and` wrap and
> MDB-1's explicit `$eq` — but never in the collation, which is the only thing it was claiming.)*
>
> *Consequence, and the second-round correction it needed.* These schemas declare `stableKey: "id"`
> as a `string` with no `text` block, so SEM-1 makes it case-**insensitive**, and the first pass let
> the SEM-6 tiebreaker collate along with the user's key
> (`ORDER BY … "id" COLLATE craticula_ci ASC NULLS LAST`). It recorded that as an open SEM-1/SEM-6
> question predating BM-2, on the premise that "the ungrouped path already does this for any string
> `stableKey` — no fixture had one until now". **The premise was wrong about the oracle**: measured
> across all 164 fixtures and every backend arm they carry, 21 declare a case-insensitive `string` `stableKey`
> and `005`/`006` are the *only* two that ever pinned a collated tiebreaker — both authored here, and
> on `main` both read `ORDER BY … "id" ASC NULLS LAST`. So this pass was not inheriting a rule, it
> was minting one.
>
> Settled, not deferred: **the appended tiebreaker resolves exact** (SEM-6's own supersession note in
> `spec/semantics.md`). `craticula_ci` is `deterministic = false`, so `'aB3'` and `'Ab3'` compare
> equal under it and a collated tiebreaker leaves the sort non-total — the same statement can order
> those rows differently at `OFFSET 0` and `OFFSET 100`, returning one twice or dropping it from both
> pages, which is precisely what SEM-6 exists to prevent. Re-authored back to the bare key:
> `005`/`006`'s `postgres`, `mssql` and `sqlite` arms, and their `search` arms back to a plain `id`
> (the `id.normalized` sub-field those two schemas had gained is removed again — `product.normalized`
> stays, since `005`'s `product` key is the *user's*). `mysql` and `mongo` were already bare. `005`
> keeps its collated `product` key beside the bare `id`, which is the whole point: the rule is about
> the key's provenance, not about the field.

**(b) The drill-down conjunction renders unwrapped → GRP-5: it is real IR, so it wraps.** Each
consumed key is an `eq` node, two or more are wrapped in an `and` node, and the result is merged
with the user's filter through the same primitive `customFilters` and static filters use. SQL-2
therefore applies with no exception and `003`/`004`/`005` now read
`WHERE ("region" = $1 AND "segment" = $2)`. A single consumed key is not a composite and stays bare
(`002`, `006`).

## Settled by #1641 (BM-3) — `016`-`019`, the last register entry

Entry (a) above covered **20** artifacts, but only 16 of them were settled: `016`-`019` carry a
synthetic `setValuesRequest` (no `rowGroupCols`, no `groupKeys`), so GRP-2 — scoped to a request
carrying a `groupSpec` — never reached them, and the question of whether BM-3's distinct-values query
is *itself* a bucket-identity query was left as a BM-3 design call.

**It is.** `spec/semantics.md`'s **GRP-6** now says so normatively: `SELECT DISTINCT` computes a
distinct set exactly as `GROUP BY` does, and its entries are handed straight back to the grid to be
resent as `in` values or as a `groupKey`, which a grouped query matches **exactly** (GRP-2) — so a
collated `DISTINCT` would fold `"EMEA"` and `"emea"` into one entry whose label is unspecified and
which then matches neither original row. All four artifacts already rendered that way, so **none was
re-authored for this**. Read GRP-6, not this summary; it also pins the four things around the
distinct key that no artifact could state — the own-field exclusion, the search prefix's status as an
ordinary `startsWith` predicate, the cap's `min(requested ?? default, maximum)` resolution, and the
row-limit-without-offset shape (the one place BM-3 widened the dialect seam).

**`017` was a fixture whose name asserted a property its data could not exercise**, and #1641 fixed
it in place. Its `filterModel` carried a `segment` filter and *no* `region` one, while its own name
is `set-filter-values-excludes-own-field-filter` — so it proved that another column's filter is
honoured and said nothing at all about the target column's being dropped. A compiler that omitted the
exclusion entirely passed it. It now carries a `region` filter beside the `segment` one, and because
the exclusion removes it, **all six artifacts then existing are byte-identical** to what they were
(the `oracle` arm #1642 added later was derived from the corrected request): a vacuous
fixture became a real test at zero artifact cost, and a compiler that forgets the exclusion now fails
against the *existing* oracle. (Verified rather than assumed — compiling the corrected request
reproduces every arm exactly.)

**`018`'s Postgres arm was wrong, and this is the one artifact #1641 re-authored.** It rendered the
prefix pattern *inside the bound parameter* — `"region" ILIKE $1 ESCAPE '\'` with `params: ["eu%"]` —
where the suite's own canonical `startsWith` rendering composes the pattern in SQL and binds the
escaped literal alone: `operators/027` (and `025`/`026`/`028` for the rest of the family) pin
`ILIKE $1 || '%' ESCAPE '\'` with `params: ["Smart"]`, and SQL-3/SEM-7 describe that form. One
artifact against twelve is not a disagreement about which is canonical (`operators/` carries three
backends per case, not six — the "twenty-four" this paragraph read until #1642's review pass was an
over-count of the same four fixtures). Corrected to
`"region" ILIKE $1 || '%' ESCAPE '\'` / `params: ["eu", 1000]`. Its **mongo** arm was already right
(`{"$regex": "^eu", "$options": "i"}` is exactly `operators/027`'s) and its **search** arm is
untouched — a `terms` aggregation's `include` is an aggregation-level filter with its own mechanism,
which that case's `description.md` explains at length.

**One further defect, found by compiling the category rather than reading it.** `013`'s three
aggregations carry distinct `ColumnVO.id`s (`amountSum`/`amountMin`/`amountMax`) over one field, so
AGG-2 makes those ids the aliases — but its artifacts spelled the compiler defaults
(`amount_sum`/`amount_min`/`amount_max`). Its twin `012` had already been corrected to the AGG-2
form in an earlier review pass; `013` was missed. Fixed on all six arms then existing (`013`'s
`oracle` arm was authored later, in #1642, already carrying the AGG-2 names). The compiler was right and
the fixture was wrong, which is the direction T-1 assumes only when the fixtures agree with each
other — here they did not, and `012` was the tiebreak.

## The Mongo arms, compiled for the first time in #1643

Until #1643 nothing had ever executed this category's `mongo.artifact.json` files. Compiling them
found two defects, both adjudicated from `spec/semantics.md` rather than from the compiler:

- **The drill-down rendered flat where GRP-5 requires a composite.** `003`, `004` and `005` spelled
  two or three consumed `groupKeys` as sibling keys of one `$match` document
  (`{"region": "EMEA", "segment": "Enterprise"}`). That is #1630's original reading — "a synthesised
  conjunction is not an IR construct" — which **GRP-5 rejects by name**, and which #1640 had already
  removed from the SQL arms (their `WHERE` is `("region" = $1 AND "segment" = $2)`, wrapped). The
  drill-down is ordinary IR merged through the same primitive as `customFilters`, so any compiler
  reusing its filter renderer emits `{"$and": [...]}` for free. Re-authored on all three; a single
  consumed key is not a composite and still renders bare.
- **`020` bound a `decimal` as a JSON number.** Its filter is `amount > 1000` and `amount` is declared
  `decimal`, so the value is AR-7's numeric string and must be a Decimal128 literal —
  `{"$numberDecimal": "1000"}`, as `operators/046`/`047` and `ag-grid-majors/002` already spell it,
  and as its own Postgres arm already binds (`params: ["1000", ...]`, a string). A JSON number in
  Extended JSON is an IEEE double, which is precisely the round-trip AR-7 exists to prevent.

## Per-dialect arms (#1640, extended to Oracle by #1642)

`001`-`015` and `020` now carry `mssql`/`mysql`/`sqlite` arms alongside `postgres`/`mongo`/`search`
— 48 artifacts, derived by mechanical transformation of each fixture's **own** Postgres arm using
only the per-dialect token table in `spec/semantics.md` SQL-1/SQL-5/SQL-6 and
`dialect-parity/README.md` (quote character, placeholder form, SEM-3 null-ordering emulation, and
the paging clause with its binding order), never by running a compiler and recording its output.
That is what makes BM-2's "identical grouping protocol verified across all four dialects" checkable.
#1642 added a fifth arm to every case in this category (Oracle, at full parity coverage), so the
claim now spans five dialects; **not one of the 22 needed a structural change for it** — BM-2's
grouping protocol is entirely ANSI, and Oracle's only genuine divergence (SQL-7's `blank` collapse)
is not reachable from any request in this category.

`021` (added in #1640's review pass, pinning AG-6's auto-group-column colId) carries every arm too,
derived the same way from `002`'s.

*Corrected in the second review round:* `021` originally carried a **two**-entry `sortModel`
(`[ag-Grid-AutoColumn, segment]`), which its own `rowGroupCols: [region, segment, product]` could
never produce. AG Grid's default single display column carries `showRowGroup: true`, which
`getSourceColumnsForGroupColumn` answers with *every* row-group column, and `setSortForColumn` sorts
`[column, ...sortableRowGroupColumns]` — so one header click sends
`[ag-Grid-AutoColumn, region, segment, product]`. The request now carries that; all six artifacts then
existing are unchanged, because the collapse still yields the single `segment` key, which is what makes the
re-derivation evidence rather than bookkeeping.

`016`-`019` gained theirs in **#1641**, once BM-3's compiler existed — 12 more artifacts, derived the
same way from each case's own Postgres arm. **#1642** then added the 22 `oracle` arms in one pass,
by the identical transformation, so every case in this category is now seven-backend — including
`023`, authored later with all seven from the start. `022`
was added in #1641's review pass, derived the same way but
from **two** sibling arms rather than one: `017`'s for the surviving `segment` predicate and `018`'s
for the prefix, conjoined under SQL-2's composite rule. It exists because GRP-6(c)'s `WHERE` order is
normative and `params`-visible while no artifact carried both halves — the gap that rule's own text
admitted to. Two
tokens are specific to this shape and appear nowhere else in the suite: the row cap renders
`LIMIT ?` on MySQL/SQLite (no `OFFSET` — a distinct-values query is not paged) and
`OFFSET 0 ROWS FETCH NEXT @p1 ROWS ONLY` on SQL Server, whose `FETCH` is illegal without an `OFFSET`
and whose zero is therefore a literal rather than a bound parameter (Oracle needs no such trick: its
`FETCH FIRST` is legal with no `OFFSET`, a third spelling again). All five dialects bind exactly
one value for it, so `params` is identical across the arms. See `spec/semantics.md` GRP-6(e) for the
table and the reasoning.

*The T-1b risk this section used to record is closed — as a defect, not a risk.* It said `020`'s
`mssql` and `mysql` arms order by the aggregate's **alias** inside a null-bucket expression
(`CASE WHEN [amount_sum] IS NULL THEN 1 ELSE 0 END DESC` / `` ISNULL(`amount_sum`) DESC ``) and that
"both engines resolve select-list aliases in `ORDER BY` expressions, so this should execute". **That
claim was false as written.** PostgreSQL's `SELECT` documentation says an output column name "has to
stand alone, that is, it cannot be used in an expression" — its own arm was legal only because the
alias did stand alone there — and T-SQL binds the same way, resolving an alias inside a `CASE`
against the `FROM` scope and raising Msg 207, *Invalid column name*. GRP-4 now requires the
**aggregate expression** in the ordering path on every dialect, including the
already-legal Postgres one, and `020`'s four SQL arms then existing were re-authored to `SUM("amount")` /
`SUM([amount])` / `` SUM(`amount`) ``. Uniform rather than fixed on the two broken arms, because
"which of these two forms is legal here" is a semantic difference, not the per-dialect spelling the
`ISqlDialect` seam exists for. `020`'s **mongo** arm is unaffected and keeps the name: after `$group`
the aggregate is a real field of the document a later `$sort` reads, which is not an alias in a scope.
(Its **search** arm was deleted in #1644's review pass — see below.)

### `020` has no `search` arm, and the absence is the finding (#1644 review)

`020` orders a **grouped** level by an aggregate, and **this backend cannot express that request at
all**. A `composite` aggregation enumerates buckets in composite-key order and returns `size` of them;
a `bucket_sort` is post-processing that reorders only the buckets already returned. So `020` on
Postgres means "the 100 largest regions by revenue" and the arm that used to sit here meant "the first
100 regions alphabetically, rearranged by revenue" — different groups as soon as the level has more
than 100 of them. Elasticsearch's own composite documentation states the general rule: a composite
aggregation is **not compatible with pipeline aggregations**, for exactly this reason. The arm was
therefore deleted rather than re-spelled, `SearchQueryBuilder` **refuses** the request
(`spec/semantics.md` OSE-11(c)), and the missing arm is asserted by name in
`SearchQueryGroupingTests` so it reads as a recorded capability gap rather than an oversight.
Ordering a grouped level by an aggregate is a real, user-visible gap on OpenSearch/Elasticsearch; it
needs a different grouped-ordering strategy, owned by #1622 and gradeable only against a live cluster
(#1624).

## Aggregation semantics addendum (AGG-1..AGG-5) — applied category-wide

`spec/semantics.md`'s "Addendum — aggregation semantics" section pins five rules this category's
artifacts follow; a review pass re-aligned every fixture to them:

- **AGG-1** — `count` aggregates non-null occurrences of the counted field, never a bare row count.
  Mongo's `count` artifacts (011) use the null-safe `{"$sum": {"$cond": [...]}}` form, never
  `{"$sum": 1}`.
- **AGG-2** — `aggregation.alias` is set from the AG Grid `ColumnVO.id` iff it differs from
  `ColumnVO.field` (012, 014); when `id === field`, `alias` is absent and the compiler's own
  default naming applies (007-011, 013, 015).
- **AGG-3** — a `groupKeys` entry is a typed value compiled into an equality predicate against its
  group column. **How that predicate treats case is GRP-2's, not SEM-1's, and #1640 changed the
  answer**: it is exact, so Postgres renders `"region" = $1` bare, Mongo carries no query-level
  `collation`, and Search's `term` carries no `case_insensitive`. An ordinary `eq` **filter** on the
  same field is unaffected and still collates — see `017`, whose set-values request filters on
  `segment` and keeps `COLLATE craticula_ci`.
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
    "filterModel": { "<anyColId>": { "...": "..." } },
    "customFilters": { "type": "and", "nodes": [] },
    "searchPrefix": "eu",
    "limit": 10000
  }
}
```

- `colId` (required) — the field whose distinct values are being resolved. It is the *question*, not
  a condition on it, so SC-1's Lenient mode may not drop it: an unknown `colId` is rejected in every
  schema mode (GRP-6).
- `filterModel` (required, may be `{}`) — the grid's *current* filter model, **exactly as the grid
  holds it**, including any entry keyed by `colId` itself. **The server is what drops that entry**
  (GRP-6's own-field exclusion), not the client.

  > *Changed in #1641, deliberately.* This note used to say the model "never includes an entry keyed
  > by `colId` itself", i.e. that the host had already stripped it. That made the exclusion
  > unobservable: `017` is named for it and carried no `region` entry, so nothing in the suite could
  > tell a compiler that implements the rule from one that ignores it. Putting the entry back is what
  > makes the fixture a test, and it costs nothing — the compiled artifacts are unchanged, because
  > the exclusion removes it. It also puts the rule where every backend inherits it, instead of
  > relying on each host to reimplement it correctly.
- `customFilters` (optional) — OQ-11's extension fragment, deserialized as IR, exactly as on the
  row-fetch request. A set filter's value list must honour the same conditions the grid's rows do, or
  it offers values no visible row has. Merged before the own-field exclusion, so it is scoped by it
  too. No case in this category carries one; the shape is the row-fetch request's, unchanged.
- `searchPrefix` (optional) — the set filter's own inline search box text, compiled into an ordinary
  `startsWith` predicate on `colId` (GRP-6). Omitted when the box is empty; `""` is rejected rather
  than read as "no prefix".
- `limit` (optional) — a client-requested cap; the server clamps it to its own configured maximum and
  answers, never rejects (see 019). When absent, the host's configured **default** applies. This
  suite's illustrative numbers are a 1,000 default and a 10,000 maximum, which is why `016`-`018`
  compile to 1,000 and `019` — requesting 50,000 — compiles to 10,000. Both are host-configured SEC-3
  values, not constants of the format.

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
| 013 | `aggregation-multiple-sum-min-max` | AR-8, BM-2 | 3 funcs together, top-level group, distinct `ColumnVO.id` aliases (AGG-2) |
| 014 | `aggregation-alias-explicit` | AR-8 | `alias` present (`"totalRevenue"`) |
| 015 | `aggregation-alias-default-name` | AR-8 | `alias` absent, compiler default `<field>_<func>` |
| 016 | `set-filter-values-no-active-filters` | BM-3, GRP-6 | empty `filterModel`, no cap requested (host default applies) |
| 017 | `set-filter-values-excludes-own-field-filter` | BM-3, GRP-6 | own field's filter **present and dropped**; another column's honored — artifacts identical either way |
| 018 | `set-filter-values-search-prefix` | BM-3, GRP-6, SEM-7 | `searchPrefix` narrows candidate values, case-insensitive prefix match |
| 019 | `set-filter-values-server-side-cap` | BM-3, GRP-6 | client requests a huge `limit`; server clamps to its configured max |
| 020 | `grouped-query-with-filter-and-sort` | BM-2, AR-5, AR-9, AR-8 | grouped query + row-level filter (pre-aggregation) + sort on the aggregate |
| 021 | `group-column-sort-via-auto-column-colid` | BM-2, AR-9, GRP-4, AG-6 | `sortModel` addresses the group column through `ag-Grid-AutoColumn`, plus the whole coupled row-group set (1 + N entries) |
| 022 | `set-filter-values-prefix-with-active-filter` | BM-3, GRP-6(b), GRP-6(c) | the **only** case carrying the own-field exclusion and a `searchPrefix` on that same column in one statement — pins GRP-6(c)'s `WHERE` order (user's filter leads, prefix trails), which `params` compares exactly |
| 023 | `grouped-drill-down-with-active-row-filter` | GRP-5, BM-2, AR-5, AR-8 | the **only** case carrying a consumed `groupKeys` **and** a `filterModel` — pins GRP-5's clause order (drill-down leads, user filter trails) on all seven arms, and each backend's composition of two conjunctions (SQL/Mongo nest, Search flattens into one `bool.filter`). Authored in #1644's review pass; a mutation reversing the order had passed every test in every suite |
