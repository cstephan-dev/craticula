# 024 — AGG-6: `sum` over a group with no non-null values is `0`, `avg` is still `null`

**Proves:** AGG-6, GRP-4, SEM-3, SEM-6, AR-8, AR-9

A one-level grouped request (`rowGroupCols` = [`department`], `groupKeys` = []) aggregating
**two different functions over two different fields** — `SUM(salary)` and `AVG(age)` — and ordering
the grouped level by the **`avg`**. That combination is the whole point of the case; each half on its
own is already pinned elsewhere and neither half alone can catch the mistake this one is aimed at.

## What only this case can fail on

AGG-6 makes `sum` the single aggregate whose SQL rendering has to change: the engines already return
`null` for `avg`/`min`/`max` over a group with no non-null values, and already return `0` for
MongoDB's `$sum` and Elasticsearch's `sum`, but `SUM(x)` returns NULL — so the SQL compilers, and
only they, wrap **`sum` and nothing else**. Two symmetrical ways to get that wrong, both of which
compile and both of which produce a perfectly plausible statement:

- **Under-application** — leaving `SUM(salary)` bare. Every grouped artifact in the suite that
  carries a `sum` catches this; it is not what this fixture is for.
- **Over-application** — wrapping all five functions "for consistency". Nothing catches that in the
  `ORDER BY`, because **no other fixture in the suite orders a grouped level by an `avg`, `min` or
  `max`**: `group-aggregation/020` orders by a `sum` (where the wrap is correct), and `012`/`013`
  put an `avg`/`min`/`max` only in the projection. Here the unwrapped `AVG("age")` appears in the
  select list *and* twice more in the ordering path, beside a wrapped `COALESCE(SUM("salary"), 0)` in
  the same statement — so a compiler that wraps uniformly fails on the same line it gets `sum` right.

## Why the null-ordering machinery is the second half of the point

`ORDER BY COALESCE(SUM(x), 0)` places a group with no non-null values among the **zeroes**, where
`ORDER BY SUM(x)` placed it with the **NULLs** — an ordering change, not only a value change, and
AGG-6 states it rather than leaving it to be discovered. That makes SEM-3's null marker inert on a
`sum` key (it is still emitted: SEM-6 licenses no omission on a nullability inference). On this
fixture's `avg` key it is fully **live** — an all-null `age` group really does produce NULL — so the
two dialects that emulate null placement spell a real, load-bearing bucket key here:
`ISNULL(AVG(\`age\`)) ASC` on MySQL and `CASE WHEN AVG([age]) IS NULL THEN 1 ELSE 0 END ASC` on SQL
Server, wrapping the aggregate **expression** per GRP-4, never its select-list alias.

`asc` → `nullsPosition: "last"` is the adapter's pinned SEM-3 mapping, and the group column
(`department`, `asc`, nulls last) is appended as the SEM-6 tiebreaker — a grouped row's tiebreaker is
its group column, never the leaf `stableKey`, which the grouped projection does not contain.

## Coverage tier: five SQL dialects, no document-store arm

A third tier for this category, and deliberately not an oversight. The two document stores are
**already conformant with AGG-6 and emit nothing that would show it**: their `$sum` / `sum` metric is
the same token before and after the rule, so a Mongo or Search arm here would be a copy of
`group-aggregation/012`'s with different field names and would grade nothing this fixture exists to
grade. On the SQL side the rule is *visible in the emitted text*, on all five dialects at once, which
is exactly what this category's parity tier is for — and `COALESCE` being ANSI is itself the parity
claim: it is spelled identically by all five, so it stays in the builder rather than becoming a sixth
`ISqlDialect` member.

The honest limit, recorded here as it is in `spec/semantics.md`: the *engine* halves of AGG-6 on
MongoDB and ES/OpenSearch are documented behaviour, not measured in this repository. Confirming them
belongs to the T-1b execution tier (**#1624**).

## Parameters

`startRow: 0`/`endRow: 100` → the category's usual `LIMIT`/`OFFSET` pair, in each dialect's own order
(`[100, 0]` where the limit binds first, `[0, 100]` on SQL Server and Oracle's `OFFSET … FETCH`).
Nothing here binds a filter value: the case carries no `filterModel`, so any difference between the
five arms is grouping and ordering syntax alone.
