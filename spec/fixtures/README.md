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
    sqlite.artifact.json
    mongo.artifact.json
    search.artifact.json     — shared OpenSearch/Elasticsearch artifact (OQ-7: one compiler, one DSL subset)
  dataset.json?             — optional, execution-equality tier (T-1b) — see "Deferred" below
  expectedRows.json?        — optional, execution-equality tier (T-1b) — see "Deferred" below
```

Every `expected/<backend>.artifact.json` validates against its shape schema in `schemas/`
(`sql-artifact.schema.json` for the four SQL dialects, `mongo-artifact.schema.json`,
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
- **Cross-backend parity showcase fixtures**: all four SQL dialects + Mongo + Search, reserved for
  the handful of cases whose entire point is proving identical semantics across every backend
  (SEM-1 case sensitivity, SEM-2 null handling) — these back T-3's property-test intent directly.

## Deferred: `dataset.json` / `expectedRows.json` (execution-equality tier, T-1b)

Not populated in this pass. They require a real seeded database to derive expected row IDs from —
fabricating them without one would be inventing data, not capturing ground truth, which is worse
than leaving the field absent. They get added to these same fixture directories once integration
containers exist (a later Feature #1624 story) — this is additive, not a schema change.

## SQL artifact rendering — one canonical form, pinned in `spec/semantics.md`

Every `expected/*.artifact.json` for a SQL dialect is byte-compared under T-1a, so "correct SQL" is
not enough — there is exactly one accepted spelling per construct, and it is normative in
`spec/semantics.md`'s **"Addendum — canonical SQL rendering"** (SQL-0..SQL-6), not here. In short:
identifiers are **always quoted**, including the table and every alias (`SELECT "region" AS "region"
FROM "sales_fact"`); a composite `and`/`or` wraps itself and renders its children bare; a SEM-2
null-inclusive negative is always wrapped; `notContains` renders `NOT ILIKE`, not `NOT (... ILIKE
...)`; `notBlank` is literally `NOT (<blank>)`; and Postgres set membership is `= ANY($1)` /
`<> ALL($1)` against **one array parameter**. Two rules there are per dialect rather than universal
and are the ones a port is most likely to get silently wrong: **SQL-5** (the substring family's
operator, `ESCAPE` clause and escape alphabet all vary — MySQL spells `ESCAPE '\\'`, SQLite's
case-sensitive path is `GLOB` with no `ESCAPE` clause at all, SQL Server escapes `[` as well) and
**SQL-6** (an engine whose default collation folds case must spell the case-**sensitive** mechanism
explicitly, not leave it bare). Read that addendum before authoring or reviewing any SQL artifact.

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

One arm of it is deliberately still open; the other closed in #1639. Both are recorded where the
affected artifacts live rather than only here:

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
- **STILL OPEN — two splits survive on the grouped artifacts** — every grouped `ORDER BY` key omits
  `COLLATE craticula_ci` on a case-insensitive `string` column (all 20 of `group-aggregation/`, plus
  `ag-grid-majors/004`), and `group-aggregation/003`/`004`/`005` render their drill-down conjunction
  unwrapped. Both are **#1640**'s to settle, because both turn on BM-2 design calls (bucket identity
  under a non-deterministic collation; whether the drill-down is a real IR `andNode`). Registered in
  `group-aggregation/README.md`'s "Known open contradictions" section; a port whose grouped output
  disagrees with those artifacts should read it before assuming its own bug.

## Case-insensitivity mechanism policy (SEM-1)

Two distinct mechanisms, never mixed: **`eq`/`neq`/`gt`/`gte`/`lt`/`lte`/`in`/`notIn`, plus sort and
group-key matching, use a case-insensitive COLLATION** (Postgres: the `craticula_ci` ICU collation;
SQL Server/MySQL: their native CI collations; Mongo: query-level `collation: {locale, strength:2}`;
ES/OpenSearch: `term`+`case_insensitive` for equality, a schema-declared `search.normalizedSubField`
for ordering). **`contains`/`notContains`/`startsWith`/`endsWith` use `ILIKE`/pattern-match** (SEM-7
owns the substring mechanics and escaping). Do not reach for `ILIKE`/`$regex` for an exact-match or
set-membership operator "because it's already case-insensitive that way" — it reintroduces an
escaping trap (a literal `%`/`_` in the value silently becomes a wildcard) that a collation doesn't
have. See `spec/semantics.md` SEM-1 for the full rationale.

**The case-SENSITIVE direction is a mechanism too, not the absence of one** (`spec/semantics.md`
**SQL-6**, added in #1639's review pass). Where an engine's own default collation folds case — SQL
Server's stock `SQL_Latin1_General_CP1_CI_AS`, MySQL's `_ci` server default — leaving a field
declared `caseSensitivity: "sensitive"` bare silently discards the declaration. SQL Server therefore
spells `COLLATE Latin1_General_CS_AS` (`dialect-parity/014`, `/018`, `/021`); MySQL cannot express it
in the statement at all and defers to a `_bin`/`_as_cs` column collation in host DDL. Postgres and
SQLite spell nothing, because their defaults already distinguish case — three different answers that
happen to share a spelling on two of the four, which is why an artifact showing "no collation" says
nothing on its own about which reason applies.

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
| `dialect-parity/` | Dialect-specific mechanics + cross-backend parity showcases (SEM-1/2/3/7) | ~22 |
| `ag-grid-majors/` | AG-4: one SSRM payload per supported AG Grid major (v31-v36), plus the two rejections any major can emit (AG-5 Advanced Filter, SEM-4 native `inRange`) — **derived from each release tag's published types, not captured from a running grid** (see its README) | 8 |

Each category directory has its own `README.md` enumerating its cases and the specific requirement
each one backs.
