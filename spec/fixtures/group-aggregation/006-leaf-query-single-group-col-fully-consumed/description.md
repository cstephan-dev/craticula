# 006 - Leaf query, single group column fully consumed

**Proves:** BM-2

`rowGroupCols` = [region], `groupKeys` = [EMEA] -- `groupKeys.length (1) ==
rowGroupCols.length (1)`: leaf query. No `sortModel` at all this time, so the compiled order is
just the SEM-6 tiebreaker (`id ASC`) on its own -- the minimal deterministic case, contrasted with
005's explicit user sort + tiebreaker.

**Case-sensitivity note (GRP-2, superseding this case's original AGG-3 note):** `region` is a
case-insensitive string field (SEM-1), and the consumed `groupKeys` drill-down match is nonetheless
compiled **exactly**. #1640 settled this: a grouping request's `GROUP BY`, drill-down `WHERE` and
grouped `ORDER BY` must agree with one another or a child level stops summing to its parent's
aggregate, and a collated `GROUP BY` key is not valid SQL at all. (This case is the leaf, so it has
no `GROUP BY` of its own — see the ordering note below.) So Postgres renders `"region" = $1` with no
`COLLATE craticula_ci`, Mongo's pipeline carries no query-level `collation` (that option is
query-level and would fold `$group`'s buckets too), and Search's `term` carries no
`case_insensitive`. A user's own `eq` filter on the same field is untouched and still collates --
see `017`. On SQL Server and MySQL, whose default collations fold case, exact buckets are a host DDL
obligation; GRP-2 records that divergence.

**Ordering note (GRP-2's narrowed scope, and SEM-6's own exception):** this leaf's `ORDER BY` is the
SEM-6 tiebreaker *alone*, and two rules meet on it. GRP-2 does **not** govern it -- a leaf has no
`GROUP BY` and no buckets, so SEM-1 would. But the **appended** tiebreaker is exempt from SEM-1 too:
it establishes row identity rather than comparing text, so it renders **bare on every arm**
(`ORDER BY "id" ASC NULLS LAST`), which is what this case had before #1640 and what it has again.
`stableKey` is `id`, declared `string` with no `text` block, so SEM-1 alone would have collated it,
and #1640's first pass did -- but `craticula_ci` is `deterministic = false`, under which `'aB3'` and
`'Ab3'` compare equal, so the collated form left this leaf's *only* sort key non-total and offset
paging free to duplicate or drop a row: SEM-6's own failure mode, reintroduced by SEM-6's own
mechanism. Since this case's whole subject is "the minimal deterministic ordering", it is also the
sharpest illustration of the rule. `005` is the companion showing the collated user key beside the
bare tiebreaker. See SEM-6's supersession note in `spec/semantics.md`.

**Modeling note (deviation from raw AG Grid wire fidelity, deliberate):** real AG Grid SSRM sends `groupKeys` as `string[]` on the wire; the frontend adapter resolves each raw string into a typed value using the group column's declared schema type before building the IR (`AR-7`), exactly like it does for filter values. `spec/ir.schema.json`'s `$defs/groupSpec.groupKeys` requires that resolved `typedValue` form -- a prior review caught and fixed a regression where `groupKeys` had been left as raw scalars there. This fixture suite pins that fix at the request boundary too: `agGridRequest.json` here already carries the adapter-resolved typed form (e.g. `{"type":"string","value":"EMEA"}`), not the raw wire string, so a fixture consumer builds/checks the IR directly against the typed contract without re-deriving the (out-of-scope-for-this-suite) string-to-typedValue resolution step. See `group-aggregation/README.md` for the category-wide note.

**Null-ordering note (SEM-3):** the compiled `ORDER BY`/`$sort`/`sort` clauses shown here are incidental to this case (needed only for deterministic grouping/paging, not because this case's own request/data exercises a null group value or null sort key) -- but SEM-3 requires every backend to *always* emit an explicit null-ordering position on a raw-field sort key, never left implicit, regardless of whether a given fixture's data actually contains a null. Postgres carries `NULLS LAST`; Mongo (no native null ordering) uses the null-bucket `$addFields`/`$sort`/`$unset` emulation pattern; Search's grouped composite aggregations set `missing_bucket: true` on every terms source so a null-valued group isn't silently dropped (ir.schema.json's `groupSpec` description explicitly anticipates a drilled-into null group bucket). SEM-3/SEM-6's own exhaustive property coverage stays in the dedicated `sort-paging/` category; this is only the minimal consistent application needed so these artifacts model real compiler output.
