# 001 — AG Grid v31: a combined filter emitting `condition1`/`condition2` **and** `conditions`

**Derived from** tag `v31.0.0` of the `github/ag-grid` reference clone (this payload was *not*
captured from a running grid — see the category `README.md`'s provenance section):

- `grid-community-modules/core/src/ts/interfaces/iServerSideDatasource.ts` — `IServerSideGetRowsRequest`,
  the request envelope (byte-identical in all six supported majors).
- `grid-community-modules/core/src/ts/filter/provided/simpleFilter.ts` — `ICombinedSimpleModel`
  (the v31 declaration: `operator`, **required** `condition1`, **required** `condition2`, *optional*
  `conditions?: M[]`) and `SimpleFilter.getModelFromUi()`, the code that actually mints the model the
  grid puts on the wire.

**Requirement ids:** AG-4 (a payload per supported major), AG-1 (both filter-model forms must parse),
AR-5 (`Or(nodes[])` composition), AR-6 (`eq`), SEM-1 (case-insensitive equality via collation).

## What makes v31 different, and why this case exists

v31 is the only supported major whose `ICombinedSimpleModel` still carries the pre-v29.2 legacy pair.
Its own doc comment states the contract in both directions:

> When supplying combined models to the grid: `conditions` will be used if present. If `conditions`
> is not present, `condition1` and `condition2` will be used (deprecated).
> When receiving combined models **from** the grid: `conditions` will be populated with all the
> conditions (including the first and second conditions). `condition1` and `condition2` will be
> populated with the first and second conditions (deprecated).

and `getModelFromUi()` implements exactly that — one object literal carrying `filterType`, `operator`,
`condition1`, `condition2` **and** `conditions` together. So a real v31 SSRM payload from a
multi-condition filter carries all three keys **simultaneously**; there is no v31 payload with only
`conditions`, and none with only the legacy pair once the grid is the sender.

This fixture is therefore not "the legacy form" (that already has coverage in
`filter-tree/003`/`004`, which send the legacy pair *alone*, as a hand-built or pre-v29 model would).
It is the **dual-emission** form only v31 produces, and it pins the one behaviour that dual emission
makes observable: an adapter must read `conditions` and must **not** additionally read the legacy
pair.

`department` carries **three** conditions — the case is deliberately built at `maxNumConditions: 3`
(v31's `ISimpleFilterParams.maxNumConditions`, default 2, is a documented grid option) because with
only two conditions both sources agree and the fixture would prove nothing. With three, the two
readings disagree loudly:

| An adapter that reads... | Produces |
|---|---|
| `conditions` (correct) | `Engineering OR Design OR Research` — 3 leaves |
| `condition1`/`condition2` (wrong: legacy is a truncated projection) | `Engineering OR Design` — **silently drops `Research`**, returning too few rows |
| both (wrong: double counting) | 5 leaves, `Engineering` and `Design` duplicated |

Only the first reading matches the artifacts below, so the fixture fails for the other two.

## Compiled shape

Single filtered column, so the root is the column's own `Or` node directly (AR-5: no spurious
single-child wrapper above it). `department` is declared case-**insensitive**, so all three
equalities compile through SEM-1's *collation* mechanism (`COLLATE craticula_ci`, Mongo query-level
`collation`, Search `term` + `case_insensitive`) — never a pattern match.
