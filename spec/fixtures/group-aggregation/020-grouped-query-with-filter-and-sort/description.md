# 020 - Grouped query combined with a row-level filter and a sort on the aggregate

**Proves:** BM-2, AR-5, AR-9, AR-8

Top-level group (`rowGroupCols` = [region, segment], `groupKeys` = []) with an active
`filterModel` on `amount` (`greaterThan 1000`) and a `sortModel` ordering by the aggregated
column (`amount`, i.e. its `sum`) descending. The `amount > 1000` predicate is a **row-level**
filter applied before grouping (a `WHERE`, not a `HAVING` on the aggregate) -- AG Grid's
`filterModel` always describes a pre-aggregation row predicate, per BM-2's "filtered by consumed
group keys" plus AR-5's ordinary filter-tree semantics composing on top of it.

Sorting by the aggregated value needs different mechanics per backend: SQL `ORDER BY` the
aggregate alias directly; Mongo `$sort` on the `$group` output key; the Search composite
aggregation can only order by its *source* values natively, so ordering by a sub-aggregation metric
requires an explicit `bucket_sort` pipeline aggregation.

**Modeling note (deviation from raw AG Grid wire fidelity, deliberate):** real AG Grid SSRM sends `groupKeys` as `string[]` on the wire; the frontend adapter resolves each raw string into a typed value using the group column's declared schema type before building the IR (`AR-7`), exactly like it does for filter values. `spec/ir.schema.json`'s `$defs/groupSpec.groupKeys` requires that resolved `typedValue` form -- a prior review caught and fixed a regression where `groupKeys` had been left as raw scalars there. This fixture suite pins that fix at the request boundary too: `agGridRequest.json` here already carries the adapter-resolved typed form (e.g. `{"type":"string","value":"EMEA"}`), not the raw wire string, so a fixture consumer builds/checks the IR directly against the typed contract without re-deriving the (out-of-scope-for-this-suite) string-to-typedValue resolution step. See `group-aggregation/README.md` for the category-wide note.

**Null-ordering note (SEM-3) and tiebreaker (SEM-6) — corrected, unconditional.** A prior revision
of this fixture argued `amount_sum` is "never NULL for an emitted group row" and skipped both the
null-ordering marker and a deterministic tiebreaker after it. That reasoning doesn't hold: SEM-3
requires an explicit `nullsPosition` on *every* `sortItem`, and SEM-6's own "must NOT" list rules
out exactly this "probably never null in practice" justification for omitting the marker — there is
no schema state that licenses the omission, so it applies here too, unconditionally, same as every
other sort key in this document. The user's requested sort (`amount ASC` in the aggregate sense
`amount_sum DESC`, since AG Grid sent `desc`) gets the adapter's pinned mapping (SEM-3): `desc` →
`nullsPosition: "first"`. And because `amount_sum` is an aggregate, not a unique key, ties on it are
exactly as non-deterministic across two executions as any other non-unique sort key (SEM-6's own
rationale) — the fix appends the group column (`region`, `asc`, `nullsPosition: "last"` per the
adapter's default) as the tiebreaker, exactly the same pattern SEM-6 uses for `stableKey` on
ordinary leaf queries. `region` (not `id`/`stableKey`) is correct here because this is a *grouped*
(non-leaf) row: the group column is already the row's own unique key post-`GROUP BY`/`$group`
(see `fixtures-suite` agent-memory's BM-2 note), so it is the group's own natural tiebreaker, not
the leaf-row `stableKey`. On Search, the composite aggregation's `bucket_sort` pipeline aggregation
has no `missing` parameter in the pinned 7.10-era subset, so `amount_sum`'s own null-ordering marker
has no native equivalent to compile to there — only the `region`-tiebreaker half of this fix is
expressible as a second `bucket_sort` sort criterion (`_key asc`); this is the same class of
documented, accepted ES-specific gap as AGG-5's own composite null-bucket note, not a three-way
parity break this fixture can otherwise resolve. Independent of sort order, the composite
aggregation's `missing_bucket: true` on the `region` terms source still applies (a group-completeness
concern, not a sort-order one).
