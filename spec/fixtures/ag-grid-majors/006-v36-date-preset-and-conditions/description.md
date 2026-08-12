# 006 — AG Grid v36: a date **preset** alongside a modern combined filter

**Derived from** tag `release-36.0.0` of the `github/ag-grid` reference clone (derived from published
types and emitting code, *not* captured from a running grid — see the category `README.md`):

- `packages/ag-grid-community/src/interfaces/iServerSideDatasource.ts` — `IServerSideGetRowsRequest`.
- `packages/ag-grid-community/src/filter/provided/iSimpleFilter.ts` — `ISimpleFilterModelPresetType`
  (the closed 22-member preset vocabulary, **introduced in this major**) and `ISimpleFilterModelType`,
  which unions the presets into the same flat `type` slot as `equals`/`lessThan`/`blank`; plus
  `ICombinedSimpleModel` (`{ operator, conditions }`), unchanged since v32.
- `packages/ag-grid-community/src/filter/provided/date/dateFilter.ts` — `createCondition`, the
  emitter that actually mints the payload: `return { dateFrom: null, dateTo: null, filterType, type,
  ...model };`.
- `packages/ag-grid-community/src/filter/provided/simpleFilterUtils.ts` — `zeroInputTypes`, which
  contains every preset, so `getNumberOfInputs` returns 0, `getValues` yields nothing and `model`
  above stays empty.
- `packages/ag-grid-community/src/filter/provided/date/iDateFilter.ts` — `DateFilterModel` and
  `PresetDateRangeFilterModel`. **Read with care: its doc comment contradicts the emitter above** —
  see "The `dateFrom`/`dateTo` nulls" below.
- `packages/ag-grid-community/src/interfaces/iSortModelItem.ts` +
  `packages/ag-grid-community/src/sort/sortService.ts` — `SortModelItem` gained `type?: SortType`
  at v35, and v36's `_getSortModel` emits `{ sort: o.sort, type: o.type, colId }`, so this payload's
  sort entry carries `"type": "default"` like 005's. `005/description.md` covers why the adapter
  ignores it rather than mapping it.
- `packages/ag-grid-community/src/filter/provided/date/dateFilterHandler.ts` — the published `@Spec`
  resolution table, every row of which is half-open `[from, to)`.

**Requirement ids:** AG-4, AG-2 (preset resolution), AG-1, AR-5, AR-6 (`inRange`, `eq`), SEM-4
(type + zone), SEM-1.

## What is v36-only here

**The preset.** `ISimpleFilterModelPresetType` does not exist in v31-v35: a payload from those majors
cannot carry `{"filterType": "date", "type": "last30Days"}` at all, which is why this is the only
case in this category with one. Everything else in the payload — the envelope, the
`{ operator, conditions }` combined form on `status` — is what v32-v35 emit too.

A preset arrives as a `type` with no bound of its own (presets are members of AG Grid's own
`zeroInputTypes`), so the **adapter** resolves it into an absolute range before any compiler sees it.
`date-presets/` owns the full 22-row table and its rationale; this case exists to prove a preset
composes normally with the rest of a real request, not to re-derive the table.

## The `dateFrom`/`dateTo` nulls — AG Grid's prose contradicts AG Grid's emitter

The preset entry here is `{"dateFrom": null, "dateTo": null, "filterType": "date", "type":
"last30Days"}` — **four keys, two of them explicitly `null`**, not the two-key form you would write
from the documentation. That is not a stylistic choice; it is what `release-36.0.0` emits:

```ts
// date/dateFilter.ts — createCondition(position)
const model: Partial<DateFilterModel> = {};
const values = this.getValues(position);          // [] for a preset: it is in zeroInputTypes
if (values.length > 0) { model.dateFrom = _serialiseDate(values[0], ...); }
if (values.length > 1) { model.dateTo   = _serialiseDate(values[1], ...); }
return { dateFrom: null, dateTo: null, filterType, type, ...model };
```

With `values` empty, `model` is `{}`, so the two `null` defaults are not overwritten and go out on the
wire. Meanwhile `iDateFilter.ts` declares, three files away:

```ts
/**
 * Date filter model used when a built-in preset range (for example, Today or Last 7 Days) is selected.
 * `dateFrom` and `dateTo` are always `undefined` and the `type` carries the preset key.
 */
export interface PresetDateRangeFilterModel extends DateFilterModel {
    type: ISimpleFilterModelPresetType;
    dateFrom: undefined;
    dateTo: undefined;
}
```

Both cannot be true of the same payload, and the emitter is the one that runs. **This fixture follows
the emitter, deliberately** — an earlier revision of it followed the prose and was wrong. AG Grid's
SSRM documentation repeats the same `undefined` claim, so a port author has two independent reasons to
get this wrong and no third one to notice.

Why it matters to a port even though the values are null either way: key *presence* is what a strict
reader keys off. A schema with `additionalProperties: false` and no `dateFrom`/`dateTo` on the preset
variant rejects every real preset payload; a `'dateFrom' in model` / `hasOwnProperty` /
`TryGetProperty` check to distinguish "preset" from "explicit range" reads `true` on a preset and
picks the wrong branch. The robust test is the one this adapter uses — dispatch on `type` being a
member of the closed preset vocabulary, before looking at any bound at all — which is insensitive to
which of the two forms it is handed. The same two keys are now present on all 28 `date-presets/`
fixtures, so nothing in the suite still models the documented-but-unemitted shape.

## The pinned clock

`requestContext` carries both extension fields (`../README.md`): `zone: "UTC"` and
`now: "2026-08-12T14:37:00Z"` — the same instant `date-presets/001`-`022` pin, deliberately boring on
every axis (a Wednesday, mid-month, mid-quarter, far from any year boundary, mid-afternoon UTC). So
`last30Days` resolves exactly as `date-presets/018` does: `[2026-07-13, 2026-08-13)`.

`now` is an instruction to the conformance harness (it sets its injected clock to that instant), not
a field an adapter may read off the wire — honouring a client-supplied `now` would let any caller
redefine what "last 30 days" means. `zone`, by contrast, *is* read from the request.

`fromInclusive: true` / `toInclusive: false` on the resolved node is read from AG Grid's own
published spec table, not guessed — the distinction from a *native* `inRange` (which carries no
inclusivity signal at all and is rejected outright) is spelled out in `date-presets/README.md`.

## Compiled shape

Cross-column `And`: the resolved `inRange` on `hireDate`, and `status`'s own `Or`. `hireDate` is
declared `date`, so both bounds are zone-less `dateValue`s (SEM-4: no instant is manufactured, and no
artifact carries a zone clause). `status` is case-insensitive, so its two equalities use SEM-1's
collation mechanism.
