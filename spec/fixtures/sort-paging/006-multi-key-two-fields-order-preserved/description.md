# 006 — multi-key sort (2 keys), order preserved, each key's own `nullsPosition` default

**Proves:** AR-9's sort array preserves `sortModel`'s array order exactly (the compiler never
reorders, groups, or dedupes the user's chosen keys), and SEM-3's adapter default is applied
**independently per key** — the second key's `desc` direction does not affect the first key's `asc`
default, and vice versa. Also SEM-6: neither `department` nor `salary` is the declared `stableKey`
(`id`), so the compiler appends it as the third and final key, after both user-chosen keys, with its
own `nullsPosition: "last"`.

**Requirement ids:** SEM-3, SEM-6, AR-9, AG-3.

**Resolved IR sort array (for reference, not a fixture file):**
```json
[
  { "field": "department", "direction": "asc", "nullsPosition": "last" },
  { "field": "salary", "direction": "desc", "nullsPosition": "first" },
  { "field": "id", "direction": "asc", "nullsPosition": "last" }
]
```

Field types under test for the primary sort keys: `string` (department), `decimal` (salary).
