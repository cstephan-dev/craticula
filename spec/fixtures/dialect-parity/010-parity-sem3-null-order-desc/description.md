# 010 — Cross-backend parity showcase: SEM-3 null ordering, `desc`

**Proves:** the mirror case of `009` — `desc` → adapter-pinned `nullsPosition: "first"` — across all
seven backends. Same `salary` primary key / `id` tiebreaker setup as `009`; only the primary key's
direction and null placement flip (the tiebreaker's own `nullsPosition: "last"` never changes,
since the tiebreaker is always ascending per SEM-6).

- **Postgres/SQLite/Oracle**: `ORDER BY salary DESC NULLS FIRST, id ASC NULLS LAST` — native on all
  three, so the direction flip below is an emulation detail of two engines, not the general rule.
- **SQL Server**: `CASE WHEN salary IS NULL THEN 1 ELSE 0 END DESC` — flipping the prefix key's own
  sort direction (not the real column's) is what turns "nulls last" into "nulls first" (SEM-3's
  Must-Not: never drop the real column as the secondary key; only the prefix key's sign changes).
- **MySQL**: `ISNULL(salary) DESC` — same prefix-key-direction-flip mechanism.
- **MongoDB**: bucket sign flips to `-1` for the `salary` key (still `1` for the `id` tiebreaker,
  whose own `nullsPosition` stays `"last"`), independent of the field's own `-1` (descending) sort
  sign — the bucket sign and the field's own sign are two separate, independently-set numbers.
- **Search**: `sort[].missing: "_first"` on the primary key, `"_last"` on the tiebreaker.

**Coverage tier:** cross-backend parity showcase — all five SQL dialects + Mongo + Search.

**Requirement ids:** SEM-3, SEM-6 (tiebreaker, incidental), AR-9, AG-3.
