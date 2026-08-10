# 020 — SEM-7 Mongo `$regex`: full metacharacter escaping, not just SEC-4's LIKE-wildcard set

**Proves:** SEM-7's Must-Not — "must NOT build the MongoDB `$regex` value by applying only SEC-4's
LIKE/wildcard escape set; regex metacharacters are a distinct, wider class ... and must be escaped
in full before insertion" — using a filter value containing characters from **both** escape classes
(and one from neither), so the distinction is visible in the artifacts themselves rather than only
asserted in prose (contrast with `007`/`011`/`013`, whose literal `"smith"`/`"art"`/`"engineer"`
values happen to contain no characters needing escaping under either class, so the escaping
requirement there is a no-op that can't be *seen*).

Filter value: `"50% off (bonus)"` on a free-text `remarks` field, `contains`.

- `%` is a SQL `LIKE` wildcard (SEC-4's set) — meaningless to a regex engine.
- `(` / `)` are regex metacharacters (would otherwise open/close a capture group) — meaningless to
  `LIKE`, not in SEC-4's set at all.
- The space is escaped by the Mongo mechanism but not the SQL one (SEC-4 doesn't cover whitespace;
  full regex-metacharacter escaping conservatively backslash-escapes every character outside
  `[A-Za-z0-9]`, which does).

**Postgres**: SEC-4 escapes only the `%` → `50\% off (bonus)`; the parentheses and space are left
completely literal (`LIKE` gives them no special meaning), plus the mandatory `ESCAPE` clause.

**MongoDB**: the full-metacharacter escape (every character outside `[A-Za-z0-9]`) turns the same
input into `50\%\ off\ \(bonus\)` — the parentheses and space are backslash-escaped here, going well
beyond SEC-4's narrower set, exactly the distinction this rule's Must-Not names explicitly.

**Search**: the `wildcard` pattern needs no escaping at all in this case, since ES/OpenSearch's own
wildcard-syntax special characters (`*`, `?`) don't appear in this input — included for contrast, a
**third**, still-different escape alphabet from the same source value.

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6.

**Coverage tier:** general/semantic fixture — Postgres + MongoDB + Search.

**Requirement ids:** SEM-7, SEC-4 (contrast only, not this suite's own rule), SEM-6 (tiebreaker,
incidental).
