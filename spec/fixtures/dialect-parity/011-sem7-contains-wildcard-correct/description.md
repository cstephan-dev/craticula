# 011 — SEM-7 correct mechanism: `contains` compiles to `wildcard`, never `match`

**Proves:** SEM-7's pinned default in isolation, as the positive/correct half of a deliberate pair
with `012` (the anti-pattern documentation case): `contains`/`notContains` compile to
**substring-matching** query types on OpenSearch/Elasticsearch — here, `wildcard` against the
`.keyword` sub-field — never a tokenized `match` query.

Illustrative match (not backed by a `dataset.json` yet: seeds are authored under #1655, `spec/semantics.md` XEQ-1): a row with
`lastName = "Bartlett"` contains the literal substring `"art"`. The `wildcard` pattern below matches
it correctly, exactly mirroring what `LIKE '%art%'` (Postgres) and `$regex` (Mongo) also match —
this is precisely the `"Smart Watch"`/`"art"` scenario `spec/semantics.md`'s SEM-7 section itself
uses to motivate the rule, adapted to this suite's `employees` domain/`lastName` field. See `012`
for the same scenario applied to a field where the anti-pattern's failure becomes visible.

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6.

**Coverage tier:** all seven backends — all five SQL dialects + MongoDB + Search. Promoted from
Postgres-only in #1639 (D2). The earlier note here ("SQL Server/MySQL/SQLite would render identically
to `007`") was true of the *shape* and misleading about the *coverage*: the substring family is where
three engine-specific tokens land at once (the concat operator `+`/`CONCAT`/`||`, the collation's
presence and position, and D1's `ESCAPE` literal), and a second case exercising them against a
different column costs three small files.

**Requirement ids:** SEM-7, SEM-1 (CI default carried through unchanged), SEM-6 (tiebreaker, incidental).
