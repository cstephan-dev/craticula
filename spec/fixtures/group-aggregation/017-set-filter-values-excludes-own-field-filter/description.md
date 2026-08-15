# 017 - Set-filter values, the target column's own filter is excluded

**Proves:** BM-3, GRP-6

`setValuesRequest.colId` = `"region"`, and the `filterModel` carries **two** entries: one on
`region` itself (`equals "EMEA"`) and one on a different column (`segment equals "Enterprise"`).
The compiled query drops the first and honors the second, which is BM-3's "the current filter model
minus the field's own filter" — GRP-6(b) in `spec/semantics.md`.

**Why the `region` entry is there, and why the artifacts do not show it.** This is the fixture that
pins the exclusion, so its request has to contain something for the exclusion to remove. Because the
rule removes it, the compiled SQL and params are **identical** to what they would be if it were
absent — which is exactly the property being asserted: a compiler that implements the exclusion and
one that has never heard of it produce different output for this request, and only the first matches
these artifacts.

> *Corrected in #1641.* This case previously carried **no** `region` entry at all, on the reading
> that the host had already stripped it before building the request. That made it a fixture whose
> name asserted a property its own data could not exercise: it proved another column's filter is
> honored and said nothing about the target column's being dropped, so a compiler that omitted the
> exclusion entirely passed it. The entry was added and **no artifact changed** — see
> `group-aggregation/README.md`'s design note, whose "never includes an entry keyed by `colId`
> itself" wording was corrected at the same time.

**Case-sensitivity (SEM-1 and GRP-6, in one statement).** `segment` is case-insensitive by schema
default, and this is an `eq`-family comparison (not a substring/prefix one) — SEM-1's
collation-vs-pattern-match split pins the **collation** mechanism here, never `ILIKE`/pattern-match
(that mechanism is reserved for `contains`/`notContains`/`startsWith`/`endsWith`, SEM-7's territory;
reusing it for `eq` is a trap, since a literal `%`/`_` in the value would silently widen into a
wildcard). Postgres compiles to `"segment" COLLATE craticula_ci = $1` (the project's pinned ICU
collation name, never `lower()`/`ILIKE`); Search's `term` query carries `case_insensitive: true`
directly on the original-cased value (`"Enterprise"`, never runtime-lowercased) — no normalized
sub-field needed, since `search.normalizedSubField` is only required for case-insensitive *ordering*
comparisons (`gt`/`gte`/`lt`/`lte`), not equality.

**Mongo is the exception, and deliberately so — see `spec/semantics.md`'s accepted divergence #3
(third shape).** This arm carries **no** query-level `collation`, so its `segment` `$match` compares
case-**sensitively** where the five SQL dialects collate it. MongoDB's `collation` is a *query-level*
option governing `$match`, `$group` and `$sort` together, with no per-stage form — so it cannot both
apply to the `segment` comparison SEM-1 wants collated and stay off the `$group` on `region` that
GRP-6(a) requires exact. Bucket identity wins: a folded bucket would return a value that matches
neither original row when the grid sends it back as an exact `IN` value or group key, whereas a
case-sensitive `segment` match is a narrower, visible difference in which rows come back.

Note the trap this arm originally fell into: it was first authored *with* the collation, because a
BM-3 request carries no `groupSpec` and so does not look like a grouping request — even though its
`$group` computes the distinct set. The test is "does this pipeline compute a distinct set", not
"does this request have a `groupSpec`".

The `region` key of the same statement is the opposite: `SELECT DISTINCT "region" … ORDER BY
"region" ASC NULLS LAST` renders bare, even though `region` is case-insensitive too, because it is a
**bucket-identity key** and GRP-6 makes it exact for GRP-2's reason. One statement, two mechanisms,
two different questions — see `016`'s note for the argument.

**Wire shape note:** `setValuesRequest` is **Craticula's own synthetic request shape**, not an AG Grid-defined format. AG Grid community edition has no fixed wire type for "get distinct values for a set filter" -- it delegates value resolution to a host-supplied callback (`values: (params) => void`) that the host implements however it likes. This fixture models a reasonable server-side wire contract for that callback so the compiler side (BM-3) has something concrete to compile against. See `group-aggregation/README.md` for the full shape rationale.

**Null-ordering note (SEM-3):** the compiled `ORDER BY`/`$sort`/`sort` clauses shown here are incidental to this case (needed only for deterministic grouping/paging, not because this case's own request/data exercises a null group value or null sort key) -- but SEM-3 requires every backend to *always* emit an explicit null-ordering position on a raw-field sort key, never left implicit, regardless of whether a given fixture's data actually contains a null. Postgres carries `NULLS LAST`; Mongo (no native null ordering) uses the null-bucket `$addFields`/`$sort`/`$unset` emulation pattern; Search's grouped composite aggregations set `missing_bucket: true` on every terms source so a null-valued group isn't silently dropped (ir.schema.json's `groupSpec` description explicitly anticipates a drilled-into null group bucket). SEM-3/SEM-6's own exhaustive property coverage stays in the dedicated `sort-paging/` category; this is only the minimal consistent application needed so these artifacts model real compiler output.
