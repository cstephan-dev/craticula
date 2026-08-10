# 008 — stableKey tiebreaker appended, decimal primary sort key

**Proves:** SEM-6's core mechanic in isolation: the schema declares `stableKey: "id"`, the request
sorts only by `salary` (a `decimal` field, not required to be unique — many employees can share a
salary), and the compiler appends `id` as the trailing tiebreaker so paging over `salary ASC` alone
stays deterministic across page reads (the rule's own rationale: ties on a non-unique sort key are
non-deterministic at the database level without one). The appended key carries its own explicit
`nullsPosition: "last"` per SEM-3 (its direction is always `asc`) — `stableKey` is required to be
unique, not required to be `NOT NULL`, so the compiler cannot skip specifying a null position for it.

**Requirement ids:** SEM-6, SEM-3, AR-9, AG-3.

**Resolved IR sort array (for reference, not a fixture file):**
```json
[
  { "field": "salary", "direction": "asc", "nullsPosition": "last" },
  { "field": "id", "direction": "asc", "nullsPosition": "last" }
]
```

Field type under test for the primary sort key: `decimal`.
