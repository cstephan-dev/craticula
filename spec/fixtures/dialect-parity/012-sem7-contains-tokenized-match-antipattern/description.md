# 012 — SEM-7 anti-pattern documentation: the tokenized-`match` trap

**Proves:** SEM-7's Must-Not — "must NOT compile `contains`/`notContains` to a `match` query as the
default" — by naming and explaining the forbidden form explicitly, **in this file only**. The wrong
form below is never placed in `expected/`; `expected/` ships only the correct `wildcard` artifact,
identical in mechanism to `011`, applied to a different field (`department`) so this isn't a
duplicate of `011`.

**The trap.** Filtering `contains: "art"` against `department`, illustrative stored value
`"Smart Devices"` (not backed by `dataset.json`, per this suite's deferred policy — described here
for narrative purposes only): the naive/wrong compilation reaches for `match` because the operator
is literally named `contains` and `match` reads as "does it contain this":

```json
{ "match": { "department": "art" } }
```

This is **wrong**: `match` tokenizes both the indexed field and the query string into analyzer
tokens. `"Smart Devices"` analyzes into the whole tokens `"smart"`/`"devices"`, neither of which is
`"art"` — so this query returns **no hit** for that row, even though the identical filter compiled to
SQL (`LIKE '%art%'`) or Mongo (`$regex`) matches it. The same IR silently diverges across backends —
exactly the parity failure SEM-7 exists to prevent, and precisely why this form must never appear in
`expected/` as if it were a legitimate alternative artifact: it is not "another correct answer", it
is a bug this suite exists to catch.

**The correct artifact actually shipped in `expected/search.artifact.json`** (identical mechanism to
`011`): a `wildcard` query against the `.keyword` sub-field, `case_insensitive: true`,
`*art*` — this one **does** match `"Smart Devices"`, because it checks for the literal substring
rather than token overlap.

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6.

**Coverage tier:** all six backends — all four SQL dialects + MongoDB + Search. Promoted from
Postgres-only in #1639 (D2): the SQL arms carry the same per-dialect substring mechanics as `011`,
against a different column, which is what keeps the anti-pattern's *correct* half honest on every
engine rather than only on the reference one.

**Requirement ids:** SEM-7 (Must-Not compliance + anti-pattern documentation), SEM-6 (tiebreaker,
incidental).
