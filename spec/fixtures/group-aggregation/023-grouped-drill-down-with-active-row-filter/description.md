# 023 - Grouped level with consumed group keys AND an active row filter

**Proves:** GRP-5, BM-2, AR-5, AR-8

`003`'s request verbatim — `rowGroupCols` = [region, segment, product], `groupKeys` = ["EMEA",
"Enterprise"], `valueCols` = [`amount` sum], its schema reused unchanged — plus a **`filterModel`
carrying two entries** (`fiscalYear >= 2023` and `quantity < 500`).

## Why this case exists (#1644 review pass)

Until it was authored, **no fixture anywhere in the suite carried both a non-empty `groupKeys` and an
active `filterModel`/`customFilters`** — measured over every `agGridRequest.json` in every category,
this is still the only one:

| | `groupKeys` consumed | active `filterModel` |
|---|---|---|
| `001` | - | - |
| `002`-`004` | yes | - |
| `020` | - | yes |
| **`023`** | **yes** | **yes** |

GRP-5's clause **order** — "the synthesised drill-down leads, the user's own filter follows" — was
therefore normative prose with no artifact behind it, **on all three backends at once**, and
`spec/semantics.md`'s OSE-3 cited `group-aggregation/003`/`004` for it although neither of those has a
user filter to order against. A reviewer's mutation confirmed the gap: reversing the two composition
calls in `SearchQueryBuilder.RenderQuery` passed the whole 904-test Search suite. The same reversal is
invisible on the SQL and Mongo suites for the same reason.

The order is visible in every arm here: in `params` for the five SQL dialects (`["EMEA",
"Enterprise", 2023, 500, …]` and not `[2023, 500, "EMEA", "Enterprise", …]`), in the `$and` element
order for Mongo, and in the `bool.filter` array order for Search — all three compared as written.

**Both halves are two-condition conjunctions on purpose.** The drill-down is an `and` of two consumed
keys and the filter model is an `and` of two columns, which is what makes the second thing this
fixture pins observable: **how each backend composes two conjunctions**. They do it three different
ways, and each follows that backend's own already-normative composite rule rather than anything new:

- **SQL (SQL-2)** — every composite wraps and its children render bare, so two nested conjunctions are
  two nested parenthesised groups: `WHERE ((a AND b) AND (c AND d))`. Not flattened to `(a AND b AND c
  AND d)`; SQL-2's non-collapsing rule is what `filter-tree/011`-`013` pin against SEC-3's depth
  budget.
- **MongoDB (MDB-2)** — the same, spelled as `{"$and": [{"$and": [a, b]}, {"$and": [c, d]}]}`.
- **Search (OSE-3)** — the `bool.filter` **array is** the conjunction, so a top-level `and` on either
  side contributes its *children* to it and the four clauses sit side by side. This is the one
  backend where the two halves flatten into one list, and until this fixture existed that flattening
  was pinned only for the drill-down half (`003`/`004`) and never for a user filter beside one.

## Per-backend derivation

Every arm is `003`'s arm for the drill-down and grouped level, with the user filter conjoined by the
backend's own composite rule and the parameters rebound in first-appearance order:

- **Postgres** — `WHERE (("region" = $1 AND "segment" = $2) AND ("fiscal_year" >= $3 AND "quantity" <
  $4))`. The drill-down predicates carry **no `COLLATE craticula_ci`** although `region` and `segment`
  are declared case-**insensitive**: GRP-2 makes a group-key match exact, exactly as in `003`. The
  user filter's two columns are `int64`, so SEM-1 never applies to them either — deliberately, so this
  fixture isolates clause **order and composition** instead of re-testing the SEM-1-versus-GRP-2
  interaction that `005`, `017` and `022` already pin.
- **SQL Server / MySQL / SQLite / Oracle** — the Postgres arm with each engine's identifier quoting,
  placeholder token, null-ordering mechanism and row-window syntax substituted, exactly as for `003`:
  `[…]`/`@pN`/`CASE WHEN … IS NULL`/`OFFSET … FETCH NEXT`, `` `…` ``/`?`/`ISNULL(…)`/`LIMIT … OFFSET`,
  `"…"`/`?`/`NULLS LAST`/`LIMIT … OFFSET`, and `"…"`/`:pN`/`NULLS LAST`/`OFFSET … FETCH NEXT`. Note
  the two placeholder orders: the three `LIMIT` dialects bind the count before the offset and the two
  `OFFSET … FETCH` ones bind the offset first, which is a row-window spelling and not a clause-order
  difference.
- **Mongo** — `003`'s pipeline with the `$match` body wrapped as above. No query-level `collation`:
  nothing in this request wants one (the drill-down is exact by GRP-2 and the user filter is numeric),
  so this is *not* an instance of accepted divergence #3 — there is nothing being suppressed.
- **Search** — `003`'s body with two `range` clauses appended to the `bool.filter` array. `fiscalYear`
  and `quantity` declare no `keywordSubField`, so both `range` queries address the base field (OSE-6's
  last row); the group level is unchanged from `003`, including `missing_bucket: true` and the absent
  `order` (ascending is the composite source's own default, OSE-11(c)).

**Sort:** `sortModel` is empty, so the grouped level's only ordering key is SEM-6's appended
tiebreaker, which at a grouped level is the **group column** (`product`) rather than the schema's
`stableKey` (GRP-4). That is `003`'s ordering verbatim, and it is deliberately unchanged: ordering a
grouped level by an aggregate is a separate question, and one this suite records as unanswerable on
Search — see `020`.

**Null-ordering note (SEM-3):** as in `003`, the null-ordering markers here are structural rather than
exercised by this case's data — Postgres/SQLite/Oracle spell `NULLS LAST`, SQL Server and MySQL
emulate it, Mongo uses the `__groupNullBucket` `$addFields`/`$sort`/`$unset` trio, and Search's
composite source carries `missing_bucket: true` so a null group is not silently dropped. SEM-3's own
exhaustive coverage lives in `sort-paging/`.
