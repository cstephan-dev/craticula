# 005 - Leaf query, two group columns fully consumed

**Proves:** BM-2

`rowGroupCols` = [region, segment], `groupKeys` = [EMEA, Enterprise] -- `groupKeys.length (2) ==
rowGroupCols.length (2)`, so this compiles the **leaf** query: no `GROUP BY`, raw rows filtered
by both consumed equality predicates, projecting every schema field. `valueCols` is still present
on the wire (AG Grid always sends it) but its `aggFunc` is ignored at leaf level -- leaf rows
project the raw `amount`, not an aggregate.

`sortModel` requests `product ASC`; per SEM-6 the compiler appends the schema's `stableKey`
(`id`) as a trailing tiebreaker since the requested sort doesn't already end in a unique field --
shown here only as the minimal illustration needed for a deterministic leaf-row order; SEM-6's own
exhaustive coverage lives in the `sort-paging/` category.

**Case-sensitivity note (AGG-3):** `region`/`segment` are case-insensitive string fields (SEM-1).
Per the semantics addendum's **AGG-3** rule, a `groupKeys` drill-down match is compiled into an
equality predicate against the group column exactly like any other filter value, so it follows the
same per-field case-sensitivity mechanism SEM-1 pins for `eq` -- never automatically exact-match.
Postgres compiles each consumed key's match to a case-insensitive collation (`COLLATE
craticula_ci`, never `ILIKE`/`lower()` -- SEM-1's collation-vs-pattern-match split), Mongo applies a
query-level `collation: {"locale": "en", "strength": 2}` option alongside the pipeline, and Search's
`term` query carries `case_insensitive: true` on each matched key.

**Modeling note (deviation from raw AG Grid wire fidelity, deliberate):** real AG Grid SSRM sends `groupKeys` as `string[]` on the wire; the frontend adapter resolves each raw string into a typed value using the group column's declared schema type before building the IR (`AR-7`), exactly like it does for filter values. `spec/ir.schema.json`'s `$defs/groupSpec.groupKeys` requires that resolved `typedValue` form -- a prior review caught and fixed a regression where `groupKeys` had been left as raw scalars there. This fixture suite pins that fix at the request boundary too: `agGridRequest.json` here already carries the adapter-resolved typed form (e.g. `{"type":"string","value":"EMEA"}`), not the raw wire string, so a fixture consumer builds/checks the IR directly against the typed contract without re-deriving the (out-of-scope-for-this-suite) string-to-typedValue resolution step. See `group-aggregation/README.md` for the category-wide note.

**Null-ordering note (SEM-3):** the compiled `ORDER BY`/`$sort`/`sort` clauses shown here are incidental to this case (needed only for deterministic grouping/paging, not because this case's own request/data exercises a null group value or null sort key) -- but SEM-3 requires every backend to *always* emit an explicit null-ordering position on a raw-field sort key, never left implicit, regardless of whether a given fixture's data actually contains a null. Postgres carries `NULLS LAST`; Mongo (no native null ordering) uses the null-bucket `$addFields`/`$sort`/`$unset` emulation pattern; Search's grouped composite aggregations set `missing_bucket: true` on every terms source so a null-valued group isn't silently dropped (ir.schema.json's `groupSpec` description explicitly anticipates a drilled-into null group bucket). SEM-3/SEM-6's own exhaustive property coverage stays in the dedicated `sort-paging/` category; this is only the minimal consistent application needed so these artifacts model real compiler output.
