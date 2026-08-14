# 017 — SEM-2 null-inclusion on `notContains`, composed with SEM-1 + SEM-7

**Proves:** SEM-2's null-inclusion rule applied to a different negative operator than `008`
(`neq`): `notContains`. Unlike `008`, this fixture does **not** dodge the SEM-1 interaction — a
negated substring match is simultaneously governed by all three rules at once (SEM-2's
null-inclusion, SEM-1's CI default, SEM-7's substring mechanism), and this fixture deliberately
composes them to prove they layer correctly rather than conflict.

- **Postgres**: `("lastName" NOT ILIKE '%' || $1 || '%' ESCAPE '\' OR "lastName" IS NULL)` — the
  negation is carried by the **negated operator** `NOT ILIKE`, not by an outer `NOT (...)` wrapper,
  matching how every other negative operator renders (`neq` → `<>`, `notIn` → `<> ALL`); the
  `ESCAPE` clause stays attached to the `NOT ILIKE` expression itself, never floating loose at the
  end of the `WHERE` clause; and the whole null-inclusive disjunction is parenthesised, per
  `spec/semantics.md`'s "Canonical SQL rendering" section.
- **MongoDB**: `$not` wrapping the case-insensitive `$regex`, composed with the same `$or`
  null-inclusion form SEM-2 pins for every negative operator.
- **Search**: a `bool`/`should` composing a negated `wildcard` (case-insensitive) with the
  negated-`exists` null-inclusion clause — mirroring SEM-2's own `bool`/`should` worked example
  structurally, built on `wildcard` instead of `term` because `notContains` needs SEM-7's substring
  mechanism, not term-level exact matching.

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6.

**Coverage tier:** all six backends — all four SQL dialects + MongoDB + Search. Promoted from
Postgres-only in #1639 (D2): SQL-3's "the negation goes into the operator" applied to four different
substring mechanisms at once, wrapped in SEM-2's null-inclusive disjunction — which is *structural*
and therefore byte-identical across the four arms apart from quoting.

**Requirement ids:** SEM-2, SEM-1, SEM-7, SEM-6 (tiebreaker, incidental).
