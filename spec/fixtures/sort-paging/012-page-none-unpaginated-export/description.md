# 012 — page spec `mode: "none"` (unpaginated), full-export compilation

**Proves:** EXP-2's compilation rule: `exportScope: "all"` (EXP-1's request extension) makes the
adapter ignore `startRow`/`endRow` entirely and resolve `page` to `$defs/nonePage`
(`{ "mode": "none" }`), so the compiler emits the full filtered+sorted artifact with **no**
row-range clause at all — no `LIMIT`/`OFFSET`, no `$skip`/`$limit`, no `from`/`size`. Compile-only
purity per EXP-2: streaming/chunking (EXP-3) is execution-adapter territory, out of scope for this
artifact. Also confirms SEM-3/SEM-6 apply identically under `page: none` — null ordering and the
`stableKey` tiebreaker are sort-clause concerns, orthogonal to which page mode is active.

**Two things differ from a ranged fixture, not one.** This request carries `exportFormat`, so it is
an *export*, and EXP-1(b) makes an export **name its output columns**: the Postgres arm renders
`SELECT "id", "created_at" AS "createdAt"` rather than `SELECT *`, and the Mongo arm ends in a
`{"$project": {"id": 1, "createdAt": 1, "_id": 0}}` stage. Only the Search arm is unchanged from an
ordinary row query, because that backend has no projection member at all (OSE-13). *(A previous
revision of this file said the page mode was the only difference, and its resolved-IR block listed
only `page` and `sort`. That was written before anything compiled this fixture; #1650 gave it the
projection its own category README already describes.)*

**Requirement ids:** EXP-1, EXP-1(b), EXP-2, SEM-3, SEM-6, AR-9 (`$defs/nonePage`, `$defs/pageSpec`).

**Resolved IR (for reference, not a fixture file):**
```json
{
  "page": { "mode": "none" },
  "sort": [
    { "field": "createdAt", "direction": "desc", "nullsPosition": "first" },
    { "field": "id", "direction": "asc", "nullsPosition": "last" }
  ],
  "projection": ["id", "createdAt"]
}
```

The `projection` key above is not an `ir.schema.json` member: EXP-1(b)'s column list is resolved by
the request adapter and handed to the compilers as `SqlQueryRequest.Projection` /
`MongoQueryRequest.Projection`. It is shown here because it is what the two artifacts render, and
because this schema declares exactly two fields, both exportable — so the list is the default
projection, not a wire-supplied `exportColumns`.

Field type under test for the primary sort key: `datetime`.
