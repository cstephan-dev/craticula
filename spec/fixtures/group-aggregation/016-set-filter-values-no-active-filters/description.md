# 016 - Set-filter values, no active filters

**Proves:** BM-3

`setValuesRequest.colId` = `"region"`, `filterModel` = `{}` (grid has no other active
filters right now), no `searchPrefix`, no `limit` -- the request omits the cap entirely, so
this fixture's illustrative default cap (1,000) applies. Compiles to a distinct-values query with
no `WHERE`/`$match`/`query` filter clause at all.

**Wire shape note:** `setValuesRequest` is **Craticula's own synthetic request shape**, not an AG Grid-defined format. AG Grid community edition has no fixed wire type for "get distinct values for a set filter" -- it delegates value resolution to a host-supplied callback (`values: (params) => void`) that the host implements however it likes. This fixture models a reasonable server-side wire contract for that callback so the compiler side (BM-3) has something concrete to compile against. See `group-aggregation/README.md` for the full shape rationale.

**Null-ordering note (SEM-3):** the compiled `ORDER BY`/`$sort`/`sort` clauses shown here are incidental to this case (needed only for deterministic grouping/paging, not because this case's own request/data exercises a null group value or null sort key) -- but SEM-3 requires every backend to *always* emit an explicit null-ordering position on a raw-field sort key, never left implicit, regardless of whether a given fixture's data actually contains a null. Postgres carries `NULLS LAST`; Mongo (no native null ordering) uses the null-bucket `$addFields`/`$sort`/`$unset` emulation pattern; Search's grouped composite aggregations set `missing_bucket: true` on every terms source so a null-valued group isn't silently dropped (ir.schema.json's `groupSpec` description explicitly anticipates a drilled-into null group bucket). SEM-3/SEM-6's own exhaustive property coverage stays in the dedicated `sort-paging/` category; this is only the minimal consistent application needed so these artifacts model real compiler output.
