# `spec/fixtures/` — the conformance fixture suite

Per **T-1** ([TRD](../../docs/TRD-grid-query-compiler.md) §11): this is the shared,
language-agnostic contract every backend compiler, in every language port, is graded against. If a
fixture and an implementation disagree, the implementation has a bug — see `spec/semantics.md`'s
own framing of the same principle for the rules these fixtures enforce.

## Fixture shape

```
<category>/<NNN-slug>/
  description.md          — what this case proves, which requirement id(s) it backs
  agGridRequest.json       — the raw AG Grid SSRM wire request (IServerSideGetRowsRequest shape)
  schema.json               — the GridSchema config this request compiles against (see ../grid-schema.schema.json):
                              the field allowlist + per-field physical mappings, the SEM-6 stableKey,
                              and `source` — the table/collection/index the query reads from, which
                              lives here because no AG Grid request carries one (added in #1638)
  expected/
    postgres.artifact.json   — present only for the backends this case targets (see policy below)
    mssql.artifact.json
    mysql.artifact.json
    oracle.artifact.json     — community-tested tier (see below); added by #1642
    sqlite.artifact.json
    mongo.artifact.json
    search.artifact.json     — shared OpenSearch/Elasticsearch artifact (OQ-7: one compiler, one DSL subset)
  dataset.json?             — optional, execution-equality tier (T-1b) — see "Deferred" below
  expectedRows.json?        — optional, execution-equality tier (T-1b) — see "Deferred" below
```

