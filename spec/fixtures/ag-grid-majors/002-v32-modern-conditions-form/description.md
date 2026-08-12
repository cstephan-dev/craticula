# 002 — AG Grid v32: the post-legacy `conditions`-only combined form

**Derived from** tag `v32.0.0` of the `github/ag-grid` reference clone (derived from published types
and emitting code, *not* captured from a running grid — see the category `README.md`):

- `community-modules/core/src/interfaces/iServerSideDatasource.ts` — `IServerSideGetRowsRequest`.
- `community-modules/core/src/filter/provided/iSimpleFilter.ts` — `ICombinedSimpleModel`, which in
  v32 declares exactly `{ operator: JoinOperator; conditions: M[] }`: `condition1`/`condition2` were
  **removed** (they were deprecated in v29.2 and dropped in the v32 breaking-change release).
- `community-modules/core/src/filter/provided/simpleFilter.ts` — `SimpleFilter.getModelFromUi()`,
  which correspondingly emits `{ filterType, operator, conditions }` and nothing else.

Note the source path: v32 moved the community core from `grid-community-modules/core/src/ts/` (v31)
to `community-modules/core/src/`, then v33 moved it again to `packages/ag-grid-community/src/`. The
*paths* churn across majors far more than the *shapes* do.

**Requirement ids:** AG-4, AG-1, AR-5 (`And(nodes[])`), AR-6 (`gte`, `lt`), AR-7 (`decimal` typing).

## What this case pins

The modern combined form: one column, `operator`, and a `conditions` array with no legacy siblings.
This is the shape v32, v33, v34, v35 and v36 all emit — cases 002-005 are shape-identical to one
another by design (see the category `README.md`'s "Cases 002-005 are shape-identical" section for
why they are still kept per-major). Their **content** differs so the set broadens coverage rather
than repeating one payload four times; this one covers a numeric range expressed the way AG Grid's
UI actually produces it when the user picks two conditions.

## Content-specific notes

- The range is expressed as **two ANDed simple conditions** (`greaterThanOrEqual` + `lessThan`), not
  as AG Grid's dedicated `inRange` type. That is deliberate and matches `filter-tree/004`: a native
  `inRange` carries no boundary-inclusivity flags on the wire, so this suite never asserts one (see
  `../README.md` on `customFilters`, and SEM-4). Two explicit conditions carry their inclusivity in
  the operators themselves, so nothing has to be guessed.
- `salary` is declared `decimal`, so per AR-7 the compiled parameters are exact decimal
  representations — Postgres binds the values as strings (`"60000"`) and Mongo emits
  `{"$numberDecimal": ...}`, never a binary float that could round-trip lossily. The adapter itself
  performs no coercion: it carries the wire's JSON number forward verbatim as its raw text, and
  SEM-5's coercion stage downstream is what turns it into the declared type.
