# 014 — SEM-1 per-field case-**sensitive** opt-in, `eq`

**Proves:** SEM-1's SC-3 per-field override in the opposite direction from `006`: when a field's
`text.caseSensitivity` is explicitly `"sensitive"`, **none** of the CI mechanisms from `006` apply —
no `ILIKE`, no CI collation, no Mongo `collation` option, no ES `case_insensitive` parameter. Plain
`=`/`$match` equality / a plain `term` query, matching each engine's own case-sensitive default
directly.

This is the deliberate per-field override SEM-1's second Must-Not protects: a compiler must NOT
default to case-sensitive "because the engine's own default happens to be case-sensitive" — but
case-sensitive comparison IS correct once a field is explicitly configured that way (SC-1's
allowlist-and-config model puts that decision in `schema.json`, never inferred implicitly).

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6.

**Coverage tier:** general/semantic fixture — Postgres + MongoDB + Search.

**Requirement ids:** SEM-1, SC-3, SEM-6 (tiebreaker, incidental).
