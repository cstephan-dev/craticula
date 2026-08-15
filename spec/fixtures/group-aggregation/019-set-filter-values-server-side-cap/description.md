# 019 - Set-filter values, server-side cap enforced

**Proves:** BM-3, GRP-6

Client requests `limit: 50000`. This fixture's illustrative host-configured max cap for this
grid is 10,000 -- lower than the request. The compiled artifact clamps to the **configured** cap
(10,000), never the client-requested value: `LIMIT $1` with `params: [10000]`, Mongo
`$limit: 10000`, Search `terms.size: 10000`. This is the "server-side cap" BM-3 requires
regardless of what a client asks for -- contrast with 016/017/018 where the request omits `limit`
entirely and the (lower) illustrative default of 1,000 applies instead.

**Clamped, not rejected** (GRP-6(d)). A client asking for more values than the host will serve has
not malformed anything -- a set filter over a high-cardinality column asks for as many as it can get
by design -- so the compiled cap is `min(requested ?? default, maximum)` and the server answers.
Rejecting instead would make the maximum a contract the client has to know, which is the opposite of
what a server-side cap is for. The default (1,000) and the maximum (10,000) are **two separate**
host-configured SEC-3 values, not one number used twice: they answer different questions, and a host
raising the ceiling for an explicit request like this one has not thereby agreed to send 10,000
values to every set filter that opens.

Note this fixture's request is the *only* difference from `016`: same column, same empty filter
model, same ordering. Its five SQL arms therefore differ from `016`'s in exactly one token -- the
bound cap -- which is what makes the clamp the only thing it can be proving.

**Wire shape note:** `setValuesRequest` is **Craticula's own synthetic request shape**, not an AG Grid-defined format. AG Grid community edition has no fixed wire type for "get distinct values for a set filter" -- it delegates value resolution to a host-supplied callback (`values: (params) => void`) that the host implements however it likes. This fixture models a reasonable server-side wire contract for that callback so the compiler side (BM-3) has something concrete to compile against. See `group-aggregation/README.md` for the full shape rationale.

**Null-ordering note (SEM-3):** the compiled `ORDER BY`/`$sort`/`sort` clauses shown here are incidental to this case (needed only for deterministic grouping/paging, not because this case's own request/data exercises a null group value or null sort key) -- but SEM-3 requires every backend to *always* emit an explicit null-ordering position on a raw-field sort key, never left implicit, regardless of whether a given fixture's data actually contains a null. Postgres carries `NULLS LAST`; Mongo (no native null ordering) uses the null-bucket `$addFields`/`$sort`/`$unset` emulation pattern; Search's grouped composite aggregations set `missing_bucket: true` on every terms source so a null-valued group isn't silently dropped (ir.schema.json's `groupSpec` description explicitly anticipates a drilled-into null group bucket). SEM-3/SEM-6's own exhaustive property coverage stays in the dedicated `sort-paging/` category; this is only the minimal consistent application needed so these artifacts model real compiler output.
