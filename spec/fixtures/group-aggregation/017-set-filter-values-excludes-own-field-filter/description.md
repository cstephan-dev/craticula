# 017 - Set-filter values, another column's filter is honored (own field excluded)

**Proves:** BM-3

`setValuesRequest.colId` = `"region"`, but `filterModel` carries an active filter on a
**different** column (`segment equals "Enterprise"`) -- per BM-3's AG Grid convention, the
values query honors "the current filter model minus the field's own filter". If `region` itself
had an active filter in the live grid, the host is expected to have already stripped it before
building this synthetic request; this fixture asserts the *other* column's filter still applies
while resolving `region`'s own distinct values.

`segment` is case-insensitive by schema default (SEM-1), and this is an `eq`-family comparison
(not a substring/prefix one) — SEM-1's collation-vs-pattern-match split pins the **collation**
mechanism here, never `ILIKE`/pattern-match (that mechanism is reserved for
`contains`/`notContains`/`startsWith`/`endsWith`, SEM-7's territory; reusing it for `eq` is a trap,
since a literal `%`/`_` in the value would silently widen into a wildcard). Postgres compiles to
`"segment" COLLATE craticula_ci = $1` (the project's pinned ICU collation name, never
`lower()`/`ILIKE`); Mongo applies the same case-insensitive comparison via a query-level
`collation: {"locale": "en", "strength": 2}` option alongside the pipeline (covering both the
`$match` and the `_id` `$sort`, per SEM-1's "same mechanism across every context on that field");
Search's `term` query carries `case_insensitive: true` directly on the original-cased value
(`"Enterprise"`, never runtime-lowercased) — no normalized sub-field needed, since
`search.normalizedSubField` is only required for case-insensitive *ordering* comparisons
(`gt`/`gte`/`lt`/`lte`), not equality.

**Wire shape note:** `setValuesRequest` is **Craticula's own synthetic request shape**, not an AG Grid-defined format. AG Grid community edition has no fixed wire type for "get distinct values for a set filter" -- it delegates value resolution to a host-supplied callback (`values: (params) => void`) that the host implements however it likes. This fixture models a reasonable server-side wire contract for that callback so the compiler side (BM-3) has something concrete to compile against. See `group-aggregation/README.md` for the full shape rationale.

**Null-ordering note (SEM-3):** the compiled `ORDER BY`/`$sort`/`sort` clauses shown here are incidental to this case (needed only for deterministic grouping/paging, not because this case's own request/data exercises a null group value or null sort key) -- but SEM-3 requires every backend to *always* emit an explicit null-ordering position on a raw-field sort key, never left implicit, regardless of whether a given fixture's data actually contains a null. Postgres carries `NULLS LAST`; Mongo (no native null ordering) uses the null-bucket `$addFields`/`$sort`/`$unset` emulation pattern; Search's grouped composite aggregations set `missing_bucket: true` on every terms source so a null-valued group isn't silently dropped (ir.schema.json's `groupSpec` description explicitly anticipates a drilled-into null group bucket). SEM-3/SEM-6's own exhaustive property coverage stays in the dedicated `sort-paging/` category; this is only the minimal consistent application needed so these artifacts model real compiler output.
