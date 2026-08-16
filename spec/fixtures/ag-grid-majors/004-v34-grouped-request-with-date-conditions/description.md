# 004 — AG Grid v34: a grouped + aggregated request envelope, with date `conditions`

**Derived from** tag `release-34.0.0` of the `github/ag-grid` reference clone (note the tag-naming
change: v31-v33 are tagged `vNN.0.0`, v34-v36 are tagged `release-NN.0.0` — see the category
`README.md`). Derived from published types and emitting code, *not* captured from a running grid:

- `packages/ag-grid-community/src/interfaces/iServerSideDatasource.ts` — `IServerSideGetRowsRequest`,
  the envelope this case is really about (`rowGroupCols`, `valueCols`, `groupKeys`, `pivotCols`,
  `pivotMode`).
- `packages/ag-grid-community/src/interfaces/iColumnVO.ts` — `ColumnVO`
  (`{ id, displayName, field?, aggFunc? }`), identical in all six majors.
- `packages/ag-grid-community/src/filter/provided/iSimpleFilter.ts` — `ICombinedSimpleModel`
  (`{ operator, conditions }`); v34 additionally adds the `isCombinedFilterModel()` type guard
  *beside* it, which is a source-level convenience and changes no wire shape.
- `packages/ag-grid-community/src/filter/provided/date/iDateFilter.ts` — `DateFilterModel`
  (`dateFrom`/`dateTo`, both `string | null | undefined`).
- `packages/ag-grid-community/src/filter/provided/date/dateFilter.ts` — `createCondition`, which
  serialises a bound with `_serialiseDate(values[0])`, and
  `packages/ag-grid-community/src/utils/date.ts` — `_serialiseDate(date, includeTime = true,
  separator = '-')`. Together these fix the emitted format at this tag; see below.

**Requirement ids:** AG-4, AG-1, BM-2 (grouping), AR-8 (`sum`), AR-5, AR-6 (`gte`, `lt`), SEM-3.

## What this case pins

The four majors 002-005 cover are shape-identical in their *filter model*, so this one varies the
part of the payload that has nothing to do with filters: the **request envelope**. It is the only
case in this category with a non-empty `rowGroupCols`/`valueCols`, which is what makes it worth
keeping distinct — those arrays are declared identically in every supported major, and this fixture
is what would fail if that ever stopped being true.

`groupKeys` is empty and `rowGroupCols` has one entry, so this is a **top-level grouped** query
(BM-2): the compiler groups by `department` and aggregates `SUM(salary)`, and no leaf row is
projected at all.

## The date bounds are `"YYYY-MM-DD hh:mm:ss"`, because v34 cannot emit anything else

`dateFrom` here is `"2018-01-01 00:00:00"`, not `"2018-01-01"`. At `release-34.0.0` that is not a
choice — it is the only form the grid produces:

```ts
// date/dateFilter.ts — createCondition(position)
if (values.length > 0) { model.dateFrom = _serialiseDate(values[0]); }
// utils/date.ts
export function _serialiseDate(date: Date | null, includeTime = true, separator = '-'): string | null
```

`createCondition` passes no `includeTime` argument, so the default `true` applies and the time part is
always appended. `iDateFilter.ts` at this tag says the same thing in prose — *"The type is `string`
and format is **always** `YYYY-MM-DD hh:mm:ss` e.g. 2019-05-24 00:00:00"* — so here, unlike the preset
case in 006, the documentation and the emitter agree. This holds for **v31-v35 alike**. The date-only
form becomes reachable only at v36,
which threads a parameter through
(`params.includeTime ?? dataTypeSvc?.getDateIncludesTimeFlag(params.colDef.cellDataType) ?? true`) —
and even there it still defaults to `true`, appearing only for a column explicitly configured
`includeTime: false` or `cellDataType: 'date'`. (v36 also gains `useIsoSeparator`, giving a
`"YYYY-MM-DDThh:mm:ss"` third form.) An earlier revision of this fixture used `"2018-01-01"`, which
no v34 grid emits.

