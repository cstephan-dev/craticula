# `ag-grid-majors/` — one SSRM payload per supported AG Grid major (v31-v36)

Per `spec/fixtures/README.md`'s category index, this directory backs **AG-4**: the adapter must parse
the SSRM request shape of every AG Grid major in the supported range v31-v36. One case pins each
major, plus two shared rejection cases for wire shapes any major can produce.

## Provenance: these payloads are **DERIVED, not captured**

**Read this before treating anything here as recorded ground truth.** Every `agGridRequest.json` in
this directory was *constructed* from the corresponding release tag's own published TypeScript
interfaces and model-emitting code in the vendored `github/ag-grid` clone. **None of them was
recorded from a running grid**, because no browser is available in the environment these fixtures
were authored in. Each case's `description.md` names the exact tag and source file(s) its shape was
derived from, so anyone can re-check a claim at its source rather than trusting the fixture.

Why derivation is legitimate *for a payload shape*: the shape is fully determined by the version's
own published interfaces (`IServerSideGetRowsRequest`, `ICombinedSimpleModel`, `SetFilterModel`, ...)
— the very artifact the grid itself compiles against — plus the code path that mints the model
(`SimpleFilter.getModelFromUi()`). Both are available at every tag, and both are read here rather
than recalled. A claim of the form "v31 emits these keys" is checkable against the same source that
determines it.

Why that reasoning does **not** extend to everything: this suite deliberately leaves
`dataset.json`/`expectedRows.json` empty (`../README.md`, "Deferred"), because expected *rows*
require a real seeded database and are genuinely unknowable from source — "fabricating them without
one would be inventing data, not capturing ground truth, which is worse than leaving the field
absent". A shape derived from published interfaces and an outcome derived from an imagined database
are different kinds of claim with different standards of evidence. Deriving the first is sound;
deriving the second would be fabrication. The distinction is recorded here rather than left implied,
so nobody has to reconstruct the reasoning later.

**What would upgrade these fixtures:** replaying each `agGridRequest.json` against a real grid of
that major and diffing. That is additive — it would confirm or correct these files without changing
the category's structure.

## Tag naming in the reference clone (it changes mid-range)

A port author re-deriving these will hit this immediately:

| Major | Tag in `github/ag-grid` | Community core source root |
|---|---|---|
| v31 | `v31.0.0` | `grid-community-modules/core/src/ts/` |
| v32 | `v32.0.0` | `community-modules/core/src/` |
| v33 | `v33.0.0` | `packages/ag-grid-community/src/` |
| v34 | `release-34.0.0` | `packages/ag-grid-community/src/` |
| v35 | `release-35.0.0` | `packages/ag-grid-community/src/` |
| v36 | `release-36.0.0` | `packages/ag-grid-community/src/` |

The tag prefix changes from `vNN.0.0` to `release-NN.0.0` at v34, and the source root moves twice
(at v32 and again at v33). Read a file at a tag with `git show <tag>:<path>` — the paths in one
major's `description.md` do not resolve at another's tag.

## What actually differs across the six majors

Verified by reading each tag, not inferred. There are **three** real wire differences across the
range, and each is pinned by the case for the major that introduced it:

| # | What changed | Introduced at | Pinned by |
|---|---|---|---|
| 1 | `ICombinedSimpleModel` lost its legacy `condition1`/`condition2` pair | v32 (present v31 only) | 001 |
| 2 | `SortModelItem` gained `type` | v35 | 005 (and 006) |
| 3 | Date presets in the `type` slot | v36 | 006 |

(A fourth candidate is *not* on this list: `createCondition`'s `dateFrom: null, dateTo: null` defaults
are emitted by **every** major, so 004's `"dateTo": null` and 006's preset nulls are the same
long-standing behaviour, not a v36 change. It is called out under difference 3 only because AG Grid's
own preset documentation denies it.)

- **The request envelope is identical in all six.** `IServerSideGetRowsRequest`
  (`interfaces/iServerSideDatasource.ts`) is byte-identical across v31-v36: `startRow`/`endRow`,
  `rowGroupCols`, `valueCols`, `pivotCols`, `pivotMode`, `groupKeys`, `filterModel`, `sortModel`. So
  are `ColumnVO`, `SetFilterModel`, `IMultiFilterModel` and the per-condition `ISimpleFilterModel`
  base. (`SortModelItem` is *not* on this list — see difference 2.)
