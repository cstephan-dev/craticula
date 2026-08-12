# 008 — A native `inRange` condition is rejected, in every major

**Derived from** the `github/ag-grid` reference clone. The shape below was read at tag
`release-34.0.0` from
`packages/ag-grid-community/src/filter/provided/date/iDateFilter.ts` (`DateFilterModel`) and
`packages/ag-grid-community/src/filter/provided/date/dateFilter.ts` (`createCondition`), with
`packages/ag-grid-community/src/filter/provided/simpleFilterUtils.ts`'s `getNumberOfInputs`
confirming `inRange` is the one two-input type. The same declarations exist at every supported tag
(`v31.0.0` and `v32.0.0` carry them under `grid-community-modules/core/src/ts/filter/provided/date/`
and `community-modules/core/src/filter/provided/date/` respectively). Derived from published types,
*not* captured from a running grid — see the category `README.md`.

**Requirement ids:** SEM-4 (boundary inclusivity must never be guessed), AG-4, AG-1, AR-6, SEC-6
(clean rejection).

## Why this case exists

Until this fixture, **nothing in the suite sent `"type": "inRange"` on the native wire.** Every
`inRange` case in `operators/` (039-043) reaches the operator through the `customFilters` extension,
precisely because the native form cannot express inclusivity — which is correct, but it left the
native path itself untested. A grep of every `agGridRequest.json` found no `"type": "inRange"`
anywhere, so the deliberate rejection in `AgGridRequestParser.ParseDateCondition` /
`ParseNumberCondition` was load-bearing behaviour that no fixture would have noticed the loss of. A
future change that "fixed" the rejection by inventing a default would have gone in green.

This is not an exotic payload. `inRange` is a stock option in AG Grid's number and date filter UIs,
and this is the payload a grid emits the *moment a user picks "In range"* — very likely the most
common single request this adapter refuses. Refusing it needs to be a recorded decision with a
recorded reason, not an accident.

## The shape, and what is missing from it

```ts
// iDateFilter.ts (release-34.0.0) — the whole model
export interface DateFilterModel extends ISimpleFilterModel {
    filterType?: 'date';
    dateFrom: string | null;   // "Range filter has two values (from and to)."
    dateTo: string | null;
}
```

Two bounds, and nothing else. `iNumberFilter.ts`'s `NumberFilterModel` is the same story with
`filter`/`filterTo`. **There is no `fromInclusive`/`toInclusive`, no `boundaries`, no inclusivity
hint of any kind — in any of the six supported majors.** Four different user intents (`[a,b]`,
`[a,b)`, `(a,b]`, `(a,b)`) serialise to byte-identical JSON.

The bounds here use AG Grid's real `"YYYY-MM-DD hh:mm:ss"` serialisation (see `004/description.md`
for why that, and not `"YYYY-MM-DD"`, is what v31-v35 emit). They are deliberately present and
well-formed: this case must fail on the *operator*, not on a malformed value, or it would prove
nothing about `inRange`.

## Which validation stage must catch it, and why not guess instead

**Request parsing** (`requestParsing` / `unknown-operator` — pinned by this case's own
`expected/rejection.json`). Note the reason is `unknown-operator`, not `malformed-request`: the
request is perfectly well-formed AG Grid, and `inRange` is a real member of AG Grid's vocabulary. What
the adapter cannot do is *map* it into the IR, because `rangeComparisonNode` requires
`fromInclusive`/`toInclusive` and the wire supplies neither. The rejection is about an operator this
adapter cannot honestly express, which is what that reason code means.

SEM-4 states the rule directly: an implementation "must NOT present the exact AG Grid
boundary-inclusivity default as settled fact". Picking one silently would be worse than the
rejection, because the error is invisible: an off-by-one at the boundary, on the boundary row only,
on every filtered query, with no signal to the user or the host that a guess was made. A clean
rejection is loud, immediate, and fixable.

Contrast the **date preset** path, which arrives in the same `type` slot and *is* resolved to a
half-open `inRange` node (see `006` and `date-presets/README.md`). That is not an inconsistency: a
preset's `[from, to)` inclusivity is published in AG Grid's own `@Spec` table, so the adapter is
reading a documented fact rather than guessing an undocumented one. The distinction between those two
is the whole of SEM-4's position here, and this fixture is the negative half of it.

## What would have to change to lift this rejection

Any one of these, and only these:

1. **AG Grid adds an inclusivity signal to the wire model.** Then the adapter reads it, and this
   fixture becomes a stale case to delete — with the tag that introduced the field recorded, exactly
   as the three per-major differences in the category `README.md` are.
2. **AG Grid documents its `inRange` boundary semantics normatively**, the way it already documents
   the preset table. A citable published statement is the same class of evidence as the `@Spec`
   comment, and would make a fixed mapping a *derivation* rather than a guess. (Reading the
   client-side comparator's source is **not** sufficient on its own: the server-side contract is what
   is at issue, and AG Grid has never claimed the two must agree.)
3. **The host opts in explicitly**, e.g. an adapter option declaring which inclusivity its grid means.
   That keeps the decision with whoever actually knows, and is a strictly additive change — this
   fixture would keep passing, since it configures no such option.

What is *not* sufficient: matching some other server implementation's choice, or picking the
"obviously intended" inclusive/inclusive. Both are guesses with a citation attached to them.
