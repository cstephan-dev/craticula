# 025 - A grouped level under a null key

**Proves:** GRP-7, BM-2

`rowGroupCols` = [region, segment, product], `groupKeys` = [`nullValue`] -- the second level of the group whose region is
null. GRP-5 turns the key into `eq` against `region` with a `nullValue` operand and GRP-7 renders it as the null test
(`"region" IS NULL`, `must_not exists`, `{region: {$eq: null}}`), with no bound parameter. The buckets are `segment`, in
ascending order with the null bucket last.

**What it discriminates.** A drill-down compiled as ordinary equality matches no row on SQL and is refused by Search, so the
children of a null group could not be shown at all. Two dataset rows guard the edges: a row whose region is the empty
string (a different group; it would add 1000.00 to Enterprise if the test were `blank`) and a bucket whose only amount is null
(AGG-6, sum 0).

**On Oracle.** SQL-7 stores the empty string as NULL, so the 1000.00 of `n06` joins the null group and the Enterprise
bucket sums to 1125.50 there, declared as an XEQ-4 `rows` divergence in `expectedRows.json`.
