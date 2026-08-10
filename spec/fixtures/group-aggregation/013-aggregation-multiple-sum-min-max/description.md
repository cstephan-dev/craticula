# 013 - Multiple aggregations: sum + min + max

**Proves:** AR-8, BM-2

Three aggregation pairs together: `sum(amount)`, `min(amount)`, `max(amount)` -- a common
"summary row" shape. Top-level group (`rowGroupCols` = [region], `groupKeys` = []): grouped by
`region` with no drill-down filter yet.

**Modeling note (deviation from raw AG Grid wire fidelity, deliberate):** real AG Grid SSRM sends `groupKeys` as `string[]` on the wire; the frontend adapter resolves each raw string into a typed value using the group column's declared schema type before building the IR (`AR-7`), exactly like it does for filter values. `spec/ir.schema.json`'s `$defs/groupSpec.groupKeys` requires that resolved `typedValue` form -- a prior review caught and fixed a regression where `groupKeys` had been left as raw scalars there. This fixture suite pins that fix at the request boundary too: `agGridRequest.json` here already carries the adapter-resolved typed form (e.g. `{"type":"string","value":"EMEA"}`), not the raw wire string, so a fixture consumer builds/checks the IR directly against the typed contract without re-deriving the (out-of-scope-for-this-suite) string-to-typedValue resolution step. See `group-aggregation/README.md` for the category-wide note.

**Null-ordering note (SEM-3):** the compiled `ORDER BY`/`$sort`/`sort` clauses shown here are incidental to this case (needed only for deterministic grouping/paging, not because this case's own request/data exercises a null group value or null sort key) -- but SEM-3 requires every backend to *always* emit an explicit null-ordering position on a raw-field sort key, never left implicit, regardless of whether a given fixture's data actually contains a null. Postgres carries `NULLS LAST`; Mongo (no native null ordering) uses the null-bucket `$addFields`/`$sort`/`$unset` emulation pattern; Search's grouped composite aggregations set `missing_bucket: true` on every terms source so a null-valued group isn't silently dropped (ir.schema.json's `groupSpec` description explicitly anticipates a drilled-into null group bucket). SEM-3/SEM-6's own exhaustive property coverage stays in the dedicated `sort-paging/` category; this is only the minimal consistent application needed so these artifacts model real compiler output.

**Known divergence note (AGG-5):** this case's Search artifact sets `missing_bucket: true` on its
composite `terms` source(s) (group-completeness, so a null-valued group isn't silently dropped).
The semantics addendum's **AGG-5** rule documents an accepted, host-visible divergence here: ES/
OpenSearch's composite aggregation has no `missing_order` control in the pinned 7.10-era subset, so
the null bucket always sorts **first**, regardless of the requested sort direction -- unlike
Postgres (`NULLS LAST`/`NULLS FIRST` per SEM-3) and Mongo (the null-bucket emulation), which both
honor the requested direction. This is a known, accepted divergence, not an omission this fixture
tries to work around.
