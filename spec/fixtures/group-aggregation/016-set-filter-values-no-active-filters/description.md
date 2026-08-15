# 016 - Set-filter values, no active filters

**Proves:** BM-3, GRP-6

`setValuesRequest.colId` = `"region"`, `filterModel` = `{}` (grid has no other active
filters right now), no `searchPrefix`, no `limit` -- the request omits the cap entirely, so
this fixture's illustrative default cap (1,000) applies. Compiles to a distinct-values query with
no `WHERE`/`$match`/`query` filter clause at all.

**The baseline shape, and the three things about it that are rules rather than incidents (GRP-6):**

- **The `DISTINCT` key and its `ORDER BY` fold no case**, even though `region` is a case-insensitive
  `string` by schema default. A `SELECT DISTINCT` computes a distinct set exactly as `GROUP BY` does,
  and its entries are handed straight back to the grid to be resent as `in` values in this very
  column's filter, or as a `groupKey` — which a grouped query matches **exactly** (GRP-2). A collated
  `DISTINCT` would fold `"EMEA"` and `"emea"` into one entry whose label is unspecified, and that
  entry would then match neither original row. Read as an ordinary query instead, SEM-1 would collate
  both positions; GRP-6 rejects that reading. This was the open question `016`-`019` carried from
  #1630 through #1640 and #1641 settled — the artifacts already rendered this way, so none changed.
- **There is no projection alias and no SEM-6 tiebreaker.** The response is one column of scalars,
  not rows a grid maps by key, so GRP-3's naming rule has nothing to name; and the distinct column's
  values are unique across the result rows by construction, so appending the schema's `stableKey`
  (`id`) would order by a column the statement does not even select.
- **The cap is a ceiling, not a page** — `LIMIT $1` with no `OFFSET`, `$limit` with no `$skip`,
  `"size"` with no `"from"`. The list is fetched once; there is no second page to skip into.

**Wire shape note:** `setValuesRequest` is **Craticula's own synthetic request shape**, not an AG Grid-defined format. AG Grid community edition has no fixed wire type for "get distinct values for a set filter" -- it delegates value resolution to a host-supplied callback (`values: (params) => void`) that the host implements however it likes. This fixture models a reasonable server-side wire contract for that callback so the compiler side (BM-3) has something concrete to compile against. See `group-aggregation/README.md` for the full shape rationale.

**Null-ordering note (SEM-3):** the compiled `ORDER BY`/`$sort`/`sort` clauses shown here are incidental to this case (needed only for deterministic grouping/paging, not because this case's own request/data exercises a null group value or null sort key) -- but SEM-3 requires every backend to *always* emit an explicit null-ordering position on a raw-field sort key, never left implicit, regardless of whether a given fixture's data actually contains a null. Postgres carries `NULLS LAST`; Mongo (no native null ordering) uses the null-bucket `$addFields`/`$sort`/`$unset` emulation pattern; Search's grouped composite aggregations set `missing_bucket: true` on every terms source so a null-valued group isn't silently dropped (ir.schema.json's `groupSpec` description explicitly anticipates a drilled-into null group bucket). SEM-3/SEM-6's own exhaustive property coverage stays in the dedicated `sort-paging/` category; this is only the minimal consistent application needed so these artifacts model real compiler output.
