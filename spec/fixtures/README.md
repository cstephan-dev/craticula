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
  schema.json               — the GridSchema config this request compiles against (see ../grid-schema.schema.json)
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

## `agGridRequest.json` wire shape (grounded in `github/ag-grid`, the local reference clone)

Top level (`IServerSideGetRowsRequest`, `packages/ag-grid-community/src/interfaces/iServerSideDatasource.ts`):
```
{ startRow: number|null, endRow: number|null, rowGroupCols: ColumnVO[], valueCols: ColumnVO[],
  pivotCols: [], pivotMode: false, groupKeys: string[], filterModel: FilterModel|null,
  sortModel: SortModelItem[] }
```
- `ColumnVO` (`iColumnVO.ts`): `{ id, displayName, field?, aggFunc? }`.
- `SortModelItem` (`iSortModelItem.ts`): `{ colId, sort: "asc"|"desc" }`.
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
    strings (`"YYYY-MM-DD HH:mm:ss"` or `"YYYY-MM-DD"`); **date presets** (AG-2) use a preset
    `type` (`"today"`, `"last7Days"`, ...) with `dateFrom`/`dateTo` both `undefined`.
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
  depth/leaf-count adversarial cases (AG Grid's own `filterModel` is structurally shallow — a flat
  per-column map, at most 2 conditions per column), `inRange` boundary inclusivity (AG Grid's
  `inRange` carries no inclusivity flags at all — do not invent an inclusivity default and assert it
  as if it were the AG Grid default; use `customFilters` with explicit flags instead, per SEM-4's own
  "must NOT present the exact AG Grid boundary-inclusivity default as settled fact" rule), and a set
  filter's `notIn` direction (AG Grid's `SetFilterModel` carries only an inclusion list — there is no
  wire-level invert flag).
- **`requestContext` (extension, not part of AG Grid's own wire format):** an optional sibling field,
  `{ requestContext: { zone: "<IANA id>" } }`, carrying the zone the adapter used to resolve any
  `datetime`-typed filter value in this request (SEM-4). Required on any fixture whose expected
  artifacts depend on a specific zone resolution — without it, a port author must guess the zone the
  fixture author assumed.

## Categories

| Directory | Proves | Target count |
|---|---|---|
| `operators/` | AR-6's 17-operator closed set, across field types | ~45 |
| `filter-tree/` | AR-5 And/Or/Not composition, nesting/depth variation | ~15 |
| `sort-paging/` | SEM-3 null ordering, SEM-6 stableKey tiebreaker, AR-9 | ~12 |
| `group-aggregation/` | BM-2 grouping, BM-3 set-filter values, AR-8 aggregation funcs | ~20 |
| `page-export/` | AG-3/EXP-2 page spec, EXP-1/EXP-6/EXP-8 export + hostile CSV values | ~20 |
| `adversarial/` | SEC-6: injection attempts, unknown fields, type confusion, depth bombs, oversized limits | ~20 |
| `dialect-parity/` | Dialect-specific mechanics + cross-backend parity showcases (SEM-1/2/3/7) | ~20 |

Each category directory has its own `README.md` enumerating its cases and the specific requirement
each one backs.
