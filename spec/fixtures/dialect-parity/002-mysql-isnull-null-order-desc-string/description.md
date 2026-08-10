# 002 — MySQL `ISNULL` null-ordering emulation, `desc` sort, string field

**Proves:** the same SEM-3 MySQL `ISNULL` prefix-key mechanism as `001`, applied to a `string` field
this time, to confirm the emulation is type-independent (the field's own comparison semantics —
including SEM-1's default case-insensitivity, carried here by `lastName`'s own declared `_ci`
collation, e.g. `utf8mb4_0900_ai_ci` — are completely orthogonal to how its NULLs are positioned;
this fixture does not exercise or assert anything about that collation, it is mentioned only so the
plain `ORDER BY lastName DESC` clause below isn't mistaken for an oversight). `desc` sort → adapter
pins `nullsPosition: "first"` → `ISNULL(lastName) DESC, lastName DESC`. The `id` stableKey
tiebreaker (SEM-6) is appended with the identical prefix-key treatment for its own
`nullsPosition: "last"`.

**Dialect-specific mechanic:** MySQL only, per this suite's backend-coverage policy.

**Requirement ids:** SEM-3, SEM-6 (tiebreaker mechanic, incidental to this case's focus).

**Resolved IR sort array (for reference, not a fixture file):**
```json
[
  { "field": "lastName", "direction": "desc", "nullsPosition": "first" },
  { "field": "id", "direction": "asc", "nullsPosition": "last" }
]
```

Field type under test for the primary sort key: `string`.
