# 018 - Set-filter values, search prefix

**Proves:** BM-3

`setValuesRequest.searchPrefix` = `"eu"` -- the set-filter's own search box narrows the
candidate values (e.g. typing "eu" while browsing region values). No other active filters.
This is a `startsWith`-shaped, pattern-match operation (SEM-1's pattern-match family, not the
collation/exact-match family SEM-1 pins for `eq`/`in`), so every backend's own substring/prefix
mechanism applies:

- **Postgres** -- `ILIKE 'eu%' ESCAPE '\'`. `searchPrefix`'s own `%`/`_`/`\` characters (none
  present in `"eu"`, but the mechanism is unconditional) are escaped *before* the trailing `%` is
  appended to build the pattern, and the `ESCAPE '\'` clause is mandatory (SEM-7) -- without it,
  the escaping is silently a no-op on dialects with no default `LIKE` escape character.
- **Mongo** -- an anchored, case-insensitive regex: `{"$regex": "^eu", "$options": "i"}`, built
  from the same escaped literal (regex metacharacters are a wider class than `LIKE`'s `%`/`_`/`\`,
  per SEM-7, and are escaped in full before insertion -- again a no-op here only because `"eu"`
  happens to contain none).
- **Search** -- a `terms` aggregation's `include` parameter, a Lucene regex evaluated at
  aggregation time, has **no case-insensitivity option** in the pinned 7.10-era subset (unlike
  `term`/`wildcard`/`regexp` query-time `case_insensitive`, or `range`'s own documented gap in
  SEM-1). `region` does not declare a `search.normalizedSubField` (SEM-1), so this artifact's
  `include: "eu.*"` is compiled case-sensitively as typed -- a documented, accepted divergence
  from Postgres/Mongo's case-insensitive prefix match, not an oversight (mirrors AGG-5's
  precedent of naming an ES-specific mechanism gap rather than silently pinning three-way-identical
  behavior ES cannot actually produce). A field needing case-insensitive prefix matching on
  Search would need to declare `search.normalizedSubField` and target it instead.

**Wire shape note:** `setValuesRequest` is **Craticula's own synthetic request shape**, not an AG Grid-defined format. AG Grid community edition has no fixed wire type for "get distinct values for a set filter" -- it delegates value resolution to a host-supplied callback (`values: (params) => void`) that the host implements however it likes. This fixture models a reasonable server-side wire contract for that callback so the compiler side (BM-3) has something concrete to compile against. See `group-aggregation/README.md` for the full shape rationale.

**Null-ordering note (SEM-3):** the compiled `ORDER BY`/`$sort`/`sort` clauses shown here are incidental to this case (needed only for deterministic grouping/paging, not because this case's own request/data exercises a null group value or null sort key) -- but SEM-3 requires every backend to *always* emit an explicit null-ordering position on a raw-field sort key, never left implicit, regardless of whether a given fixture's data actually contains a null. Postgres carries `NULLS LAST`; Mongo (no native null ordering) uses the null-bucket `$addFields`/`$sort`/`$unset` emulation pattern; Search's grouped composite aggregations set `missing_bucket: true` on every terms source so a null-valued group isn't silently dropped (ir.schema.json's `groupSpec` description explicitly anticipates a drilled-into null group bucket). SEM-3/SEM-6's own exhaustive property coverage stays in the dedicated `sort-paging/` category; this is only the minimal consistent application needed so these artifacts model real compiler output.
