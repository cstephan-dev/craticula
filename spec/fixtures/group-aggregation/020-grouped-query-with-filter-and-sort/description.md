# 020 - Grouped query combined with a row-level filter and a sort on the aggregate

**Proves:** BM-2, AR-5, AR-9, AR-8

Top-level group (`rowGroupCols` = [region, segment], `groupKeys` = []) with an active
`filterModel` on `amount` (`greaterThan 1000`) and a `sortModel` ordering by the aggregated
column (`amount`, i.e. its `sum`) descending. The `amount > 1000` predicate is a **row-level**
filter applied before grouping (a `WHERE`, not a `HAVING` on the aggregate) -- AG Grid's
`filterModel` always describes a pre-aggregation row predicate, per BM-2's "filtered by consumed
group keys" plus AR-5's ordinary filter-tree semantics composing on top of it.

Sorting by the aggregated value needs different mechanics per backend: SQL `ORDER BY` the
aggregate **expression**; Mongo `$sort` on the `$group` output key. **OpenSearch/Elasticsearch cannot
express this request at all — see the Search note below — so this fixture deliberately has no
`search` arm.**

**Ordering-target note (GRP-4, corrected in #1640's review pass).** All four SQL arms that existed then originally
ordered by the aggregate's select-list **alias** (`ORDER BY "amount_sum" DESC NULLS FIRST` on
Postgres, and that same alias *inside* the emulated null bucket on SQL Server and MySQL:
`CASE WHEN [amount_sum] IS NULL …`, `` ISNULL(`amount_sum`) ``). The category README recorded the
latter two as a T-1b execution risk with the note that "both engines resolve select-list aliases in
`ORDER BY` expressions". **That was wrong.** PostgreSQL's `SELECT` documentation states that an
output column name "has to stand alone, that is, it cannot be used in an expression" — the Postgres
arm was legal only because the alias did stand alone — and T-SQL binds an alias inside a `CASE`
against the `FROM` scope, raising Msg 207 *Invalid column name*. GRP-4 now requires the aggregate
expression in the ordering path, and all five arms read the aggregate expression there
(`COALESCE(SUM(…), 0)` since #1886's AGG-6; `SUM(…)` when this note was written), **including
Postgres's**,
which was already legal: "which of these two forms an engine accepts" is a semantic difference, not
a per-dialect spelling, so splitting it across dialects would put it in the wrong layer. The alias
still names the aggregate in the projection, which is what the grid reads the response back by.

The **mongo** arm is unaffected and still addresses the aggregate by name. It is not an alias in a
scope: after `$group` the aggregate is a real field of the document the later `$addFields`/`$sort`
stages read.

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
the leaf-row `stableKey`.

## No `search` arm: OpenSearch/Elasticsearch cannot answer this request (#1644 review pass)

A prior revision of this fixture carried a `search.artifact.json` that rendered the aggregate sort as
a `bucket_sort` pipeline aggregation beside the `composite`, and this note described the missing
`missing` parameter as "the same class of documented, accepted ES-specific gap as AGG-5's". **That was
the wrong question.** The problem is not that a null marker is inexpressible; it is that the whole
answer is different.

A `composite` aggregation **always** enumerates buckets in composite-key order and returns `size` of
them. A `bucket_sort` is post-processing: it reorders only the buckets that page already contains. So
this request — "the top 100 regions by `SUM(amount)`" — compiled on Postgres to the globally largest
100 regions, and compiled on Search to *the first 100 regions in `region` key order, rearranged by
revenue*. With more than 100 distinct regions those are **different groups**, not a different
ordering, and nothing in the artifact tier could see it. Elasticsearch's own composite documentation
states the general rule rather than a "limit": a composite aggregation is **not compatible with
pipeline aggregations**, and gives this precise reason (a pipeline aggregation can only see one page).

So the Search arm is **deleted** and `SearchQueryBuilder` **refuses** a grouped level ordered by
anything other than its own group column (`spec/semantics.md` OSE-11(c)) — the same treatment
OSE-11(d) already gives a non-zero `startRow` on a grouped level, and the register SQL-7(b) and MDB-9
established: a plausible request that silently answers a different question is worse than an error
naming the reason. **This is a real, user-visible capability gap on this backend**, not a spelling
difference: ordering a grouped level by an aggregate does not work on OpenSearch/Elasticsearch.
Closing it needs a different grouped-ordering strategy (a two-pass `terms`-then-`composite` plan, or a
full bucket enumeration reordered client-side), which is the execution adapter's business (#1622) and
gradeable only against a live cluster (#1624).

The absence is asserted rather than left silent: `SearchQueryGroupingTests` names this fixture and
pins both that it carries no Search arm and that compiling it throws.

Independent of sort order, the composite aggregation's `missing_bucket: true` on a `region` terms
source still applies wherever a grouped level *is* expressible (a group-completeness concern, not a
sort-order one) — see `003`, `021` and `023`.
