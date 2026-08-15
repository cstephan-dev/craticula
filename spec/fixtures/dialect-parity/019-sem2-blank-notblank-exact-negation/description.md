# 019 — SEM-2 `notBlank` as `blank`'s exact Boolean negation

**Proves:** SEM-2's rule that `notBlank` MUST be `blank`'s literal negation, never a separately
hand-derived condition (SEM-2's Must-Not: "must NOT derive `notBlank` independently from `blank`
... always negate the compiled `blank` condition so the two can never drift apart"). Every artifact
below is rendered as an **explicit negation wrapping `blank`'s own compiled form**, to make that
mechanism visible rather than its algebraically-equivalent expansion:

- **Postgres** (and, apart from the quote character, SQL Server / MySQL / SQLite — but **not**
  Oracle, see SQL-7 below): `NOT (("lastName" IS NULL OR "lastName" = ''))` — the inner parentheses are
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

**Coverage tier:** all seven backends — all five SQL dialects + MongoDB + Search. Promoted from
Postgres-only in #1639 (D2) for the opposite reason to its neighbours: `blank`/`notBlank` is a
*builder-owned composition*, so its SQL arms were expected to differ in nothing but the quote
character. That is still the assertion for four of the five — a dialect that "tidied" the doubled
parentheses, or re-derived `IS NOT NULL AND <> ''` instead of negating `blank`'s own text, fails
three more artifacts than it used to.

## The oracle arm is the exception, and it is why #1642 exists (SQL-7)

**Oracle's arm is the one artifact in this category that is not its Postgres arm with tokens
substituted.** Oracle documents a character value of length zero as NULL, so `"lastName" = ''` is
`"lastName" = NULL` — **UNKNOWN for every row**, not FALSE. The consequence is asymmetric, and it is
the negation this fixture is named for that breaks:

| operator | row where `lastName IS NULL` | row where `lastName = 'Smith'` |
|---|---|---|
| `blank` = `(c IS NULL OR c = '')` | `TRUE OR UNKNOWN` → TRUE → matched ✓ | `FALSE OR UNKNOWN` → UNKNOWN → not matched ✓ |
| `notBlank` = `NOT (…)` | `NOT TRUE` → FALSE → not matched ✓ | `NOT UNKNOWN` → **UNKNOWN → not matched** ✗ |

A `WHERE` clause keeps only rows evaluating to TRUE, so on Oracle the uncollapsed form leaves `blank`
accidentally correct and makes **`notBlank` return the empty result set** — a silently empty grid on
well-formed SQL, invisible to this tier, which compares strings. Its arm therefore reads
`WHERE NOT ("lastName" IS NULL)`: `blank`'s text arm collapses onto the non-text one, and the
`NOT (<blank>)` composition is applied to *that* text, unchanged. **This artifact was derived from
Oracle's documented `''`-is-NULL semantics and three-valued logic, not from running the compiler** —
the same rule every other arm in this suite is held to.

Note what did *not* change: the double parentheses are gone from the oracle arm only because
`blank`'s own form no longer carries a pair, not because anything was tidied. The composition rule
is untouched on all five dialects, which is exactly why SQL-7 puts the divergence behind a single
engine fact (`ISqlDialect.EmptyStringIsNull`) rather than behind a per-dialect `renderBlank`.

**Requirement ids:** SEM-2, SQL-7, SEM-6 (tiebreaker, incidental).
