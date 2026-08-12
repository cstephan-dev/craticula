# 003 — AG Grid v33: text `conditions` on one column, a set filter on another

**Derived from** tag `v33.0.0` of the `github/ag-grid` reference clone (derived from published types
and emitting code, *not* captured from a running grid — see the category `README.md`):

- `packages/ag-grid-community/src/interfaces/iServerSideDatasource.ts` — `IServerSideGetRowsRequest`
  (v33 is where the sources moved into `packages/ag-grid-community/`, the layout every later major
  keeps).
- `packages/ag-grid-community/src/filter/provided/iSimpleFilter.ts` — `ICombinedSimpleModel`
  (`{ operator, conditions }`), `ISimpleFilterModelType` (the `startsWith`/`contains` members used
  here).
- `packages/ag-grid-community/src/filter/provided/simpleFilter.ts` — `getModelFromUi()`.
- `packages/ag-grid-community/src/interfaces/iSetFilter.ts` — `SetFilterModel`
  (`{ filterType?: 'set', values: SetFilterModelValue }`), unchanged in all six majors.

**Requirement ids:** AG-4, AG-1, AR-5 (cross-column `And` over a within-column `Or`), AR-6
(`startsWith`, `contains`, `in`), SEM-1 (the two-mechanism split), SEM-7 (substring matching).

## What this case pins

Structurally identical to 002 (v33's `ICombinedSimpleModel` is byte-identical to v32's), with content
chosen to cover a different half of the operator surface: pattern-matching text operators, plus a
**second filtered column** so the compiled root is a cross-column `And` whose first child is the
column's own `Or`.

It also puts both SEM-1 mechanisms in one artifact, which is the point of pairing them here:

- `startsWith`/`contains` are **pattern matches** — `ILIKE` with an explicit `ESCAPE`, Mongo
  `$regex`+`$options:"i"`, Search `wildcard`+`case_insensitive`.
- the set filter's `in` is **set membership**, so it uses the *collation* mechanism instead
  (`COLLATE craticula_ci = ANY(...)`, Mongo's query-level `collation`, Search `term`+
  `case_insensitive`). A query-level Mongo collation deliberately does **not** apply to the `$regex`
  clauses in the same pipeline — that is why the regexes still carry their own `$options: "i"`.

Reaching for `ILIKE`/`$regex` to implement the `in` "because it is already case-insensitive that way"
is precisely what `../README.md`'s case-insensitivity policy forbids: a literal `%` or `_` inside a
set value would silently become a wildcard.

## Wire-shape note

A `set` entry has no `operator`/`conditions` at all — it is a bare `{ filterType: "set", values }`,
which is why a per-column dispatch cannot assume every entry is an `ISimpleFilterModel`. AG Grid
Community's set filter carries only an inclusion list; there is no wire-level invert flag, so `notIn`
is unreachable from a native payload (see `../README.md` and `operators/036`, which reaches it
through `customFilters`).
