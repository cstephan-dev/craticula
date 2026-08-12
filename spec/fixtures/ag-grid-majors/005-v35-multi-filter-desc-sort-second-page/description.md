# 005 — AG Grid v35: a stacked multi filter, a descending sort, and a non-first page

**Derived from** tag `release-35.0.0` of the `github/ag-grid` reference clone (derived from published
types and emitting code, *not* captured from a running grid — see the category `README.md`):

- `packages/ag-grid-community/src/interfaces/iServerSideDatasource.ts` — `IServerSideGetRowsRequest`
  (`startRow`/`endRow` are `number | undefined`; AG Grid always sends both for a row-range request).
- `packages/ag-grid-community/src/interfaces/iMultiFilter.ts` — `IMultiFilterModel`
  (`{ filterType?: 'multi', filterModels: any[] | null }`), identical in all six majors.
- `packages/ag-grid-community/src/interfaces/iSetFilter.ts` — `SetFilterModel`.
- `packages/ag-grid-community/src/interfaces/iSortModelItem.ts` — `SortModelItem`, which at this tag
  is `{ colId, sort: NonNullable<SortDirection>, type?: SortType }`. **v35 is where `type` appears**
  (v31-v34 declare `{ colId, sort }` only), which is why this case carries it and 001-004 do not.
- `packages/ag-grid-community/src/sort/sortService.ts` — `collectSortItems`, the emitter, which shows
  `type` is optional only in the *declaration*: it builds `const type = _normalizeSortType(
  column.getSortDef()?.type); const sortItem = { sort, type }`, and `_normalizeSortType`
  (`entities/agColumn.ts`) returns `'default'` for anything invalid or unset. So a real v35 payload
  always carries three keys, and `"type": "default"` is what an unconfigured column produces.
  v36 does the same in `_getSortModel` (`model[i] = { sort: o.sort, type: o.type, colId }`).
- `packages/ag-grid-community/src/agStack/utils/aria.ts` — `SortType = 'absolute' | 'default'`.

**Requirement ids:** AG-4, AG-1, AG-3 (page spec from `startRow`/`endRow`), AR-6 (`startsWith`,
`in`), SEM-3 (null ordering), SEM-6 (`stableKey` tiebreaker).

## What this case pins

v35's filter model is byte-identical to v32/v33/v34's, but its **`sortModel` is not** — see below.
Beyond that, the content varies the three remaining axes of the request envelope that the other cases
hold constant:

- **A stacked `multi` filter.** AG Grid's Multi Filter puts several filter UIs on one column and ANDs
  them; each slot is a *complete child filter model* (it may itself be a combined form), and an
  inactive slot is JSON `null` rather than being omitted. Here the canonical pairing — a text filter
  stacked on a set filter, both on `department` — compiles to an `And` of two leaves on the same
  field.
- **A descending sort on a non-key column**, so SEM-3's default null placement flips to
  `NULLS FIRST`/`missing: "_first"`/bucket sign `-1`, and SEM-6's `stableKey` tiebreaker (`id ASC
  NULLS LAST`) is appended after it. Note the tiebreaker is *derived*, not sent: `SortModelItem` has
  no null-placement or tiebreaker field, so an implementation that omits either is not "matching AG
  Grid" — it is producing a non-deterministic page.
- **`SortModelItem.type`, the second real per-major wire difference** (the category `README.md` lists
  all three). The entry is `{"colId": "salary", "sort": "desc", "type": "default"}`, not the two-key
  form 001-004 carry: v35 added `type?: SortType`, and although the *type declaration* marks it
  optional, the *emitter* always populates it (`_normalizeSortType` defaults to `'default'`), so a key
  omitted here would be a payload v35 cannot produce. It is the AG-4 evidence this category exists to
  provide, in both directions:
  - The adapter must **not** choke on it. `AgGridRequestParser.ParseSortModel` reads `colId` and
    `sort` and nothing else from a `sortModel` entry, so `type` is ignored — this fixture and 006 are
    what pin that, and they are the only fixtures in the suite that do.
  - The adapter must **not** try to honour it either. `SortType` selects AG Grid's *client-side*
    comparator (`'absolute'` sorts by magnitude, ignoring sign); it has no server-side meaning, the IR
    has no slot for it, and `spec/semantics.md` defines no such ordering. Silently mapping it to
    something would invent a semantic AG Grid never asked for. If a host needs absolute-value
    ordering, that is a new IR concept, not a wire flag to smuggle through.
- **A non-first page**: `startRow: 200, endRow: 250` → `LIMIT 50 OFFSET 200` /
  `$skip: 200, $limit: 50` / `from: 200, size: 50`. `endRow` is exclusive, so the page size is
  `endRow - startRow`, never `endRow`.

## SEM-1 note

Both leaves target the same case-insensitive `string` column but use *different* mechanisms on
purpose: `startsWith` is a pattern match (`ILIKE ... ESCAPE`, `$regex`+`$options:"i"`, `wildcard`+
`case_insensitive`) and `in` is set membership (collation-based). Seeing both on one column in one
artifact is the clearest statement of the split.