- **Difference 1 — `ICombinedSimpleModel`**, see case 001. v31 declares `operator` + **required**
  `condition1`/`condition2` + *optional* `conditions?`, and emits **all three** together; v32 removed
  the legacy pair, leaving `{ operator, conditions }`, which v33-v36 keep unchanged.
- **Difference 2 — `SortModelItem.type`**, see case 005. v31-v34 declare `{ colId, sort }`;
  `release-35.0.0`'s `interfaces/iSortModelItem.ts` adds `type?: SortType`
  (`SortType = 'absolute' | 'default'`). The `?` is misleading: the emitter always populates it. v35's
  `sort/sortService.ts` `collectSortItems` builds `{ sort, type }` with
  `_normalizeSortType(column.getSortDef()?.type)`, which returns `'default'` for anything unset, and
  v36's `_getSortModel` writes `{ sort: o.sort, type: o.type, colId }`. So every v35/v36 `sortModel`
  entry has three keys, and 005/006 carry `"type": "default"` while 001-004 carry the two-key form.
  Nothing in the IR consumes it — `SortType` selects AG Grid's client-side comparator, not a server
  ordering — so `AgGridRequestParser.ParseSortModel` reads only `colId` and `sort` and ignores the
  rest of the entry. **That is the AG-4 evidence these two cases exist to provide**: a v35+ payload
  must parse unchanged, and the extra key must be neither a rejection nor a semantic the adapter
  invents. No other fixture in the suite carries the three-key form.
- **Difference 3 — date presets, v36-only.** `ISimpleFilterModelPresetType` (`today`, `last30Days`,
  ... — a closed 22-member vocabulary) was introduced in `release-36.0.0` and does not exist in
  v31-v35. That is why only case 006 carries a preset: a v31-v35 payload containing one would not be a
  payload that major can produce. Case 006 also pins the shape a preset really arrives in —
  `dateFrom`/`dateTo` **present and `null`**, not absent, because a preset is zero-input so
  `createCondition`'s nulls are never overwritten. AG Grid's own `PresetDateRangeFilterModel` doc
  comment and SSRM docs both say `undefined`; the emitter beside them says otherwise and wins. See
  its `description.md` — the discrepancy is itself a finding a port author needs.
- **`filterModel: FilterModel | AdvancedFilterModel | null` in every major**, so AG-5's Advanced
  Filter rejection is in scope for all six, not just recent ones — hence case 007 is shared rather
  than repeated per major.
- **The date-bound wire format is identical in v31-v35 and *nearly* so at v36.** `createCondition`
  calls `_serialiseDate(value)` and `_serialiseDate(date, includeTime = true, separator = '-')`
  defaults the time part in, so `"YYYY-MM-DD hh:mm:ss"` is what every one of these majors emits.
  v36 threads an `includeTime`/`useIsoSeparator` pair through, but still defaults `includeTime` to
  `true` — the date-only and `T`-separated forms need explicit column configuration, so they are a
  *configuration* variation rather than a per-major one and no case here claims them. Case 004 carries
  the real form; see its `description.md` for the parser bug that fixture exposed.

## Cases 002-004 are shape-identical to one another. That is a finding, not a gap.

v32, v33 and v34 emit the *same* SSRM wire shape; there is no per-major divergence to pin between v32
dropping the legacy condition pair and v35 adding `SortModelItem.type`. (005 is *nearly* a fourth copy
— its `filterModel` shape is identical too, and only its `sortModel` differs.) Fixtures that differ
only in which major they claim to represent would be copies. They are kept anyway, and their content
is varied, for two distinct reasons:

1. **Each pins its own major.** The value of a conformance fixture is that it fails when reality
   changes. If v34 had (or later gains) a divergence, the case labelled v34 is what fails, and its
   `description.md` points straight at that tag's source files. Collapsing them into one "v32-v34"
   case would trade that precision for two saved files — and would have hidden difference 2, which
   only became visible because a case labelled v35 existed to be checked against `release-35.0.0`.
