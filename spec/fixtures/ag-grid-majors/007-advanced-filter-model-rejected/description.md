# 007 — An Advanced Filter model on `filterModel` is rejected, in every major

**Derived from** the `github/ag-grid` reference clone. The shape below is
`JoinAdvancedFilterModel` + `TextAdvancedFilterModel` + `NumberAdvancedFilterModel`, read at tag
`release-36.0.0` from
`packages/ag-grid-community/src/interfaces/advancedFilterModel.ts`; the same declarations exist at
every supported tag (`v31.0.0` and `v32.0.0` carry them under `grid-community-modules/core/src/ts/
interfaces/` and `community-modules/core/src/interfaces/` respectively). Derived from published
types, *not* captured from a running grid — see the category `README.md`.

**Requirement ids:** AG-5 (Advanced Filter out of scope for v1), AG-4, SEC-6 (clean rejection).

## Why this belongs in a per-major category

`IServerSideGetRowsRequest.filterModel` is declared
`FilterModel | AdvancedFilterModel | null` in **all six** supported majors — its own doc comment
says so: *"If Advanced Filter is enabled, will be of type `AdvancedFilterModel | null`. If Advanced
Filter is disabled, will be of type `FilterModel`."* So this is not a recent-version concern that
only v36 payloads can raise: any supported major can put this on the wire the moment a host enables
Advanced Filter, and the answer must be the same rejection in every one of them. One shared case,
rather than six identical ones.

## The shape, and why it cannot be mistaken for a `FilterModel`

A `FilterModel` is a **map keyed by colId**, whose every value is a per-column filter object. An
`AdvancedFilterModel` is a **single object with `filterType` at its root**, whose column ids live in
a `colId` property *inside* each condition:

```ts
export interface JoinAdvancedFilterModel { filterType: 'join'; type: 'AND' | 'OR'; conditions: AdvancedFilterModel[]; }
export interface TextAdvancedFilterModel { filterType: 'text'; colId: string; type: TextAdvancedFilterModelType; filter?: string; }
```

The two are structurally incompatible: a per-column walk over this object sees a property named
`filterType` whose value is the *string* `"join"`, where a `FilterModel` walk requires an object.

## Which validation stage must catch it, and what "clean" means here

**Request parsing** (`requestParsing` / `malformed-request` — pinned by this case's own
`expected/rejection.json`). No backend artifact exists, per the adversarial fixture shape documented
in `../README.md`: validation rejects the request before any compilable IR is built.

Note the failure is *incidental* to strict per-column validation rather than the result of a
dedicated Advanced Filter shape check — the adapter has no ground truth to verify such a check
against, so it does not pretend to have one. That is exactly why the rejection needs a fixture built
from the real published shape: it pins the outcome (one clean, reason-coded rejection) independently
of the mechanism that produces it, so a port is free to detect it any way it likes as long as it
never silently parses a partial filter. The dangerous non-rejection here is not a crash, it is an
implementation that skips values it does not understand and returns **unfiltered** rows to a user who
can see filter chips in their grid.