**This is the fixture that caught a real parser bug.** Because AG Grid ships the time part by default,
the single most ordinary user action — typing a date into a date column's filter — reached the adapter
as `"2018-01-01 00:00:00"`, which was passed through verbatim as a `stringValue` and then rejected
downstream by `TypeCoercionValidator` (a `date` field accepts only canonical `YYYY-MM-DD`). Nothing
caught it because the parser's own tests stopped at `Parse`, which succeeded, and every pre-existing
date fixture used the date-only string. `AgGridRequestParser.ResolveDateWireValue` now decodes both
wire forms against the field's schema-declared type per SEM-4: a `date` field yields a `dateValue`
(rejecting a non-midnight time rather than truncating it), a `datetime` field a zone-resolved
`datetimeValue`. `hireDate` is declared `date` here, so `"2018-01-01 00:00:00"` resolves to the
`dateValue` `2018-01-01` — which is why the artifacts below still bind `"2018-01-01"` and did not
change. That resolution needs the field's type, so the conformance harness must supply this fixture's
own `schema.json` when parsing it (with no schema the adapter has no honest basis to type the string
and leaves it verbatim, unchanged from before).

## Notes on artifact construction

- **No `id`/`stableKey` tiebreaker.** Grouped (non-leaf) rows do not get one: the group column is
  itself unique per output row after `GROUP BY`, and the leaf-row `id` is not projected in a grouped
  result at all. This matches `group-aggregation/`'s convention; SEM-6's tiebreaker applies to the
  leaf-row cases (001, 002, 003, 005, 006 here).
- **Explicit projection.** A grouped query cannot be `SELECT *` like the other cases in this
  category, so this artifact carries a group-column + aggregate projection with the
  `<field>_<func>` aggregate naming `group-aggregation/` established. Identifier quoting stays
  unquoted, as everywhere else in this category.
- **Search `missing_bucket: true`** on the composite source: ES silently drops documents whose source
  field is null unless it is set, which would make the null `department` bucket disappear rather than
  sort somewhere — a completeness bug, not an ordering one.
- **Mongo carries no query-level `collation`**, and the artifact is the bare pipeline array. `groupKeys`
  is empty, so nothing here *matches* on the group column — there is no `$match` on `department` at
  all, only a `$group` on it. `group-aggregation/001-group-top-level-no-keys-consumed` (the exact
  structural analogue: empty `groupKeys`, one `rowGroupCols` entry on a case-insensitive string, one
  `sum` valueCol) and `/020-grouped-query-with-filter-and-sort` both emit the bare array too. An
  earlier revision of this artifact wrapped it, which would have made this case and
  `group-aggregation/020` mutually unsatisfiable under T-1a artifact equality — a query-level
  collation is not decoration, it changes `$group` bucket identity and would collapse `"Engineering"`
  and `"engineering"` into one row.

  *The artifact is right; the reason above used to be wrong, and this corrects it.* Until #1640's
  review this bullet ended by saying that when a *consumed* group key does produce a `$match` on a
  case-insensitive string "the wrapper is required (`group-aggregation/002` onwards)" — citing as
  proof the exact fixtures #1640 had just **stripped** it from. GRP-2 makes a consumed `groupKeys`
  drill-down match **exact**, so `002` onwards carry no wrapper either, and a grouping request never
  gets one on account of its own keys. The real distinction is narrower than "does anything match":
  the wrapper goes only where a **user's own** `$eq`/`$in` filter on a case-insensitive string needs
  it, and even then only in a request that is not grouped — because Mongo's `collation` is
  query-level, a grouped request cannot carry it without folding `$group`'s buckets too. GRP-2
  records that as an accepted divergence. Under either the old reason or the new one this case's
  artifact is the bare array, which is why nothing caught the drift: Mongo had no conformance runner
  until #1643, whose compiler now reproduces this artifact unchanged.
- **The Search artifact composes its cross-condition `And` as `bool.filter`**, where 001/003/005/006
  in this category use `bool.must`. Both conventions exist suite-wide (`filter-tree/` and `operators/`
  use `must`; `group-aggregation/` and `page-export/` use `filter`) and neither is wrong — `filter`
  is the scoring-free form, and for a `size: 0` aggregation query, where no document score is ever
  read, it is the accurate one. This case follows `group-aggregation/`, consistent with the rest of
  its borrowings above, and the category `README.md` records it alongside them.
- The date bounds compile as plain `dateValue` strings with no zone and no manufactured instant
  (SEM-4): a `date`-typed field never becomes an instant, in any backend. The wire form carries a
  midnight time (see above), which the adapter resolves away; nothing downstream of the adapter ever
  sees `"2018-01-01 00:00:00"`.