2. **Varied content broadens real coverage.** Since the shape is fixed, the *content* is not: 002 is a
   numeric range on a `decimal` field, 003 pairs pattern-matching text conditions with a set filter
   across two columns, 004 exercises the grouping/aggregation envelope, 005 stacks a `multi` filter
   under a descending sort on a non-first page. The set is four different requests, not one request
   four times.

Do not "deduplicate" 002-005 into a single case. The redundancy is the point being recorded.

## Backend coverage

General/structural fixtures per the suite-wide policy (`../README.md`): `postgres` + `mongo` +
`search` on each of the six valid cases. Cases 007 and 008 are rejection-shaped and carry
`expected/rejection.json` **instead of** any backend artifact.

## Cases

| # | Directory | Tag derived from | Pins |
|---|---|---|---|
| 001 | `001-v31-combined-filter-dual-emission/` | `v31.0.0` | v31's dual emission: `operator` + `condition1` + `condition2` + `conditions` **all present at once**. Three conditions, so reading the legacy pair drops one and reading both double-counts two — only reading `conditions` matches the artifacts. |
| 002 | `002-v32-modern-conditions-form/` | `v32.0.0` | The post-legacy `{ operator, conditions }` form (v32 removed `condition1`/`condition2`). Content: an AND'd numeric range on a `decimal` field, expressed as two simple conditions rather than a native `inRange`. |
| 003 | `003-v33-text-conditions-and-set-filter/` | `v33.0.0` | Same shape; content covers pattern-matching text operators (`startsWith`/`contains`) OR'd within one column, AND'd across columns with a `set` filter — both SEM-1 mechanisms in one artifact. |
| 004 | `004-v34-grouped-request-with-date-conditions/` | `release-34.0.0` | Same shape; content is the **request envelope**: non-empty `rowGroupCols`/`valueCols` (BM-2 top-level grouping + AR-8 `sum`) with date conditions and an empty `sortModel`. Also the only case carrying AG Grid's real `"YYYY-MM-DD hh:mm:ss"` date-bound form — the one that exposed the `ResolveDateWireValue` bug. |
| 005 | `005-v35-multi-filter-desc-sort-second-page/` | `release-35.0.0` | **Difference 2** — `SortModelItem.type`, new at v35 and always emitted. Content: a stacked `multi` filter (text + set on one column), a descending sort on a non-key column (SEM-3 null flip + SEM-6 tiebreaker), and a non-first page (`startRow: 200`). |
| 006 | `006-v36-date-preset-and-conditions/` | `release-36.0.0` | **Difference 3** — the v36-only date **preset** (`last30Days`) resolved by the adapter into an absolute half-open range, with the `dateFrom`/`dateTo` nulls a real preset carries, alongside a modern combined filter on another column. Also carries difference 2's `SortModelItem.type`. Carries `requestContext` with `zone` + pinned `now`. |
| 007 | `007-advanced-filter-model-rejected/` | all six (shape read at `release-36.0.0`) | AG-5: a real `JoinAdvancedFilterModel` root on `filterModel` → one clean `requestParsing` / `malformed-request` rejection, never a partial parse. |
| 008 | `008-native-inrange-rejected/` | all six (shape read at `release-34.0.0`) | SEM-4: a stock `"type": "inRange"` date condition — what a grid emits the moment a user picks "In range" — → `requestParsing` / `unknown-operator`, because AG Grid ships no boundary-inclusivity signal and the adapter must not invent one. |

## Notes on artifact construction

- Cases 001, 002, 003, 005, 006 are ordinary leaf-row queries: `SELECT * FROM "employees"`, SEM-6's
  `id` tiebreaker always appended, Mongo's mandatory null-bucket `$addFields`/`$sort`/`$unset` trio,
  Search `sort[].missing`. Range paging is `LIMIT (endRow - startRow) OFFSET startRow` /
  `$skip`/`$limit` / `from`/`size`.
