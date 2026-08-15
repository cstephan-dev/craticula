# 018 - Set-filter values, search prefix

**Proves:** BM-3, GRP-6, SEM-7

`setValuesRequest.searchPrefix` = `"eu"` -- the set-filter's own search box narrows the
candidate values (e.g. typing "eu" while browsing region values). No other active filters.
GRP-6(c) makes this an **ordinary `startsWith` predicate on the target column**, not a construct of
its own: SEM-1 picks the field's own mechanism for it and SEM-7 owns its escaping, exactly as they
would for a `startsWith` the user typed into `region`'s own text filter. So every backend's own
substring/prefix mechanism applies, and the artifacts are the same shapes `operators/027` pins:

- **Postgres** -- `ILIKE $1 || '%' ESCAPE '\'`, binding the escaped literal `"eu"`. The trailing
  `%` is composed in SQL and the **bound value carries no pattern syntax**: SEC-4's escaping of the
  prefix's own `%`/`_`/`\` (none present in `"eu"`, but the mechanism is unconditional) is what makes
  a literal `%` typed into the search box stay literal, and appending the wildcard to the *value*
  instead would put pattern syntax and user text in the same string. The `ESCAPE '\'` clause is
  mandatory (SEM-7) -- without it, the escaping is silently a no-op on dialects with no default
  `LIKE` escape character.

  > *Corrected in #1641.* This arm previously read `ILIKE $1 ESCAPE '\'` with `params: ["eu%"]`,
  > i.e. the pattern built inside the bound parameter. That contradicted the suite's own canonical
  > `startsWith` rendering — `operators/025`-`028` pin the composed form on each of their three
  > backends (Postgres + Mongo + Search, this suite's default coverage for a general/structural
  > case) — and SQL-3/SEM-7 describe it. One artifact against twelve is not a disagreement about
  > which is canonical, so this one was re-authored. *(The counts read "six backends" and
  > "twenty-four" until #1642's review pass; `operators/` carries no per-dialect arms at all, so both
  > were an over-count of the same fixtures.)* Its **mongo** arm was already identical to
  > `operators/027`'s and its **search** arm is a different mechanism entirely (below); neither
  > changed.

- **SQL Server / MySQL / SQLite** -- the same predicate in each engine's own spelling, and every one
  of them carries the mandatory `ESCAPE` clause, since SEM-7's escaping is the mechanism rather than
  a Postgres detail: `` COLLATE Latin1_General_CI_AS LIKE @p1 + '%' ESCAPE '\' `` (T-SQL concatenates
  with `+`, and needs the inline collation SEM-1's mechanism table gives it, exactly as
  `dialect-parity/007` spells it for `contains`), `` LIKE CONCAT(?, '%') ESCAPE '\\' `` (MySQL's `||` is
  logical OR, and its `ESCAPE` literal needs the doubled backslash),
  `LIKE ? || '%' ESCAPE '\'` (SQLite, whose `LIKE` is ASCII-case-insensitive already).

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

**The prefix folds case; the values it returns do not.** The `WHERE` clause matches `region`
case-insensitively (SEM-1) while `SELECT DISTINCT "region" … ORDER BY "region"` stays exact (GRP-6).
That is the intended behaviour and not the SEM-1 mix it resembles: typing `eu` must find `"EU"`,
`"Europe"` and `"europe"` alike, and then offer each of them as its own entry, because those are the
values the data actually holds and the grid will send one of them straight back as an `in` value.

**Wire shape note:** `setValuesRequest` is **Craticula's own synthetic request shape**, not an AG Grid-defined format. AG Grid community edition has no fixed wire type for "get distinct values for a set filter" -- it delegates value resolution to a host-supplied callback (`values: (params) => void`) that the host implements however it likes. This fixture models a reasonable server-side wire contract for that callback so the compiler side (BM-3) has something concrete to compile against. See `group-aggregation/README.md` for the full shape rationale.

**Null-ordering note (SEM-3):** the compiled `ORDER BY`/`$sort`/`sort` clauses shown here are incidental to this case (needed only for deterministic grouping/paging, not because this case's own request/data exercises a null group value or null sort key) -- but SEM-3 requires every backend to *always* emit an explicit null-ordering position on a raw-field sort key, never left implicit, regardless of whether a given fixture's data actually contains a null. Postgres carries `NULLS LAST`; Mongo (no native null ordering) uses the null-bucket `$addFields`/`$sort`/`$unset` emulation pattern; Search's grouped composite aggregations set `missing_bucket: true` on every terms source so a null-valued group isn't silently dropped (ir.schema.json's `groupSpec` description explicitly anticipates a drilled-into null group bucket). SEM-3/SEM-6's own exhaustive property coverage stays in the dedicated `sort-paging/` category; this is only the minimal consistent application needed so these artifacts model real compiler output.
