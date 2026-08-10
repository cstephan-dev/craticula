# 011 — page spec `mode: "range"` (startRow/endRow), sort + tiebreaker for deterministic paging

**Proves:** AG-3's `startRow`/`endRow` → `$defs/rangePage` mapping, requested at a non-zero offset
(`startRow: 20, endRow: 40`, a "second page" of size 20) — this is v1's only paging mechanism
(SEM-6's "must NOT" explicitly rules out treating the `stableKey` tiebreaker as license to switch to
keyset paging; it stays offset-based always). Combines directly with SEM-6: the tiebreaker is what
makes *this* offset read return the same 20 rows on every repeated call with the same filter+sort,
since `lastName` alone is not guaranteed unique.

**Requirement ids:** AG-3, SEM-6, SEM-3, AR-9 (`$defs/rangePage`).

**Resolved IR (for reference, not a fixture file):**
```json
{
  "page": { "mode": "range", "startRow": 20, "endRow": 40 },
  "sort": [
    { "field": "lastName", "direction": "asc", "nullsPosition": "last" },
    { "field": "id", "direction": "asc", "nullsPosition": "last" }
  ]
}
```
