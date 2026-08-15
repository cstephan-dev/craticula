# 009 — Cross-backend parity showcase: SEM-3 null ordering, `asc`

**Proves:** SEM-3's rule — every backend honors an explicit `nullsPosition` identically — for the
`asc` → adapter-pinned `nullsPosition: "last"` case, across all seven backends at once. Primary sort
key `salary` (`decimal`) does not end in the declared `stableKey` (`id`), so SEM-6 appends `id` as
a trailing tiebreaker (`asc`, `nullsPosition: "last"`, incidental to this fixture's SEM-3 focus but
included for a realistic compiled shape, matching `sort-paging`'s own convention).

- **Postgres/SQLite/Oracle**: native `ORDER BY salary ASC NULLS LAST, id ASC NULLS LAST` (SQLite has
  supported `NULLS FIRST`/`NULLS LAST` natively since 3.30.0; Oracle has always had them). The
  emulating engines are the minority here, not the majority — three of five spell it natively.
- **SQL Server**: no native syntax — `CASE WHEN salary IS NULL THEN 1 ELSE 0 END ASC` prefix key
  (see `003`'s note on why this can't reuse the name `ISNULL`), applied to both sort keys.
- **MySQL**: `ISNULL(salary) ASC` prefix key (see `001`), applied to both sort keys.
- **MongoDB**: the null-bucket `$addFields`+`$sort` emulation — one `__<field>NullBucket` per sort
  key, bucket sign `1` (ascending) since `nullsPosition` is `"last"` for both keys, in the
  bucket-then-field pair order this suite standardizes on (see this category's `README.md`).
- **Search**: native `sort[].missing: "_last"` on both keys — no emulation needed.

**Coverage tier:** cross-backend parity showcase — all five SQL dialects + Mongo + Search.

**Requirement ids:** SEM-3, SEM-6 (tiebreaker, incidental), AR-9, AG-3.
