# 024 - Leaf rows under the null group

**Proves:** GRP-7, BM-2

`rowGroupCols` = [region], `groupKeys` = [`nullValue`] -- the leaf query of the group whose key is null. GRP-5 turns the
key into `eq` against `region` with a `nullValue` operand, and GRP-7 renders that as the null test on every backend:
`"region" IS NULL` on SQL, `must_not exists` on Search, `{region: {$eq: null}}` on MongoDB. No parameter is bound for it.

**What it discriminates.** Before GRP-7 SQL bound the null as a parameter (`"region" = $1`, which matches no row), Search
sent a `term` with a null value (refused by the engine with HTTP 400) and EF Core refused the request; only MongoDB
answered the group's rows. The dataset also holds a row whose region is the **empty string**: it is a different group at the
grouped level, so it must not appear here (`blank` would have returned it). The null group's rows come back in `id` order,
the SEM-6 tiebreaker, one of them with every other column null as well.
