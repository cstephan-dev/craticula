# 012 — page spec `mode: "none"` (unpaginated), full-export compilation

**Proves:** EXP-2's compilation rule: `exportScope: "all"` (EXP-1's request extension) makes the
adapter ignore `startRow`/`endRow` entirely and resolve `page` to `$defs/nonePage`
(`{ "mode": "none" }`), so the compiler emits the full filtered+sorted artifact with **no**
row-range clause at all — no `LIMIT`/`OFFSET`, no `$skip`/`$limit`, no `from`/`size`. Compile-only
purity per EXP-2: this is the same core compilation path as every ranged fixture, with only the
page mode differing; streaming/chunking (EXP-3) is execution-adapter territory, out of scope for
this artifact. Also confirms SEM-3/SEM-6 apply identically under `page: none` — null ordering and
the `stableKey` tiebreaker are sort-clause concerns, orthogonal to which page mode is active.

**Requirement ids:** EXP-1, EXP-2, SEM-3, SEM-6, AR-9 (`$defs/nonePage`, `$defs/pageSpec`).

**Resolved IR (for reference, not a fixture file):**
```json
{
  "page": { "mode": "none" },
  "sort": [
    { "field": "createdAt", "direction": "desc", "nullsPosition": "first" },
    { "field": "id", "direction": "asc", "nullsPosition": "last" }
  ]
}
```

Field type under test for the primary sort key: `datetime`.
