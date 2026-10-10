# 025 — SQLite compares a datetime as text, so its bound carries the stored width

**Proves:** SEM-4's SQLite rule. SQLite has no timestamp type; a `datetime` column is TEXT and `<=`, `>`
compare it character by character. `.` sorts before `Z`, so a stored `2026-01-01T00:00:00.001Z` sorts
*before* a bound spelled `2026-01-01T00:00:00Z`: `gt` on that bound drops a later row and `lte` keeps one.
The host therefore stores exactly three fractional digits (`yyyy-MM-ddTHH:mm:ss.fffZ`, UTC) and the SQLite
arm binds every whole-millisecond instant in the same form, so `2026-01-01T00:00:00Z` is bound as
`2026-01-01T00:00:00.000Z`. The other four SQL dialects keep the whole-second form (`operators/041`).

The request is one `or` over the three operators the bug touched: `lte` at the start of the day, an
`inRange` (exclusive from, inclusive to) from noon to the end of the day, and `gt` at the end of the day.
Every bound is a whole second; every row sits on, one millisecond either side of, or well away from one.
`expectedRows.json` is derived by hand from SEM-4 (the instant comparison) and SEM-2 (null never matches
a range or ordering comparison), never from an engine run: rows 1, 2, 6, 7 and 8 match; row 3 (past the
`lte` bound), row 4 (on the exclusive bound) and row 5 (null) do not. The old bound form would also keep
row 3 (`lte`) and lose row 8 (`inRange`; row 7 survives only because the `inRange` upper bound wrongly admits it), which is what the execution tier shows when the dialect's
`DatetimeBoundsAreFixedWidth` is reverted.

**Host obligation:** the seed stores `...:00.000Z` for a whole second (XEQ-2's execution tier does). The
EF Core adapter hands its provider a `DateTime` and is outside this rule (AR-4's same-instant rule).

**Dialect-specific mechanic:** SQLite only, per this suite's backend-coverage policy (like `001`-`005`).

**Requirement ids:** SEM-4, SEM-2, XEQ-2.
