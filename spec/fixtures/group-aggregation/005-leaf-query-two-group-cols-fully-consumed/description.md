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

**Case-sensitivity note (GRP-2, superseding this case's original AGG-3 note):**
`region`/`segment` are case-insensitive string fields (SEM-1), and the consumed `groupKeys`
drill-down matches are nonetheless compiled **exactly**. #1640 settled this: a grouping request's
`GROUP BY`, drill-down `WHERE` and grouped `ORDER BY` must agree with one another or a child level stops (this case is the leaf, so it has no `GROUP BY` of its own -- see the ordering note below)
summing to its parent's aggregate, and a collated `GROUP BY` key is not valid SQL at all. So
Postgres renders `"region" = $1` with no `COLLATE craticula_ci`, Mongo's pipeline carries no
query-level `collation` (that option is query-level and would fold `$group`'s buckets too), and
Search's `term`s carry no `case_insensitive`. A user's own `eq` filter on the same field is
untouched and still collates -- see `017`. On SQL Server and MySQL, whose default collations fold
case, exact buckets are a host DDL obligation; GRP-2 records that divergence.

**Ordering note (GRP-2's narrowed scope, #1640's review pass):** the `ORDER BY` above is the one
part of this leaf that GRP-2 does **not** govern. A leaf has no `GROUP BY` and no buckets, and
`product` is neither a group column nor a group key, so nothing about bucket identity is at stake --
SEM-1 governs it, and the **user's** key collates on the dialects that spell it: Postgres `COLLATE
craticula_ci`, SQL Server `COLLATE Latin1_General_CI_AS` (inside the null-bucket pair), SQLite
`COLLATE NOCASE`. **MySQL's arm is unchanged**, because there case-insensitive *is* the column's own
declared collation and there is nothing to spell (SQL-6). The **search** arm sorts `product` on the
`search.normalizedSubField` (`product.normalized`) this case's `schema.json` now declares, which
SEM-1 requires for case-insensitive ordering on ES/OpenSearch. The **mongo** arm is unchanged, and
that is a divergence GRP-2 records rather than a gap: Mongo's `collation` is query-level, so
collating this `$sort` would also fold the drill-down `$match` that reproduces the bucket -- and
bucket identity wins.

**The appended tiebreaker is the exception, and this case is where the two forms sit side by side.**
`stableKey` here is `id`, also declared `string` with no `text` block, so SEM-1 alone would collate
it too -- and #1640's first pass did, which is what its second review round overturned. The
**appended** SEM-6 tiebreaker resolves *exact* on every dialect (`"id" ASC NULLS LAST`, bare), because
it establishes row identity rather than comparing text: `craticula_ci` is `deterministic = false`, so
under it `'aB3'` and `'Ab3'` compare equal and a collated tiebreaker leaves the very ties it exists to
break -- offset paging could then return a row twice or drop it from both pages. Spelling nothing
sorts the key under its own storage collation, the one its declared uniqueness is stated in. So this
statement carries a **collated user key beside a bare tiebreaker**, which is the whole point: the rule
is about the key's provenance, not about the field. See SEM-6's supersession note in
`spec/semantics.md`.

**Wrapping note (GRP-5):** the drill-down conjunction is materialised as a real IR `and` node and
merged with any user filter through the same primitive `customFilters` uses, so SQL-2's "every
composite wraps" applies to it with no exception: `WHERE ("region" = $1 AND "segment" = $2)`. The
original artifact rendered it bare, on the reading that a synthesised conjunction is not an IR
construct; #1640 rejected that reading, because any compiler reusing its filter renderer for the
drill-down produces the wrapped form for free.

**Modeling note (deviation from raw AG Grid wire fidelity, deliberate):** real AG Grid SSRM sends `groupKeys` as `string[]` on the wire; the frontend adapter resolves each raw string into a typed value using the group column's declared schema type before building the IR (`AR-7`), exactly like it does for filter values. `spec/ir.schema.json`'s `$defs/groupSpec.groupKeys` requires that resolved `typedValue` form -- a prior review caught and fixed a regression where `groupKeys` had been left as raw scalars there. This fixture suite pins that fix at the request boundary too: `agGridRequest.json` here already carries the adapter-resolved typed form (e.g. `{"type":"string","value":"EMEA"}`), not the raw wire string, so a fixture consumer builds/checks the IR directly against the typed contract without re-deriving the (out-of-scope-for-this-suite) string-to-typedValue resolution step. See `group-aggregation/README.md` for the category-wide note.

**Null-ordering note (SEM-3):** the compiled `ORDER BY`/`$sort`/`sort` clauses shown here are incidental to this case (needed only for deterministic grouping/paging, not because this case's own request/data exercises a null group value or null sort key) -- but SEM-3 requires every backend to *always* emit an explicit null-ordering position on a raw-field sort key, never left implicit, regardless of whether a given fixture's data actually contains a null. Postgres carries `NULLS LAST`; Mongo (no native null ordering) uses the null-bucket `$addFields`/`$sort`/`$unset` emulation pattern; Search's grouped composite aggregations set `missing_bucket: true` on every terms source so a null-valued group isn't silently dropped (ir.schema.json's `groupSpec` description explicitly anticipates a drilled-into null group bucket). SEM-3/SEM-6's own exhaustive property coverage stays in the dedicated `sort-paging/` category; this is only the minimal consistent application needed so these artifacts model real compiler output.