Every `expected/<backend>.artifact.json` validates against its shape schema in `schemas/`
(`sql-artifact.schema.json` for every SQL dialect, `mongo-artifact.schema.json`,
`search-artifact.schema.json`). Every `schema.json` validates against `../grid-schema.schema.json`
— itself a spec-level assumption authored here ahead of the actual `GridSchema` model (#1631), which
must conform to this shape rather than the reverse.

**Adversarial fixtures are a distinct shape.** SEC-6 requires injection/unknown-field/type-confusion/
depth-bomb cases to produce a "clean rejection" — no backend ever sees a compilable IR, because
validation rejects the request before compilation reaches a compiler. These cases have
`expected/rejection.json` (validating against `schemas/rejection.schema.json`) **instead of** any
`expected/<backend>.artifact.json`.

## Backend coverage policy

Not every fixture carries every backend's artifact — that would multiply ~150 cases into ~1000+
files with no proportional gain in what's actually being proven:

- **General/structural/semantic fixtures** (most of the suite): Postgres (the SQL reference
  dialect) + MongoDB + one shared Search artifact.
- **Dialect-specific fixtures**: exactly the one dialect whose mechanics the case exists to pin
  (MySQL's `ISNULL` null-ordering emulation, SQL Server's `OFFSET/FETCH` paging, SQLite as the
  cheap in-proc CI target).
- **Cross-backend parity showcase fixtures**: all five SQL dialects + Mongo + Search, reserved for
  the handful of cases whose entire point is proving identical semantics across every backend
  (SEM-1 case sensitivity, SEM-2 null handling) — these back T-3's property-test intent directly.

### Dialect support tiers

Not every dialect this suite grades is supported to the same level, and the fixture tree does not
distinguish them — a passing artifact means the same thing for all five. The difference is what runs
outside T-1a:

| Tier | Dialects | What that means |
|---|---|---|
| **v1 supported** (§5) | Postgres, SQL Server, MySQL/MariaDB, SQLite | Graded by T-1a; on the release path; execution-equality (T-1b, Feature #1624) will cover them. |
| **Community-tested** | **Oracle** (added #1642) | Graded by T-1a exactly like the four above — 39 arms across `dialect-parity/006`-`022` and all of `group-aggregation/` — but **no CI job runs it against a real Oracle instance and no release is blocked on it**. OQ-6 stays open. Its host obligations (12.2+, `MAX_STRING_SIZE = EXTENDED`, quoted DDL) are recorded in `dialect-parity/README.md`. |

An Oracle artifact disagreeing with a compiler is still a bug in one of the two, on the same terms as
any other arm; the tier is about *coverage beyond the artifact*, not about how seriously the artifact
is taken. Do not quietly promote it — nothing here has been executed.

## Deferred: `dataset.json` / `expectedRows.json` (execution-equality tier, T-1b)

Not populated in this pass. They require a real seeded database to derive expected row IDs from —
fabricating them without one would be inventing data, not capturing ground truth, which is worse
than leaving the field absent. They get added to these same fixture directories once integration
containers exist (a later Feature #1624 story) — this is additive, not a schema change.

## SQL artifact rendering — one canonical form, pinned in `spec/semantics.md`

Every `expected/*.artifact.json` for a SQL dialect is byte-compared under T-1a, so "correct SQL" is
not enough — there is exactly one accepted spelling per construct, and it is normative in
`spec/semantics.md`'s **"Addendum — canonical SQL rendering"** (SQL-0..SQL-7), not here. In short:
identifiers are **always quoted**, including the table and every alias (`SELECT "region" AS "region"
FROM "sales_fact"`); a composite `and`/`or` wraps itself and renders its children bare; a SEM-2
null-inclusive negative is always wrapped; `notContains` renders `NOT ILIKE`, not `NOT (... ILIKE
...)`; `notBlank` is literally `NOT (<blank>)` applied to whatever text that engine's `blank` produced
(**SQL-7**); and Postgres set membership is `= ANY($1)` /
`<> ALL($1)` against **one array parameter**. Three rules there are per dialect rather than universal
and are the ones a port is most likely to get silently wrong: **SQL-5** (the substring family's
operator, `ESCAPE` clause and escape alphabet all vary — MySQL spells `ESCAPE '\\'`, SQLite's
case-sensitive path is `GLOB` with no `ESCAPE` clause at all, SQL Server escapes `[` as well),
**SQL-6** (an engine whose default collation folds case must spell the case-**sensitive** mechanism
explicitly, not leave it bare) and **SQL-7** (an engine whose empty string *is* NULL — Oracle —
compiles `blank` on a text field to a bare `IS NULL`; emitting the `= ''` disjunct there makes
`notBlank` return no rows at all). Read that addendum before authoring or reviewing any SQL artifact.

**One cross-dialect divergence is known and deliberately unfixed**, because T-1a compares strings and
cannot grade it: a zero-width page window (`{"startRow": 100, "endRow": 100}`) yields an empty page on
Postgres/MySQL/SQLite and a **server error** on SQL Server. Recorded with its reasoning in
`dialect-parity/README.md`'s "Known divergences deferred to the execution-equality tier"; owned by
Feature #1624 (T-1b). No fixture exercises it — every fixture pages `0..100`.

This was not always so. Fixtures authored in #1630 disagreed with each other on all six of those
points (the suite was written by seven parallel agents with no compiler to check it). #1638 picked
one form per construct and re-authored the minorities — 144 Postgres artifacts gained quoting, and 9
had a rendering rewritten (`dialect-parity/008`, `/017`, `/018`, `/019`; `filter-tree/006`;
`operators/001`, `/004`, `/007`; `page-export/005`) — so the previously-recorded "cross-category
quoting contradiction" (`ag-grid-majors/README.md`) is **resolved for Postgres**.

Both arms of it have since closed — one in #1639, the other in #1640 and #1641 — and both are
recorded where the affected artifacts live rather than only here. **The register is therefore empty:
`grep -rn "Known open contradiction" spec/` finding nothing is the convention working, not a record
that went missing.** An artifact that disagrees with a compiler is a bug in one of the two, never an
expected divergence, with the single exception recorded in the paragraph above (the zero-width page
window, owned by #1624 at the execution tier).

- **CLOSED (#1639): the `mysql`/`mssql`/`sqlite` artifacts in `dialect-parity/` are now quoted too.**
  The *rule* was always universal; only the quote character (`"` vs `[…]` vs backticks) was each
  engine's own, and it is chosen when that engine's `Dialect` is written. #1639 wrote all three and
  re-authored the 19 pre-existing arms accordingly, then added 30 more (`011`-`020` promoted to full
  parity coverage) — plus a new `021` for the case-sensitive substring case its own coverage audit
  found missing and a new `022` for T-SQL's `[` wildcard, found by its code review: 55 non-Postgres
  arms in total, every one quoted, and each engine's escape for a quote character *inside* an
  identifier pinned with it (`]`→`]]`, `` ` ``→```` `` ````, `"`→`""`). See
  `dialect-parity/README.md`, which also records that story's five decisions (MySQL's `ESCAPE '\\'`,
  the per-dialect arm coverage policy, SQLite's `GLOB` for a case-sensitive substring match, SQL
  Server's explicit case-**sensitive** collation, and its `[` escaping) — now normative in
  `spec/semantics.md` as **SQL-5**/**SQL-6** — along with the host DDL obligations and the one known
  divergence deferred to T-1b (#1624).
- **CLOSED by #1642: the `blank`/`notBlank` composition was scoped to four dialects and Oracle
  falsified it.** `ISqlDialect` had recorded, since #1638, that `IS NULL`/`= ''` were pushed down to
  the query builder because "every v1 dialect spells them identically" — true of four engines and
  false of the fifth, where the empty string *is* NULL. It was not a redundant disjunct: `"c" = ''`
  is `"c" = NULL`, hence UNKNOWN for every row, so `notBlank` (which negates the whole composition)
  returned the **empty result set** on Oracle. Now normative as **SQL-7**; the composition stays
  builder-owned and only a `bool EmptyStringIsNull` engine fact varies inside it (the seam's third
  widening — see `dialect-parity/README.md`'s **D6**). `dialect-parity/019`'s oracle arm pins it and
  is the only artifact in the suite whose structure differs from its Postgres arm rather than only
  its tokens.
- **CLOSED by #1640 (BM-2) for the 16 grouped cases, and by #1641 (BM-3) for `016`-`019`.**
  Both splits turned on BM-2 design calls, and both are now normative in `spec/semantics.md`'s
  **grouping addendum (GRP-1..GRP-6)** rather than in a fixture README: a grouping request is
  **exact** on the protocol's own keys, so its `GROUP BY`, drill-down `WHERE` and a **grouped
  level's** `ORDER BY` render with no case-folding mechanism (GRP-2 — which is why those `ORDER BY`
  keys carry no `COLLATE craticula_ci` and the drill-down predicates lost theirs), and the drill-down
  **is** real IR, so SQL-2's wrap applies to it and `003`/`004`/`005` now read
  `("region" = $1 AND "segment" = $2)` (GRP-5). `ag-grid-majors/004`'s artifacts were already correct
  under GRP-2; its stated rationale was not, and #1640's review corrected it. The same story authored
  the missing `mssql`/`mysql`/`sqlite` arms for `group-aggregation/001`-`015` and `020`, so BM-2's
  "identical grouping protocol across all four dialects" is checkable rather than asserted.

  *Two corrections from #1640's own review, both recorded in `group-aggregation/README.md`.* GRP-2
  was **narrowed**: a fully-consumed drill-down **leaf**'s `ORDER BY` is not one of the protocol's
  keys (it has no `GROUP BY` and no buckets), so SEM-1 governs it and `005`/`006` collate again on
  the dialects that spell it. And the original register's entry (a) claimed all 20 of this category's
  artifacts; only 16 were settled, leaving `016`-`019` open for #1641.

  *#1641 closed those four, as **GRP-6**.* A distinct-values query **is** a bucket query: its
  `SELECT DISTINCT` column and that column's `ORDER BY` are exact, on GRP-2's own argument — the
  entries are handed straight back to the grid as `in` values or a `groupKey`, which a grouped query
  matches exactly, so a folded entry would match neither original row. The four artifacts already
  rendered that way and none was re-authored for it. GRP-6 also pins the three things around it that
  no artifact stated: the own-field exclusion (BM-3's defining semantic), the search prefix as an
  ordinary `startsWith` predicate governed by SEM-1/SEM-7, and the cap as
  `min(requested ?? default, maximum)` — clamped rather than rejected, and rendered as a row limit
  with no offset, which is the one place BM-3 had to widen the dialect seam. The same story authored
  those four cases' missing `mssql`/`mysql`/`sqlite` arms, so the whole category is now
  six-backend. Its review pass then added `group-aggregation/022`, because one of those three — the
  `WHERE` order when a filter model and a `searchPrefix` are both present — was `params`-visible and
  pinned by **no** artifact: `017` had filters and no prefix, `018` a prefix and no filters, so a port
  could emit the two conjuncts in either order and pass everything. `022` carries both.

## Case-insensitivity mechanism policy (SEM-1)

Two distinct mechanisms, never mixed: **`eq`/`neq`/`gt`/`gte`/`lt`/`lte`/`in`/`notIn`, plus sort,
use a case-insensitive COLLATION** (Postgres: the `craticula_ci` ICU collation; SQL Server/MySQL:
their native CI collations; Oracle: `COLLATE BINARY_CI`, which is also how it reaches the substring
family, having no `ILIKE`; Mongo: query-level `collation: {locale, strength:2}`; ES/OpenSearch:
`term`+`case_insensitive` for equality, a schema-declared `search.normalizedSubField` for ordering).
**`contains`/`notContains`/`startsWith`/`endsWith` use `ILIKE`/pattern-match** (SEM-7 owns the
substring mechanics and escaping). **Bucket-identity keys are the documented third case: they use
neither** — the `GROUP BY` key, the `groupKeys` drill-down match and a **grouped level's**
`ORDER BY` are all exact, per `spec/semantics.md`'s grouping addendum (**GRP-2**), because they must
agree with one another and a collated `GROUP BY` key is not valid SQL; BM-3's `SELECT DISTINCT`
column and its `ORDER BY` join them (**GRP-6**). That scope is narrow and every edge matters: a user
filter inside the same grouped request still follows the split above, so does a fully-consumed
drill-down **leaf**'s `ORDER BY`, which orders raw rows rather than buckets, and so does a set
filter's own search prefix, which filters the distinct column rather than being its key. Do not reach for `ILIKE`/`$regex` for an exact-match or
set-membership operator "because it's already case-insensitive that way" — it reintroduces an
escaping trap (a literal `%`/`_` in the value silently becomes a wildcard) that a collation doesn't
have. See `spec/semantics.md` SEM-1 for the full rationale.

**The case-SENSITIVE direction is a mechanism too, not the absence of one** (`spec/semantics.md`
**SQL-6**, added in #1639's review pass). Where an engine's own default collation folds case — SQL
Server's stock `SQL_Latin1_General_CP1_CI_AS`, MySQL's `_ci` server default — leaving a field
declared `caseSensitivity: "sensitive"` bare silently discards the declaration. SQL Server therefore
spells `COLLATE Latin1_General_CS_AS` (`dialect-parity/014`, `/018`, `/021`); Oracle spells
`COLLATE BINARY` in the same three, because its default collation resolves through the *session*'s
`NLS_COMP`/`NLS_SORT`; MySQL cannot express it in the statement at all and defers to a
`_bin`/`_as_cs` column collation in host DDL. Postgres and SQLite spell nothing, because their
defaults already distinguish case — four different answers that happen to share a spelling on two of
the five, which is why an artifact showing "no collation" says nothing on its own about which reason
applies.

## `agGridRequest.json` wire shape (grounded in `github/ag-grid`, the local reference clone)

Top level (`IServerSideGetRowsRequest`, `packages/ag-grid-community/src/interfaces/iServerSideDatasource.ts`):
```
{ startRow: number|null, endRow: number|null, rowGroupCols: ColumnVO[], valueCols: ColumnVO[],
  pivotCols: [], pivotMode: false, groupKeys: string[], filterModel: FilterModel|null,
  sortModel: SortModelItem[] }
```
- `ColumnVO` (`iColumnVO.ts`): `{ id, displayName, field?, aggFunc? }`.
- `SortModelItem` (`iSortModelItem.ts`): `{ colId, sort: "asc"|"desc" }` in v31-v34. **From v35 it also
  carries `type?: SortType` (`"absolute"|"default"`)** — optional in the declared type, but the emitter
  always populates it: v35's `sortService.ts` `collectSortItems` builds `{ sort, type }` with
  `_normalizeSortType()` returning `"default"` when unset, and v36's `_getSortModel` writes
  `{ sort, type, colId }`. So a real v35/v36 payload always has three keys. Nothing in the IR consumes
  it (it selects AG Grid's *client-side* comparator, not a server ordering), so an adapter reads only
  `colId`/`sort` and ignores it — see `ag-grid-majors/005` and `/006`, which pin exactly that.
  There is **no null-placement field** on the wire in any major; SEM-3's `nullsPosition` is always
  derived by the adapter.
- `filterModel` is a map keyed by `colId`. Per column, either a single condition or a **combined**
  form. Current form (`iSimpleFilter.ts`'s `ICombinedSimpleModel`): `{ filterType, operator:
  "AND"|"OR", conditions: [<condition>, <condition>] }`. **Legacy form** (pre-v29, AG-1 still
  requires parsing it): `{ filterType, operator: "AND"|"OR", condition1: <condition>, condition2:
  <condition> }` — same per-condition shape, just two named slots instead of an array.
  - Text condition (`iTextFilter.ts`): `{ filterType: "text", type: "equals"|"notEqual"|
    "contains"|"notContains"|"startsWith"|"endsWith"|"blank"|"notBlank", filter?: string,
    filterTo?: string }`.
  - Number condition (`iNumberFilter.ts`): `{ filterType: "number", type: "equals"|"notEqual"|
    "lessThan"|"lessThanOrEqual"|"greaterThan"|"greaterThanOrEqual"|"inRange"|"blank"|"notBlank",
    filter?: number, filterTo?: number }`.
  - Date condition (`iDateFilter.ts`): same `type` vocabulary as number, plus `dateFrom`/`dateTo`
    strings. **The emitted form is `"YYYY-MM-DD hh:mm:ss"`, not `"YYYY-MM-DD"`** — `createCondition`
    calls `_serialiseDate(value)` and `utils/date.ts` declares
    `_serialiseDate(date, includeTime = true, separator = '-')`, so v31-v35 always include the time
    part. The date-only form becomes possible only at v36, which threads an `includeTime` parameter
    through (`params.includeTime ?? dataTypeSvc.getDateIncludesTimeFlag(cellDataType) ?? true`), so it
    appears only for a column explicitly configured `includeTime: false` or `cellDataType: 'date'`;
    v36 can also emit `"YYYY-MM-DDThh:mm:ss"` under `useIsoSeparator: true`. A fixture using the
    date-only form must say which of those it is modelling.
    **Date presets** (AG-2) use a preset `type` (`"today"`, `"last7Days"`, ...). Their `dateFrom`/
    `dateTo` are **present and explicitly `null`**, not absent: `createCondition` returns
    `{ dateFrom: null, dateTo: null, filterType, type, ...model }` and a preset contributes nothing to
    `model` (it is in `simpleFilterUtils.ts`'s `zeroInputTypes`), so the real payload is
    `{"dateFrom":null,"dateTo":null,"filterType":"date","type":"last30Days"}`. AG Grid's own
    `PresetDateRangeFilterModel` doc comment says they are "always `undefined`" — that prose
    contradicts the emitter beside it; the fixtures follow the emitter. The preset
    vocabulary (`ISimpleFilterModelPresetType`) is closed and has 22 members, each resolving to a
    half-open `[from, to)` range published in AG Grid's own spec table — the adapter resolves it
    against an injected clock + zone, so no compiler ever sees an unresolved preset. See
    `date-presets/README.md` for the full table and the pinned-clock convention.
  - Set condition (`iSetFilter.ts`): `{ filterType: "set", values: string[]|null }` — maps to
    `in`/`notIn`.
  - Multi condition (`iMultiFilter.ts`): `{ filterType: "multi", filterModels: [<condition>, ...] }`
    — stacks several filter UIs on one column, combined with AND.
  - Boolean: AG Grid core has no dedicated boolean filter type — represent as a `set` condition
    with `values: ["true"]` / `["false"]` / `["true","false"]`; note this modeling choice in the
    fixture's `description.md`.
- **`customFilters` (OQ-11 extension, not part of AG Grid's own wire format):** an optional
  sibling field on the request, `{ customFilters: <filterNode> }` — a host-defined IR fragment
  merged into the filter tree by the adapter (see
  `docs/architecture/c4-components-core-ir.md`'s "Custom Filter Merge" component). This is the
  **only** way a fixture can exercise anything the AG Grid wire format itself cannot express: SEC-3
  **depth** adversarial cases (AG Grid's own `filterModel` is structurally shallow — a flat per-column
  map whose per-column condition count is bounded by `ISimpleFilterParams.maxNumConditions`, a
  documented grid option defaulting to `2`, so the native tree is at most `And(columns...)` over
  `And`/`Or(conditions...)`: three levels, never deeper, whatever `maxNumConditions` is set to),
  `inRange` boundary inclusivity (AG Grid's
  `inRange` carries no inclusivity flags at all — do not invent an inclusivity default and assert it
  as if it were the AG Grid default; use `customFilters` with explicit flags instead, per SEM-4's own
  "must NOT present the exact AG Grid boundary-inclusivity default as settled fact" rule), and a set
  filter's `notIn` direction (AG Grid's `SetFilterModel` carries only an inclusion list — there is no
  wire-level invert flag).
  - **Known gap (stated, not covered).** `max-depth-exceeded` is genuinely unreachable from a native
    `filterModel` — the shape caps out at three levels. `max-leaves-exceeded` is **not**: leaf count
    is `columns x maxNumConditions`, and a grid configured with a high `maxNumConditions` (or simply
    with many filtered columns) can breach the limit from a pure `filterModel` with no extension in
    sight. No adversarial fixture currently covers that path — every SEC-3 leaf-count case routes
    through `customFilters` — so the leaf limit is pinned only on the extension, not on the wire form
    a real grid emits. Authoring it is deliberately out of scope of #1637; recorded here so the gap is
    known rather than implied to be impossible.
- **`requestContext` (extension, not part of AG Grid's own wire format):** an optional sibling field,
  `{ requestContext: { zone: "<IANA id>", now: "<ISO-8601 instant>" } }`. Required on any fixture
  whose expected artifacts depend on a specific zone resolution or on a specific "current time" —
  without it, a port author must guess what the fixture author assumed.
  - `zone` carries the zone the adapter used to resolve any `datetime`-typed filter value in this
    request (SEM-4). An adapter **does** read this off the request: it takes precedence over whatever
    default zone the adapter was configured with, and an id it cannot resolve is a clean rejection,
    never a silent fallback.
  - `now` (added by AG-2's `date-presets/` category) pins the instant a **date preset** resolves
    against — preset resolution is clock-dependent, so a preset fixture is not reproducible without
    it. Unlike `zone`, this is an instruction **to the conformance harness**, not a field an adapter
    reads off the wire: the harness sets its injected clock to this instant before parsing. An
    implementation must not honour a client-supplied `now`, or any caller could redefine what
    "last 7 days" means. Absent on every fixture that does not use a preset.

## Categories

| Directory | Proves | Target count |
|---|---|---|
| `operators/` | AR-6's 17-operator closed set, across field types | ~45 |
| `date-presets/` | AG-2 date presets resolved to absolute half-open ranges (SEM-4 type + zone) | ~28 |
| `filter-tree/` | AR-5 And/Or/Not composition, nesting/depth variation | ~15 |
| `sort-paging/` | SEM-3 null ordering, SEM-6 stableKey tiebreaker, AR-9 | ~12 |
| `group-aggregation/` | BM-2 grouping, BM-3 set-filter values, AR-8 aggregation funcs | ~20 |
| `page-export/` | AG-3/EXP-2 page spec, EXP-1/EXP-6/EXP-8 export + hostile CSV values | ~20 |
| `adversarial/` | SEC-6: injection attempts, unknown fields, type confusion, depth bombs, oversized limits | ~20 |
| `dialect-parity/` | Dialect-specific mechanics + cross-backend parity showcases (SEM-1/2/3/7, SQL-5/6/7) | ~22 |
| `ag-grid-majors/` | AG-4: one SSRM payload per supported AG Grid major (v31-v36), plus the two rejections any major can emit (AG-5 Advanced Filter, SEM-4 native `inRange`) — **derived from each release tag's published types, not captured from a running grid** (see its README) | 8 |

Each category directory has its own `README.md` enumerating its cases and the specific requirement
each one backs.
