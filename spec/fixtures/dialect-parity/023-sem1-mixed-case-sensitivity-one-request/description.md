# 023 — SEM-1: two case sensitivities in one request, and the one backend that cannot hold both

**Proves:** SEM-1's third Must-Not read the *other* way round. The rule's usual reading is "do not
fold case on a field the schema declared sensitive by accident"; every fixture before this one
carries a single string field, so no artifact could show what happens when one request compares an
**insensitive** field and a **sensitive** one at the same time. On the five SQL dialects nothing
happens — `COLLATE` attaches per operand, so each comparison spells its own mechanism and the two
sit side by side in one `WHERE`. On MongoDB the mechanism is a **query-level** option that governs
every stage at once, so the insensitive comparison's collation reaches the sensitive one too, and
`"AB-100"` would match a stored `"ab-100"` on the one field whose schema says it must not.

This fixture's `schema.json` is `006`'s and `014`'s **merged**, field for field: `006` pins the
case-insensitive `lastName` alone, `014` the case-sensitive `employeeCode` alone. Putting both in
one request is the only new variable, so any difference between this fixture's artifacts and those
two is attributable to the interaction and nothing else.

Filter: `lastName equals "smith"` (insensitive, SEM-1's default) **and** `employeeCode equals
"AB-100"` (sensitive, SC-3's opt-in). Two columns in one `filterModel`, which AR-5 composes as an
implicit `and` in `filterModel` key order (`filter-tree/001`).

Per backend:

- **Postgres**: `"lastName" COLLATE craticula_ci = $1` beside a bare `"employeeCode" = $2` — bare is
  exact here, the default collation being deterministic (SQL-6's Postgres row). SQL-2's composite
  wrap around the pair.
- **SQL Server**: the one arm that spells **both** directions, `COLLATE Latin1_General_CI_AS` and
  `COLLATE Latin1_General_CS_AS`, because the stock server collation
  (`SQL_Latin1_General_CP1_CI_AS`) folds case and would silently discard the sensitive declaration.
- **MySQL**: **both** operands bare, for two different reasons — the insensitive one because the
  column's own `_ci` collation already folds, the sensitive one because MySQL has no portable
  in-statement spelling and SQL-6 records it as a host DDL obligation instead (`employeeCode` needs
  a `_bin`/`_as_cs` column collation, or its declaration has no effect). The clearest case in the
  suite for why "the arms differ in tokens alone" is not the same claim as "the arms are
  equivalent".
- **SQLite**: `"lastName" = ? COLLATE NOCASE` — note the position, after the comparison rather than
  after the operand (`006`) — beside a bare `"employeeCode" = ?`, `BINARY` being the default
  sequence.
- **Oracle**: `COLLATE BINARY_CI` and `COLLATE BINARY`, both spelled, because Oracle's default
  column collation resolves through the *session*'s `NLS_COMP`/`NLS_SORT` and so is not a fixed
  answer in either direction (SQL-6).
- **MongoDB**: the pipeline carries `collation: {locale: "en", strength: 2}` for `lastName`, and the
  `employeeCode` comparison is therefore rendered as **`{"$expr": {"$eq": ["$employeeCode",
  {"$literal": "AB-100"}]}}`** rather than as an ordinary field document (**MDB-9**).
  MongoDB 7.0 applies the aggregate's query-level collation to `$expr` comparisons as well, so the
  `$expr` form does **not** keep `employeeCode` case-sensitive: the `mongo` `allowedDivergence` entry
  records the rows the engine returns (MDB-9, accepted divergence #5). This
  is the only artifact in the suite that contains an `$expr`, and it is here rather than in
  `operators/` for the same reason `021`'s GLOB is: the shape exists only because two backends answer
  the same IR differently.

  **The `{"$literal": …}` around the value is not decoration.** It is the *only* arm of the seven
  whose value is not carried by a parameter: the five SQL dialects bind `$2`/`?`/`:2` and Search
  puts the value in its own `value` slot, whereas an aggregation expression has no parameter
  mechanism and instead reads a `$`-prefixed string as a **field path**. Unwrapped, a filter value of
  `"$salary"` would compile to `{"$eq": ["$employeeCode", "$salary"]}` — a column-to-column
  comparison against a field the grid never exposed. `$literal` is this language's binding, MDB-9
  requires it on **every** value operand regardless of type, and the pinned form above is what a port
  must reproduce. (The `$`-prefixed value itself is pinned by `MongoPipelineOperatorTests` rather
  than by a fixture: the other six arms would bind it exactly as they bind `"AB-100"`, so a seventh
  set of artifacts would restate what `014` already shows.)
- **Search**: two `term` clauses under `bool.must` in request order (`filter-tree/001`), the first
  carrying `case_insensitive: true` (`006`) and the second omitting it (`014`). ES/OpenSearch has
  per-clause granularity, so it needs no equivalent of the Mongo escape and the two clauses are
  simply the two forms the suite already pins, side by side.

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6. That is deliberate and load-bearing rather than incidental: an appended
tiebreaker resolves `NotApplicable` (SEM-6), so it cannot collide with the collation. **A
user-chosen sort key on `employeeCode` would**, and MongoDB has no `$expr` for a `$sort` — MDB-9
makes that combination a compiler error, which is why no fixture can pin it (there is no artifact
for a request that does not compile; `MongoPipelineSortAndPagingTests` covers it, exactly as
SQL-7(b) is covered by `DialectRenderingTests`).

**Coverage tier:** all seven backends — all five SQL dialects + MongoDB + Search. Authored during
#1643's review pass, when the Mongo compiler was found to fold case on every case-sensitive string
comparison in any pipeline that carried a collation for some other field. Fixing that without an
artifact would have left the new `$expr` rendering pinned by unit tests alone, which is the state
`021` (GLOB) and `022` (`[`) were authored to fix for the SQL dialects.

**Requirement ids:** SEM-1, SC-3, SQL-6, MDB-9, SEM-6 (tiebreaker, incidental), SQL-2 (composite
wrap, incidental).
