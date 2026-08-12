# 019 — SEM-2 `notBlank` as `blank`'s exact Boolean negation

**Proves:** SEM-2's rule that `notBlank` MUST be `blank`'s literal negation, never a separately
hand-derived condition (SEM-2's Must-Not: "must NOT derive `notBlank` independently from `blank`
... always negate the compiled `blank` condition so the two can never drift apart"). Every artifact
below is rendered as an **explicit negation wrapping `blank`'s own compiled form**, to make that
mechanism visible rather than its algebraically-equivalent expansion:

- **Postgres**: `NOT (("lastName" IS NULL OR "lastName" = ''))` — the inner parentheses are
  `blank`'s own compiled form, reproduced verbatim, and the outer ones are `NOT`'s; the double pair
  is what makes "negate `blank`'s output" visible in the text rather than merged away. Not the
  De Morgan-expanded
  `"lastName" IS NOT NULL AND "lastName" <> ''` (functionally identical once evaluated, but the
  negated form is what this rule requires the compiler to actually *construct*; the AND-form, if a
  dialect renderer chooses to simplify to it, must be *derived from* this negation, never written
  independently).
- **MongoDB**: `$nor` — Mongo's native "not any of these" operator, the direct negation of
  `blank`'s own `$or` form (`{ $or: [{ lastName: null }, { lastName: "" }] }`).
- **Search**: a `bool.must_not` wrapping `blank`'s own compiled `bool.should` query verbatim (field
  missing, via negated `exists`, OR an empty-string `term`).

`blank`'s own text-specific rule (`IS NULL OR = ''`) applies here since `lastName` is a `string`
field (SEM-2's fourth Must-Not: never apply the `= ''` clause to a non-text type).

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6.

**Coverage tier:** general/semantic fixture — Postgres + MongoDB + Search.

**Requirement ids:** SEM-2, SEM-6 (tiebreaker, incidental).
