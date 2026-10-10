# 022 - Set-filter values, search prefix beside an active filter

**Proves:** BM-3, GRP-6(b), GRP-6(c)

`setValuesRequest.colId` = `"region"`, a `filterModel` carrying **two** entries (`region equals
"EMEA"` and `segment equals "Enterprise"`), **and** `searchPrefix: "eu"`. It is the only case in the
suite that puts the own-field exclusion and the search prefix **on the same column, in the same
statement** — `016`-`019` each carry one of the two and never both:

| | own-field entry in `filterModel` | other column's filter | `searchPrefix` |
|---|---|---|---|
| `016` | - | - | - |
| `017` | yes (`region`) | yes (`segment`) | - |
| `018` | - | - | yes |
| `019` | - | - | - |
| **`022`** | **yes (`region`)** | **yes (`segment`)** | **yes** |

That gap is why this case exists. GRP-6(c) pins the `WHERE` clause's **order** — the user's filter
leads, the prefix trails — and states it explicitly *because* no artifact carried both, which made a
normative sentence the only thing standing between two ports emitting `params` in opposite orders.
This fixture removes that asymmetry: `params` is compared exactly, so
`["Enterprise", "eu", 1000]` fails a compiler that emits `WHERE (<prefix> AND <filter model>)` even
though its SQL is semantically identical.

**The two rules act on the same column in opposite directions, and both must fire.**

- **GRP-6(b), the exclusion**: `region`'s own `equals "EMEA"` entry is dropped, exactly as in `017`.
  A value list narrowed by the selection already made on its own column collapses to what is already
  selected.
- **GRP-6(c), the prefix**: the `searchPrefix` is *also* on `region`, and must **survive** that
  exclusion — it is not part of the ambient filter model, it is the narrowing the user is typing into
  the set filter's own search box. A compiler that implements the exclusion by pruning every
  `region`-referencing predicate from the finished tree, rather than by pruning the filter model
  *before* injecting the prefix, loses it and fails every arm here.
- **GRP-6(a), unchanged by either**: `SELECT DISTINCT "region" … ORDER BY "region"` (`GROUP BY [region]` on
  SQL Server, GRP-6(f)) still renders
  bare. One statement therefore spells **three** different treatments of case: a collation on
  `segment` (SEM-1's exact-match mechanism), a pattern match on `region` (SEM-1's substring
  mechanism, via the prefix), and nothing at all on `region` again (GRP-6(a)'s bucket identity).
  That is not the mix SEM-1's third Must-NOT forbids — it is a filter, a filter, and a bucket key,
  which are three different questions.

**Per-backend derivation.** Every arm is `017`'s arm for the surviving `segment` predicate and
`018`'s arm for the prefix predicate, conjoined by SQL-2's composite rule (**the composite is
wrapped, its children are rendered bare**) and rebound in first-appearance order, with the row cap
still binding last:

- **Postgres** — `WHERE ("segment" COLLATE craticula_ci = $1 AND "region" ILIKE $2 || '%' ESCAPE
  '\')`. Note the `||` composition and the bare `"eu"` bound value, per `018`'s correction.
- **SQL Server** — the same shape with `[…]` quoting, the inline `COLLATE Latin1_General_CI_AS` on
  both text predicates (SEM-1's mechanism table), `+` for concatenation, `@pN` placeholders, the
  distinct set spelled `GROUP BY [region]` rather than `SELECT DISTINCT` (GRP-6(f): T-SQL refuses a
  `DISTINCT` whose `ORDER BY` is the `CASE WHEN … IS NULL` null-placement expression, msg 145), and `OFFSET 0 ROWS FETCH NEXT @p3 ROWS ONLY` for
  the cap (GRP-6(e) — T-SQL has no `LIMIT` and its `FETCH` is illegal without an `OFFSET`, whose
  zero is a **literal** so all five dialects still bind exactly one value for the cap — Oracle needs
  no such trick, since its `FETCH FIRST` is legal with no `OFFSET` before it).
- **MySQL** — backtick quoting, no spelled collation at all (the column's own declared collation is
  case-insensitive, SQL-6), `CONCAT(?, '%')`, `ESCAPE '\\'` with the doubled backslash, `ISNULL(…)`
  null placement, `LIMIT ?`.
- **SQLite** — `"…"` quoting, `COLLATE NOCASE` in SQLite's own per-construct position (after the
  bound value on the `eq`), nothing on the `LIKE` (SQLite's `LIKE` is already ASCII-case-insensitive),
  `|| '%'`, `LIMIT ?`.
- **Mongo** — the `$match` becomes an explicit `$and` of the two surviving conditions, in the same
  order (the suite's composite convention, `filter-tree/001`), with `018`'s `$regex`/`$options: "i"`
  for the prefix. It carries **no** query-level `collation`, so the `segment` `$eq` compares
  case-**sensitively** — `017`'s divergence, inherited: MongoDB's `collation` governs `$match`,
  `$group` and `$sort` together, so it cannot serve SEM-1 on `segment` without folding the `$group`
  on `region` that GRP-6(a) requires exact. See `spec/semantics.md`'s accepted divergence #3, third
  shape. The prefix is unaffected either way — `$options: "i"` is a regex-level flag and a
  query-level collation never applies to `$regex`.
- **Search** — `017`'s `bool.filter` `term` for `segment`, and `018`'s `terms.include: "eu.*"` for
  the prefix. **The order GRP-6(c) pins is not observable on this backend at all**: the two land in
  different parts of the request body (the `query` and the aggregation), so there is no sequence for
  a port to get backwards. `018`'s accepted divergence carries over verbatim — `include` is a Lucene
  regex evaluated at aggregation time with no case-insensitivity option in the pinned 7.10-era
  subset, and `region` declares no `search.normalizedSubField`, so the prefix match alone is
  case-**sensitive** here where Postgres/Mongo fold it.

**Wire shape note:** `setValuesRequest` is **Craticula's own synthetic request shape**, not an AG Grid-defined format. AG Grid community edition has no fixed wire type for "get distinct values for a set filter" -- it delegates value resolution to a host-supplied callback (`values: (params) => void`) that the host implements however it likes. This fixture models a reasonable server-side wire contract for that callback so the compiler side (BM-3) has something concrete to compile against. See `group-aggregation/README.md` for the full shape rationale.

**Null-ordering note (SEM-3):** the compiled `ORDER BY`/`$sort`/`sort` clauses shown here are incidental to this case (needed only for deterministic grouping/paging, not because this case's own request/data exercises a null group value or null sort key) -- but SEM-3 requires every backend to *always* emit an explicit null-ordering position on a raw-field sort key, never left implicit, regardless of whether a given fixture's data actually contains a null. Postgres carries `NULLS LAST`; Mongo (no native null ordering) uses the null-bucket `$addFields`/`$sort`/`$unset` emulation pattern; Search's grouped composite aggregations set `missing_bucket: true` on every terms source so a null-valued group isn't silently dropped (ir.schema.json's `groupSpec` description explicitly anticipates a drilled-into null group bucket). SEM-3/SEM-6's own exhaustive property coverage stays in the dedicated `sort-paging/` category; this is only the minimal consistent application needed so these artifacts model real compiler output.

**No SEM-6 tiebreaker, on any arm** (GRP-6(e)): the distinct column is unique across the result rows
by construction and the schema's `stableKey` is not in the one-column projection, so appending it
would be the invalid SQL GRP-4 avoids at a grouped level. A request carrying no `groupSpec` is *not*
sufficient reason to append one here.
