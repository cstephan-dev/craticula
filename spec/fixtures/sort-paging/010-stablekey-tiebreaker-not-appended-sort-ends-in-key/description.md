# 010 — stableKey tiebreaker NOT appended, user's sort already ends in it

**Proves:** SEM-6's guard against duplicating the tiebreaker: the request's own `sortModel` already
ends in the declared `stableKey` (`lastName ASC` then `id ASC`), so the compiler must NOT append a
second `id` key — the resulting IR `sort` array has exactly two entries, not three, and the `id`
entry present is the *user's own* sort key (compiled the same way any other explicit key would be),
not a synthesized tiebreaker. This is the negative case for 001-009's positive appended-tiebreaker
behavior — it guards against an implementation that appends unconditionally without checking whether
the sort already ends in `stableKey`.

**Requirement ids:** SEM-6, SEM-3, AR-9, AG-3.

**Resolved IR sort array (for reference, not a fixture file) — no duplicate `id` key:**
```json
[
  { "field": "lastName", "direction": "asc", "nullsPosition": "last" },
  { "field": "id", "direction": "asc", "nullsPosition": "last" }
]
```
