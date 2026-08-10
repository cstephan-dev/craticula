# 001 — single-key sort, string field, asc, adapter-pinned `nullsPosition: "last"`

**Proves:** SEM-3 (null ordering) — AG Grid's `sortModel` carries no null-position signal at all
(`{ colId, sort }` only); the adapter fills `nullsPosition` itself, and for `asc` that pinned value
is `"last"`. Also exercises SEM-6 (deterministic paging): the user's sort (`lastName` only) doesn't
end in the schema's declared `stableKey` (`id`), so the compiler appends `id` as a trailing
tiebreaker with its own explicit `nullsPosition: "last"` (its direction is always `asc`).

**Requirement ids:** SEM-3, SEM-6, AR-9, AG-3 (`$defs/sortItem`, `$defs/nullsPosition`,
`$defs/rangePage`).

**Resolved IR sort array (for reference, not a fixture file):**
```json
[
  { "field": "lastName", "direction": "asc", "nullsPosition": "last" },
  { "field": "id", "direction": "asc", "nullsPosition": "last" }
]
```

Field type under test for the primary sort key: `string`.
