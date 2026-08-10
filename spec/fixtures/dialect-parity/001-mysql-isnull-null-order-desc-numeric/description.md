# 001 — MySQL `ISNULL` null-ordering emulation, `desc` sort, decimal field

**Proves:** SEM-3's MySQL-specific emulation mechanism in isolation: MySQL has no native `NULLS
FIRST`/`NULLS LAST` syntax, so `nullsPosition` is emulated with an `ISNULL(col)` prefix key (`1` for
NULL, `0` otherwise). The primary sort is `desc`, so the adapter's pinned default
(`nullsPosition: "first"` on `desc`) applies: sorting the prefix key `DESC` puts `1` (null) rows
ahead of `0` (non-null) rows — nulls first — while the real column keeps its own requested `DESC`
direction as the secondary key among non-null rows (SEM-3's Must-Not: "must NOT drop the original
field as secondary key"). The `id` stableKey tiebreaker (SEM-6) gets the identical treatment for its
own `nullsPosition: "last"` (direction always `asc` for the tiebreaker): `ISNULL(id) ASC, id ASC`.

**Dialect-specific mechanic:** MySQL only, per this suite's backend-coverage policy — this case pins
MySQL's `ISNULL` rendering specifically; see `006`-`010` for the cross-backend parity showcases that
prove the same rule holds identically across every backend.

**Requirement ids:** SEM-3, SEM-6 (tiebreaker mechanic, incidental to this case's focus).

**Resolved IR sort array (for reference, not a fixture file):**
```json
[
  { "field": "salary", "direction": "desc", "nullsPosition": "first" },
  { "field": "id", "direction": "asc", "nullsPosition": "last" }
]
```

Field type under test for the primary sort key: `decimal`.