- Case 004 is grouped, so it follows `group-aggregation/`'s structural conventions instead. The full
  list of what it borrows, so none of it looks accidental:
  - an explicit group-column + aggregate projection (a grouped query cannot be `SELECT *`);
  - the `<field>_<func>` aggregate naming;
  - **no** `id`/`stableKey` tiebreaker (the group column already is one, and no leaf `id` is
    projected), i.e. SEM-6's tiebreaker is deliberately dropped for grouped output;
  - a Mongo null bucket on the post-`$group` `_id` rather than on a pre-group field;
  - `missing_bucket: true` on the Search composite source;
  - **the Search `bool.filter` composition** for its cross-condition `And`, where 001/003/005/006 in
    this category use `bool.must`. Both conventions exist suite-wide — `filter-tree/` and `operators/`
    use `must` (13 artifacts), `group-aggregation/` and `page-export/` use `filter` (18) — and neither
    is wrong; `filter` is the scoring-free form, which is the accurate one for a `size: 0` aggregation
    query where no document score is ever read. Following `group-aggregation/` here is consistent with
    everything above it in this list, but it does mean this category is internally split, so it is
    written down rather than left for a reader to notice.
  - **and** `group-aggregation/`'s identifier quoting — see the next bullet; this used to be the one
    place case 004 knowingly departed from its reference category, and no longer is.
- **RESOLVED (#1638): identifier quoting on a grouped projection.** This bullet used to record an
  open contradiction: case 004 rendered `SELECT department AS department, SUM(salary) AS salary_sum
  ...` while `group-aggregation/001`, the *same* IR construct, rendered `SELECT "region" AS "region",
  SUM("amount") AS "amount_sum" ...`. Unquoted was kept here at the time because it was the suite
  majority (144 of 164 Postgres artifacts, with all 20 quoted ones in `group-aggregation/`) and
  switching one case would have split this category instead of unsplitting the suite.
  #1638 unsplit the suite the other way: **always quote** is now normative
  (`spec/semantics.md`, SQL-1), the 144 unquoted artifacts were re-authored, and case 004 now reads
  `SELECT "department" AS "department", SUM("salary") AS "salary_sum" FROM "employees" ...`. The
  deciding argument was not the head-count but a latent execution-tier bug the artifact tier cannot
  see: `dialect-parity/` declares camelCase physical columns that unquoted PostgreSQL folds to
  lowercase, so those queries would have failed against a real table at T-1b (#1624). A
  quoting-only mismatch is therefore now a **port bug**, not a suite defect. Kept as a record of why
  the answer is what it is.

  *Scope of "resolved", so nobody reads this bullet as "the suite is now internally consistent".* It
  resolves the quoting split, on **Postgres**, and nothing more. Two registers of open contradictions
  remain, both scheduled and both recorded where their artifacts live: `dialect-parity/`'s 19
  `mysql`/`mssql`/`sqlite` artifacts are still unquoted pending #1639's choice of each engine's quote
  character (`dialect-parity/README.md`), and `group-aggregation/` still splits from the rest of the
  suite on grouped `ORDER BY` collation and on the unwrapped drill-down conjunction, both pending
  BM-2 decisions (`group-aggregation/README.md`, "Known open contradictions"). **Case 004 is inside
  the first of those two today**: its `ORDER BY "department" ASC NULLS LAST` groups and sorts on a
  case-insensitive `string` column with no `COLLATE craticula_ci`, exactly as `group-aggregation/`'s
  20 do and unlike every leaf artifact in the suite. It is the only grouped artifact outside that
  category, so #1640 must re-author it in the same pass.
- The Mongo query-level `collation` wrapper appears only where SEM-1's collation mechanism is
  actually used (`$eq`/`$in`/group-key **matching** on a case-insensitive `string`). Cases whose only
  string mechanism is a pattern match keep their own `$options: "i"` and need no wrapper — a
  query-level collation does not apply to `$regex`. Note "matching": case 004 *groups* by a
  case-insensitive column but has empty `groupKeys`, so nothing matches on it and its artifact is the
  bare pipeline array, exactly like `group-aggregation/001` and `/020`. The wrapper is not free —
  a query-level collation changes `$group` bucket identity and would fold `"Engineering"` and
  `"engineering"` into one row — so it goes only where a `$match` needs it.
- No `customFilters` anywhere in this category. Every case is a payload a real grid of that major can
  emit on its own; reaching for the extension would defeat the point. That is also why 008 exists:
  `operators/` reaches `inRange` *through* `customFilters`, so nothing was pinning what the native
  wire form does.
