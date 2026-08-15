# `spec/semantics.md` — Semantics specification (SEM-1..SEM-7)

Normative per **AR-1** ([TRD](../docs/TRD-grid-query-compiler.md) §7): this document, not any single
compiler, is where these seven behaviors are decided. A backend compiler that disagrees with a rule
below has a bug, not an alternative opinion. Every rule here is the target of conformance fixtures
(`spec/fixtures/`, TRD T-1) authored in a later work item (#1630) — this document states the rule
and gives **illustrative** examples; it does not itself constitute the enforceable test suite.

Each section follows the same shape: the rule, why it exists (the naive alternative it rules out —
see TRD §1's problem statement for the catalogue of ways this translation is usually done badly),
one worked example per backend family showing the *same logical outcome* reached through different
backend-native mechanisms, an explicit "must NOT" list, and cross-links to the IR shapes in
[`ir.schema.json`](./ir.schema.json) that the rule constrains.

All SQL/Mongo/OpenSearch examples in the SEM-1..SEM-7 sections below are illustrative snippets to
convey intent, not the literal compiler output for any one dialect. The literal rendering — identifier
quoting, parenthesisation, which operator carries a negation, how a set is parameterized — is pinned
separately in the **"Addendum — canonical SQL rendering"** at the end of this document, which *is*
normative and *is* byte-for-byte what the `spec/fixtures/` Postgres artifacts contain.

---

## SEM-1. Case sensitivity

**Rule.** Every operator in `$defs/binaryOperator` (`eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `contains`,
`notContains`, `startsWith`, `endsWith`) and `$defs/setOperator` (`in`, `notIn`), when applied to a
`stringValue`, is **case-insensitive by default** — including the four ordering comparisons
(`gt`/`gte`/`lt`/`lte`), which fold case in their lexicographic comparison exactly as the
equality/substring operators fold case in theirs: case sensitivity is a per-field property (SC-3),
not a per-operator one, and — the scope of "not a per-operator one" pinned explicitly here — **not
a per-context one either**: the identical mechanism governs an `eq`/`in` comparison and an
`ORDER BY`/`$sort`/`sort` on that field. Per-field, a schema author may opt a field into
case-sensitive comparison instead. Whichever mode is active, the compiler MUST reach it through an
index-friendly, backend-native mechanism — and, critically, **the same class of mechanism (collation
vs. pattern-match) MUST be used consistently across every context on that field**, never one
mechanism for filtering and a different one for sorting.

**The one carve-out, and it is not a per-context exception smuggled back in: grouping (GRP-2).**
This rule governs how a field's *values* are compared. It does **not** govern *bucket identity*, a
question it never asks and which BM-2's protocol answers separately: inside a grouping request the
`GROUP BY` key, the `groupSpec.groupKeys` drill-down match and the `ORDER BY` are all **exact**,
because they have to agree with one another or a child level stops summing to its parent's
aggregate — and because collating a `GROUP BY` key produces a statement the engines reject outright.
That is a per-*statement* rule, not a per-construct one, so it cannot reintroduce the mix this
section forbids; a user's own filter predicate inside that same grouped request is still governed
here, in full. **BM-3's distinct-values query answers to the same carve-out** (GRP-6): its
`SELECT DISTINCT` column and that column's `ORDER BY` are exact, because a distinct set is a set of
buckets and its entries are handed back to the grid to be matched exactly — while the set filter's
own search prefix over that very same column is a filter, and so is still governed here. See the
grouping addendum (GRP-2, GRP-6) for the whole argument, the accepted divergence on engines whose
default collation folds case, and why an earlier draft of this section — which read the drill-down
match as an ordinary comparison — was wrong on this one point.

**Two distinct mechanisms, not one — pick per shape, not per whim.** This document previously left
it ambiguous whether Postgres case-insensitive *equality* (`eq`/`in`/`notIn`, and by extension sort)
should reuse `ILIKE` (the *substring* mechanism, §SEM-7's territory) or use a
collation (the *exact-comparison* mechanism, as SQL Server/MySQL already do below). Reusing `ILIKE`
for exact match turns out to be a trap: `ILIKE`'s pattern-language treats a literal `%`/`_` in the
*value* as a wildcard, so an exact-match operator silently becomes a widened pattern match unless
every value is escaped — and MongoDB's `$eq`/`$in` set-membership forms have no `ESCAPE`-clause
equivalent at all, making that escaping structurally inapplicable to some backends' idiomatic forms.
The rule, pinned: **`eq`/`neq`/`gt`/`gte`/`lt`/`lte`/`in`/`notIn`, plus sorting on
a case-insensitive field, use a case-insensitive COLLATION** (an equality/ordering mechanism, no
pattern language, nothing to escape). **`contains`/`notContains`/`startsWith`/`endsWith` use `ILIKE`
or the backend's substring mechanism** (SEM-7 covers `contains` specifically) — a pattern-match
mechanism is appropriate there because the operator itself is a pattern match; the value's own `%`/
`_` are escaped there because the position is a pattern, not because the CI requirement demands it.

> **This paragraph says *which mechanism* when one is used at all; it does not say one always is.**
> An earlier draft ended both sentences above with "plus sort **and group-key matching**", which
> #1640 made wrong and #1640's own review found still standing here — in the very sentence this
> section labels *the rule, pinned* — after the carve-out had been written into the intro and the
> Must-NOTs. A `groupSpec.groupKeys` drill-down match, a `GROUP BY` key and a **grouped level's**
> `ORDER BY` use **no** case-folding mechanism at all (GRP-2) — as do BM-3's `SELECT DISTINCT` key
> and its `ORDER BY` (GRP-6). A port implementing SEM-1 from the pinned sentence alone would emit the
> collation and fail `group-aggregation/002` (and `016`).

**Rationale.** AG Grid's in-memory (client-side) row model filters text case-insensitively by
default. If the SSRM compiler defaults to case-sensitive comparison instead, the exact same filter
typed into the exact same grid produces different rows depending on which row model happens to be
active — a correctness regression that looks like a backend bug but is really a default mismatch.
The "obvious" fix — wrap the column in `lower()`/`LOWER()` on both sides — reintroduces a different
failure: on most SQL engines a bare function wrapper on the indexed column makes the predicate
non-sargable, silently turning an indexed lookup into a full scan the moment a table grows past a
trivial size. This is exactly the class of "broken null/case semantics" bug the TRD's problem
statement (§1) calls out as endemic to hand-rolled OData/dynamic-LINQ translations. The rule exists
so every dialect gets case-insensitivity *and* keeps its index.

**Worked example, exact-match family** — `eq` on `name`, case-insensitive, matching `"Smith"`:

- **PostgreSQL** — a case-insensitive **collation** on the comparison, never `ILIKE` (see the
  mechanism note above). This project's fixtures and reference docs pin one canonical, explicitly
  host-created ICU collation name for this purpose — `craticula_ci` — rather than each host
  inventing its own name:
  ```sql
  -- One-time host DDL, documented in the per-backend index-guidance page (PERF-4):
  -- CREATE COLLATION craticula_ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
  WHERE name COLLATE craticula_ci = :v
  ```
- **SQL Server / MySQL** — unchanged from before: a case-insensitive collation on the column (or
  applied inline), so the engine's own comparison semantics do the folding and a plain B-tree index
  remains usable:
  ```sql
  -- SQL Server: column declared/queried with a CI collation
  WHERE name COLLATE Latin1_General_CI_AS = @v
  -- MySQL: column already uses a _ci collation (e.g. utf8mb4_0900_ai_ci)
  WHERE name = :v
  ```
- **MongoDB** — a query-level collation with `strength: 2` makes `$eq`/`$in`/`$gt`/`$lt`/`$sort`
  compare case-insensitively (and can still use a supporting index), which is the mechanism this
  document means wherever it says "the collation option for CI" (e.g. TRD §5's Mongo row):
  ```json
  db.items.find(
    { name: "Smith" },
    { collation: { locale: "en", strength: 2 } }
  )
  ```
- **OpenSearch / Elasticsearch** — a term-level query's `case_insensitive` parameter (available on
  `term`, `wildcard`, `prefix`, and `regexp` since 7.10 — **not** `range`; ordering comparisons on
  ES/OpenSearch are covered separately below) against a `keyword` field:
  ```json
  { "term": { "name.keyword": { "value": "Smith", "case_insensitive": true } } }
  ```

**Worked example, substring family** — `contains` on `name`, case-insensitive, matching
`"Bob Smith"` against pattern `"smith"` — this is the pattern-match mechanism, distinct from the
exact-match family above:

- **PostgreSQL** — `ILIKE`, backed by a `pg_trgm` GIN index (PERF-4 index guidance) rather than a
  functional index on `lower(name)`:
  ```sql
  WHERE name ILIKE '%' || :pattern || '%' ESCAPE '\'
  ```
- **MongoDB** — no collation-compatible substring operator exists; `contains` necessarily compiles
  to `$regex` with the `"i"` option, which is *not* index-friendly the way the collation/`pg_trgm`
  forms above are — SEM-7 covers this mechanism (and its escaping requirements) in full.
- **OpenSearch / Elasticsearch** — `wildcard`/`case_insensitive` against the `keyword` sub-field, no
  separate analyzed sub-field or runtime lowercasing required (SEM-7 governs the substring mechanics
  themselves):
  ```json
  { "wildcard": { "name.keyword": { "value": "*smith*", "case_insensitive": true } } }
  ```

**Ordering comparisons (`gt`/`gte`/`lt`/`lte`) on OpenSearch/Elasticsearch — the one real gap.**
Unlike `term`/`wildcard`, a `range` query has no `case_insensitive` parameter in the pinned 7.10-era
subset, and there is no query-time equivalent. The compiler targets a schema-declared **normalized
sub-field** (`SC-3`'s `search.normalizedSubField` — a `keyword` sub-field maintained via an
ICU-folding normalizer *at index time*, distinct from the plain `.keyword` exact-match sub-field
`search.keywordSubField` used by `term`/`in`/`notIn` above) instead of the raw field:
```json
{ "range": { "name.normalized": { "gt": "smith" } } }
```
A field that needs case-insensitive ordering on ES/OpenSearch MUST declare
`search.normalizedSubField`; a field that only needs case-insensitive equality/substring ops does
not need one. This is a genuine index-time cost (SC-3, PERF-4), not a query-time toggle — document it
per field, never assume it exists.

**Must NOT.**
- Must NOT wrap the column/field in a bare `lower()`/`LOWER()`/`toLowerCase` when the dialect offers
  an index-friendly alternative (`ILIKE`, a CI collation, `case_insensitive` query option) — reserve
  the bare wrapper for the rare dialect that truly has nothing better, and document that exception
  per dialect (PERF-4).
- Must NOT default to case-sensitive comparison "because SQL's default collation is case-sensitive
  on this engine" — the IR-level default is case-insensitive regardless of any one engine's own
  default; per-field case sensitivity is an explicit schema opt-in (SC-3), never an accidental
  inheritance from the underlying engine.
- Must NOT apply case-folding inconsistently between the comparison and any related `ORDER BY`/sort
  on the same field — case sensitivity is a per-field property, not a per-operator *or per-context*
  one. (A `groupSpec.groupKeys` drill-down match is the documented carve-out: it is exact, together
  with the `GROUP BY` and `ORDER BY` of the same statement — GRP-2, not this rule. So is BM-3's
  `SELECT DISTINCT` key with its own `ORDER BY` — GRP-6, and note that a set filter's search prefix
  over that same column IS governed here, which is why one such statement legitimately spells two
  mechanisms.)
- Must NOT use `ILIKE`/pattern-match as the mechanism for `eq`/`neq`/`in`/`notIn`/ordering/sort —
  that mechanism is a collation. Reserve `ILIKE`/pattern-match for
  `contains`/`notContains`/`startsWith`/`endsWith`, where the operator itself is a pattern match.
- Must NOT assume an ES/OpenSearch `range` query can be made case-insensitive at query time — it
  cannot in the pinned 7.10-era subset; case-insensitive ordering requires a schema-declared
  `search.normalizedSubField`, index-time cost included.

**Cross-links.** `$defs/binaryOperator`, `$defs/setOperator`, `$defs/stringValue`.

---

## SEM-2. Null handling

**Rule.** The unary operators `blank`/`notBlank` (`$defs/unaryOperator`) resolve as: `blank` ⇔
`IS NULL OR = ''` for text fields, `IS NULL` for every other type; `notBlank` is `blank`'s exact
Boolean negation (never a separately-derived condition that could disagree with it at the edges).
*(The text arm is per-engine as well as per-type: on a backend whose empty string **is** NULL —
Oracle among the SQL dialects — it collapses to `IS NULL`, and emitting the `= ''` disjunct there
makes `notBlank` match nothing at all. The negation rule above is unaffected. See **SQL-7** in the
canonical-SQL-rendering addendum for the truth table and the reason it is a correctness rule.)*
Negative binary/set operators — `neq`, `notContains`, `notIn` — MUST include NULL-valued rows in
their match set. A negative comparison compiles as, e.g., `col <> :v OR col IS NULL`, never a bare
`col <> :v`. **The same null-inclusive rule applies to a tree-level `$defs/notNode` wrapping any
child** (`Not(Comparison(...))`, `Not(And(...))`, `Not(Or(...))`, arbitrarily nested) — not just the
negative *operators* above. AG Grid's client-side row model has no tree-level NOT construct to take
as a reference point, but its own rationale extends cleanly: a NOT that excludes a NULL/missing row
just because SQL's three-valued logic happens to do so is the identical bug this rule exists to
prevent, one syntax level up. Concretely, `Not(Comparison(field, contains, X))` on SQL compiles as
`(NOT (col ILIKE '%'||:v||'%') OR col IS NULL)` — the `OR col IS NULL` is mandatory on the *outside*
of the `NOT`, exactly mirroring `neq`'s own compiled shape — while Mongo's `$nor`/ES's `must_not`
already include null/missing documents natively for most query types (verify per query type per the
third "must NOT" below, the same caveat as `neq`'s own worked example).

**Rationale.** AG Grid's in-memory row model filters with plain JavaScript equality, where a missing
or `null` cell value is simply "not equal" to any concrete filter value — so `neq` in the client-side
row model always includes rows with a null/missing value for that column. SQL's three-valued logic
does the opposite by default: `NULL <> 'Alice'` evaluates to `NULL` (neither true nor false), so a
bare `WHERE name <> 'Alice'` **silently excludes** every NULL row from the result — the naive,
"obviously correct" translation is actually the wrong answer, and it disagrees with what the same
filter would have shown against the same dataset in the client-side row model. This divergence is
exactly the "broken null semantics" the TRD's problem statement (§1) flags as one of the two most
common ways this translation goes wrong (case sensitivity being the other, SEM-1). Pinning the answer
— NULLs match negative operators — makes every backend agree with each other and with AG Grid's own
non-SSRM behavior.

**Worked example** — `neq: "Alice"` against a `name` field where some rows have `name IS NULL`:

- **SQL** — the explicit `OR IS NULL` is mandatory precisely because bare `<>` drops NULLs (the
  enclosing parentheses are not optional either — see SQL-2 in the canonical-rendering addendum):
  ```sql
  WHERE (name <> :v OR name IS NULL)
  ```
- **MongoDB** — `{ field: { $ne: value } }` already matches documents where the field is null or
  entirely missing (MongoDB's `$ne` treats a missing field as not-equal), so the naive single-clause
  form happens to be correct in the plain query language. The compiler nonetheless emits the
  explicit composed form below, for two reasons: it keeps the compiled shape parallel to the SQL and
  ES forms (useful for the fixture suite comparing structure across backends), and it stays correct
  inside an `$expr`/aggregation-expression context (e.g. when combined with a `$strcasecmp` for
  case-insensitive comparison, SEM-1), where relying on the query-language `$ne`'s implicit
  missing-field handling is not an option because the predicate is no longer a plain query-language
  clause:
  ```json
  { "$or": [ { "name": { "$ne": "Alice" } }, { "name": null } ] }
  ```
- **OpenSearch / Elasticsearch** — a `bool` query composing a negated `term` with an explicit
  "field does not exist" clause via `should`/`must_not`:
  ```json
  {
    "bool": {
      "should": [
        { "bool": { "must_not": { "term": { "name.keyword": "Alice" } } } },
        { "bool": { "must_not": { "exists": { "field": "name" } } } }
      ],
      "minimum_should_match": 1
    }
  }
  ```
  Note that the first `should` clause alone already covers the missing-field case for a `term` query
  (a `must_not` around a query that can't match a missing field is itself true for that document),
  so the second clause is belt-and-suspenders here — kept explicit because that implicit behavior is
  a property of the `term` query type specifically and is not guaranteed to hold for every query type
  a future operator might compile to (e.g. `range`), so the compiler does not lean on it silently.

**Must NOT.**
- SQL: must NOT emit a bare `<>`/`NOT IN (...)` for a negative operator, **or a bare `NOT (...)` for
  a tree-level `notNode`**, without the accompanying `OR col IS NULL` on the outside — three-valued
  logic drops NULLs from a bare comparison or a bare `NOT`, which is the exact bug this rule exists
  to prevent. `NOT (col ILIKE ...)` and `col <> ...` fail identically and need the identical fix.
- MongoDB: must NOT add a defensive `{ field: { $ne: null } }` guard "to be safe" or "for parity with
  SQL" — that guard would *exclude* null-valued documents from a negative comparison, which is the
  opposite of this rule and a Mongo-specific regression introduced by SQL-shaped intuition.
- Must NOT derive `notBlank` independently from `blank` (e.g. hand-writing `IS NOT NULL AND <> ''`
  separately from `blank`'s own `IS NULL OR = ''`) — always negate the compiled `blank` condition so
  the two can never drift apart. This holds unchanged under SQL-7: an engine that collapses the text
  arm negates *its own* collapsed text, so the two operators stay in step there too.
- Must NOT apply the text-specific `= ''` clause of `blank`/`notBlank` to non-text field types — nor
  to a text field on an engine where `''` is NULL (**SQL-7**), where it is not merely redundant but
  turns `notBlank` into a predicate no row satisfies.

**Cross-links.** `$defs/unaryOperator` (`blank`, `notBlank`), `$defs/binaryOperator` (`neq`),
`$defs/setOperator` (`notIn`), `$defs/nullValue`, SQL-2 and **SQL-7** (the compiled shapes of
`blank`/`notBlank`, per construct and per engine).

---

## SEM-3. Null ordering

**Rule.** Every `sortItem` in the IR carries an explicit `nullsPosition` (`first` or `last`),
independent of `sortDirection` — the IR never leaves null placement to be inferred from direction,
and the compiler MUST honor `nullsPosition` literally rather than deriving it itself. Compilers emit
native `NULLS LAST`/`NULLS FIRST` syntax where the dialect supports it, or an equivalent emulation
where it doesn't.

**The adapter's default (pinned).** AG Grid's `sortModel` carries no null-position signal at all —
it is purely `{ colId, sort }`. Since `$defs/sortItem` requires `nullsPosition` on every sort key,
the adapter must produce one on every sort key the grid sends, and it does so with a single, fixed
rule: **`nullsPosition: "last"` on `asc`, `nullsPosition: "first"` on `desc`.** This is the one
direction→position mapping this document pins — not because compilers are allowed to derive it
themselves (they are not; see "must NOT" below), but because the *adapter* has to choose something
deterministic when the wire protocol gives it nothing, and an arbitrary-but-consistent choice here is
what makes G3/G4 achievable: every port's adapter applies the identical mapping, so every port's
compiler receives the identical `nullsPosition` for the identical `sortModel`, and none of them ever
compute it themselves.

**Rationale.** Database engines disagree about where NULLs sort by default: PostgreSQL and Oracle
treat NULL as the "largest" value (NULLS LAST on ASC, NULLS FIRST on DESC, by default), while MySQL
and SQLite treat NULL as the "smallest" value (NULLS FIRST on ASC by default) — and some engines'
defaults are additionally subject to session/locale settings. If a compiler just emits `ORDER BY col
ASC` and trusts the engine's default, the exact same IR produces a different row order depending on
which SQL dialect executes it — a direct violation of G3/G4 (identical semantics across backends and
language ports). Worse, it means results are technically legal-but-unpredictable per the SQL standard
even on a single engine whose defaults change across a major version. Pinning `nullsPosition`
explicitly in the IR, and pinning *what value the adapter writes there* in this document rather than
leaving four ports to invent their own default — removes the ambiguity at the one place (the adapter,
resolving AG Grid's request into IR) that should own the decision, and happens to match the
PostgreSQL/Oracle default exactly (a coincidence of this particular choice, not a requirement that
compilers rely on it — see "must NOT" below).

**Worked example** — sort `amount ASC`, `nullsPosition: "last"`:

- **PostgreSQL / Oracle / SQL:2003-compliant dialects** — native syntax:
  ```sql
  ORDER BY amount ASC NULLS LAST
  ```
- **MySQL** — no native `NULLS LAST` syntax; emulated with an `ISNULL(col)` prefix key (returns `1`
  for NULL, `0` otherwise), so ordering ascending by that key pushes NULLs after every non-null row,
  with the real column kept as the secondary key so non-null rows still sort correctly among
  themselves:
  ```sql
  ORDER BY ISNULL(amount) ASC, amount ASC
  ```
- **MongoDB** — no native "nulls last" `$sort` behavior (BSON has its own type-ordering rules where
  null sorts before numbers); emulated with a synthetic null-bucket field added upstream in the
  pipeline, then sorted on the bucket first — and **removed afterward**, since the bucket is a
  compiler-internal emulation detail, not part of the row shape the grid should ever see:
  ```json
  [
    { "$addFields": { "__amountNullBucket": { "$cond": [{ "$eq": ["$amount", null] }, 1, 0] } } },
    { "$sort": { "__amountNullBucket": 1, "amount": 1 } },
    { "$unset": "__amountNullBucket" }
  ]
  ```
  The `$unset` MUST appear after every `$sort` stage that used a bucket, before the pipeline's final
  `$skip`/`$limit`/`$group` — a result row carrying `__amountNullBucket` alongside the real fields is
  a shape divergence from the Postgres/Search results for the identical IR, on top of leaking an
  implementation detail. If multiple sort keys each need a bucket, unset all of them in one stage
  (`{ "$unset": ["__amountNullBucket", "__lastNameNullBucket"] }`).
- **OpenSearch / Elasticsearch** — the sort clause's native `missing` parameter:
  ```json
  { "sort": [ { "amount": { "order": "asc", "missing": "_last" } } ] }
  ```

**Must NOT.**
- Must NOT rely on a dialect's implicit/default null ordering, even when it happens to match the
  desired output today — defaults vary across engines, across engine versions, and (for some
  engines) across session/locale configuration.
- Must NOT derive `nullsPosition` from `sortDirection` inside the **compiler** (e.g. hardcoding "ASC
  always means nulls last") — even though that mapping happens to be this document's own pinned
  adapter default (above), the compiler must still read the IR's `nullsPosition` literally rather
  than re-deriving it; the pinned mapping is the *adapter's* rule for producing the value, not the
  compiler's license to skip reading it. The IR already carries `nullsPosition` explicitly and
  independently for exactly this reason; a compiler that re-derives it is reintroducing the ambiguity
  the schema
  was designed to remove.
- When emulating with a prefix-key trick (MySQL `ISNULL`, the Mongo synthetic bucket), must NOT drop
  the original field as the secondary sort key — the emulation key only resolves null-vs-non-null
  placement, not the relative order of non-null values.
- Mongo: must NOT leave a synthetic `__<field>NullBucket` key in the pipeline's output — `$unset` it
  once it has served its `$sort` purpose (see the worked example above).
- Must NOT leave string-field collation implicit when sorting a case-insensitive field (SEM-1) — the
  same collation mechanism that governs `eq`/`in` on that field governs its `ORDER BY`/`$sort`/`sort`
  too, on every backend, or row order (not just null placement) diverges across backends for
  identical IR.

**Cross-links.** `$defs/sortItem`, `$defs/nullsPosition`, `$defs/sortDirection`.

---

## SEM-4. Dates & zones

**Rule.** The IR distinguishes a **local date** (`$defs/dateValue` — a calendar date, no
time-of-day, no zone) from an **instant** (`$defs/datetimeValue` — a point in time plus the IANA
zone used to resolve it). AG Grid emits date/datetime filter values as zone-less
`"YYYY-MM-DD HH:mm:ss"` (or `"YYYY-MM-DD"`) strings; resolving one of those strings into a
`dateValue` or `datetimeValue` is exclusively the frontend adapter's job, using **both** the field's
schema-declared type and the request context's zone — never the server process's local timezone,
and never a zone guessed from the string itself (it doesn't carry one).

**Compilers never emit a zone clause for a `datetimeValue` (pinned).** By the time a `datetimeValue`
reaches a compiler, `value` is already an absolute instant (an ISO-8601 timestamp with an explicit
offset/`Z`) — a fixed point on the universal timeline that means the same thing regardless of any
zone. The compiler compares/stores it as that absolute instant and nothing else: no `AT TIME ZONE`,
no `$dateTrunc`-with-timezone, no ES `time_zone` parameter, ever, for a plain comparison against a
`datetimeValue`. `datetimeValue.zone` is **provenance metadata** — it records which zone the adapter
used to resolve the original wire string into that absolute instant (needed for audit/debugging and
for the adapter's own resolution step) — it is not an instruction for the compiler to act on.
(Zone-aware date-math — grouping rows by "day in the user's zone", for example — is a
*post-v1 aggregation feature*, not part of comparing a `datetimeValue` in a filter; if and when it
ships, it will need its own IR shape carrying the target zone explicitly on the aggregation, not a
compiler quietly reaching into a comparison node's provenance field.) For `inRange`, per-boundary
inclusivity (`fromInclusive`/`toInclusive`) is carried explicitly on the IR node and the compiler
MUST read it from there — never assume a fixed default.

**MongoDB representation of `dateValue` (pinned): a plain ISO date string, never a BSON `Date`.**
MongoDB's `Date` type has no "calendar date, no time-of-day" concept — every BSON `Date` is an
instant. Storing/comparing a `dateValue` as a BSON `Date` therefore requires silently promoting it to
*some* midnight instant, and "midnight in which zone?" has no answer for a value that was never an
instant in the first place — exactly the conflation this rule's own second "must NOT" (below)
forbids "without that promotion being an explicit, documented adapter decision." Rather than making
that decision implicitly inside every Mongo compiler, the pinned mechanism sidesteps it: `dateValue`
compiles to the ISO calendar-date string itself (`"1990-05-12"`), compared as a plain BSON string.
Lexicographic string comparison on `YYYY-MM-DD` already matches chronological order, so `$gte`/`$lt`
work correctly with no `Date` promotion, no zone, and no ambiguity. `datetimeValue` (an instant)
still compiles to a BSON `Date` — extended-JSON's `$date` field requires a full instant string with
an explicit offset/`Z` (`"2026-01-01T00:00:00Z"`), never a bare calendar date.

> **Not yet pinned — flagged in the TRD, stated here rather than guessed at.** The TRD (§7.4)
> explicitly marks the *exact* default boundary inclusivity that AG Grid's own simple date filter
> uses for `inRange` (its stated default is exclusive of both bounds) as "verify at implementation
> time and freeze" — i.e., a value to be captured from a live grid and locked into fixtures by
> #1630, not a certainty this document should assert today. What *is* pinned by this rule is the
> mechanism: inclusivity flags are always explicit on the IR node and always read literally by the
> compiler. What is **not yet pinned** is the specific default value the adapter should choose when
> AG Grid's own request doesn't make it explicit. Treat any inclusive/exclusive example below as
> illustrative of the mechanism, not as the frozen default.

**Rationale.** Three naive failure modes, all seen in hand-rolled translations: (1) treating every
date as a UTC instant regardless of its declared type — a local-date field compared this way shifts
by a day for any user not in UTC, the classic "date is off by one near midnight" bug; (2) parsing
AG Grid's zone-less datetime string using the server process's own local timezone, which makes the
exact same IR return different rows depending on the deployment environment's `TZ` setting — a
compiler that isn't even deterministic on its own inputs; (3) hardcoding `inRange` boundaries as
always-inclusive (SQL `BETWEEN`'s familiar default) regardless of what the IR's explicit flags say,
so a row sitting exactly on a boundary appears in the grid's client-side row model and silently
disappears once the same filter is compiled for SSRM. All three are exactly the kind of "usually
wrong" translation TRD §7's opening line calls out — dates and zones are where naive compilers most
often diverge from what the grid itself shows the user.

**Worked example** — `inRange` on `createdAt` (an instant field), from 2026-01-01T00:00:00Z
(inclusive) to 2026-02-01T00:00:00Z (exclusive), as resolved by the adapter for illustration:

- **SQL** — a local-date field needs no zone conversion at all (it never had one); a true instant
  field is compared against already-UTC-resolved boundary parameters. PostgreSQL's `timestamptz`
  stores/compares in UTC internally regardless of session timezone, so no `AT TIME ZONE` clause is
  needed once the adapter has resolved the instant; a dialect without a true timezone-aware
  timestamp type (SQL Server's `datetime2`) needs the equivalent conversion applied in its own idiom
  before comparison:
  ```sql
  WHERE created_at >= :from AND created_at < :to
  ```
- **MongoDB** — an instant field (`datetimeValue`) compares against BSON `Date` values built from
  the full instant string (see the pinned representation above); a local-date field (`dateValue`)
  compares against the plain ISO date string instead, never a `Date`:
  ```json
  // instant field:
  { "createdAt": { "$gte": { "$date": "2026-01-01T00:00:00Z" }, "$lt": { "$date": "2026-02-01T00:00:00Z" } } }
  // local-date field:
  { "createdOn": { "$gte": "2026-01-01", "$lt": "2026-02-01" } }
  ```
- **OpenSearch / Elasticsearch** — a `range` query choosing `gte`/`gt` and `lt`/`lte` per the
  inclusivity flags, with an explicit `format` (pinning the accepted string shape) — **no**
  `time_zone` parameter, because both boundaries are already-resolved absolute instants (with an
  explicit `Z`/offset) and `time_zone` only affects *unqualified* date-math strings, which never
  occur here:
  ```json
  {
    "range": {
      "createdAt": {
        "gte": "2026-01-01T00:00:00Z",
        "lt": "2026-02-01T00:00:00Z",
        "format": "strict_date_optional_time"
      }
    }
  }
  ```

**Must NOT.**
- Must NOT assume a fixed server or database default timezone anywhere in the compiler; zone
  resolution happens once, in the adapter, using the request context's zone.
- Must NOT emit any dialect zone-conversion clause (`AT TIME ZONE`, `$dateTrunc`'s `timezone` option,
  ES `time_zone`) when compiling a comparison against a `datetimeValue` — the value is already an
  absolute instant; a zone clause at that point does not "correctly interpret" it, it *changes* what
  the comparison means, silently making the compiler's output depend on `datetimeValue.zone` (a
  provenance field) in a way no other backend's compiler does.
- Must NOT conflate `dateValue` and `datetimeValue` at the compiler level — a local date must never
  be silently promoted to a midnight-UTC instant and then compared against instant semantics without
  that promotion being an explicit, documented adapter decision.
- Must NOT hardcode `inRange` as inclusive-inclusive (or any other fixed combination) — the flags are
  already explicit on `rangeComparisonNode`; the compiler reads them, it never assumes them.
- Must NOT present the exact AG Grid boundary-inclusivity default as settled fact in fixtures or
  documentation before it has been verified against a live grid, per the TRD's own "verify and
  freeze" flag on this rule.
- Must NOT represent a `dateValue` as a MongoDB `Date` — see the pinned representation above; use
  the plain ISO date string.

**Fixture format note.** A `datetimeValue` fixture's expected artifacts are only reproducible if the
adapter's resolved value AND the request-context zone it used are both visible in the fixture inputs
— otherwise a port author must guess the zone the original fixture author assumed. `agGridRequest.json`
fixtures needing this carry an optional `requestContext: { "zone": "<IANA id>" }` sibling field
alongside the AG Grid wire shape (the same "documented extension beyond AG Grid's own wire format"
pattern as `customFilters`, OQ-11) — see `spec/fixtures/README.md`.

`requestContext` carries a second, related field for the same reproducibility reason: `now` (an
ISO-8601 instant), required by any fixture whose expected artifacts depend on what "now" was — i.e.
every AG-2 date-preset fixture, since `today`/`last7Days`/... resolve relative to the current
instant. The two fields differ in status, and the difference is normative: **`zone` is read off the
wire, `now` is not.** An implementation resolves presets against its own injected clock and MUST NOT
honor a client-supplied `now` — a request that could redefine what "last 7 days" means would let any
caller move the window at will. `now` exists so a conformance harness can pin its clock and replay
the fixture deterministically; it is a harness instruction, never a wire input. See
`spec/fixtures/date-presets/README.md`.

**Cross-links.** `$defs/dateValue`, `$defs/datetimeValue`, `$defs/rangeComparisonNode`
(`fromInclusive`, `toInclusive`), `$defs/typedValue`.

---

## SEM-5. Type coercion

**Rule.** Coercing a grid's stringly-typed wire value into a typed IR value (e.g. AG Grid's number
filter sending `"30"`, coerced to an `int64Value`) happens **exactly once**, in the frontend adapter,
at parse time, using the field's schema-declared type (SC-2) as the coercion target. No backend
compiler ever performs coercion or re-interprets a `typedValue` — by the time a compiler sees a
value, its type is already fixed and the compiler trusts it completely. If the adapter cannot coerce
a wire value to the field's declared type, that is a request error in Strict mode (SC-1's default) —
never a value that is passed through untyped or best-effort.

**Rationale.** The naive alternative pushes the stringly-typed value all the way down to the
database layer and lets the target engine's own implicit-coercion rules decide what `"30"` means
against an integer column. That makes behavior backend-dependent by construction — one engine
coerces, another errors, a third compares as a string and never matches an integer column at all —
which directly violates G3/G4 (identical semantics across backends and language ports). Worse is the
failure mode the TRD's problem statement (§1) attributes by name to string-DSL intermediaries like
OData/dynamic-LINQ adapters: an unparseable filter value is sometimes treated as "no constraint at
all," silently degrading a supposedly-narrowing filter into a full-table match — the opposite of what
the user asked for, and dangerous precisely because it fails silently rather than loudly.

**Worked example** — coercing a number filter's wire value against a field declared `int64`:

- **AG Grid adapter (the one and only coercion site)** — wire value
  `{"filterType":"number","type":"equals","filter":"30"}` against a field configured `type: "int64"`
  in the `GridSchema` is parsed once, here, into:
  ```json
  { "type": "int64", "value": 30 }
  ```
  Every downstream compiler (SQL/Mongo/ES) binds `30` as a native integer parameter/value; none of
  them contain coercion logic of any kind — they operate on the already-typed `int64Value` node.
- **Strict-mode rejection** — the same field, wire value `"abc"` (not parseable as an integer): the
  adapter's coercion fails before any IR node is produced, and Strict mode (SC-1's default) surfaces
  a request error (a validation failure an execution adapter typically maps to an HTTP 400-class
  response) rather than emitting a fallback value. Lenient mode (SC-1's non-default alternative)
  would need an explicit, documented fallback (e.g., dropping just that condition) — but that is a
  deliberate per-host opt-out, not this rule's canonical path.
- Boolean and date/datetime coercion (`"true"`/`"false"` → `booleanValue`; AG Grid's
  `"YYYY-MM-DD HH:mm:ss"` string → `dateValue`/`datetimeValue` per SEM-4) follow the identical
  one-layer rule — only the target type differs, not where or how coercion happens.

**Must NOT.**
- Compilers must NOT attempt to coerce a `typedValue`'s declared type against a schema's physical
  column/field type — that check is the adapter/validator's job, never the compiler's.
- Must NOT silently substitute an uncoercible value with a sentinel that matches everything (e.g.
  turning a bad numeric string into a SQL `1=1`) or with one that matches nothing, without surfacing
  a request error — both hide the failure instead of reporting it.
- Must NOT rely on the target engine's own implicit cast rules (e.g. a database comparing a string
  literal against an integer column via its native cast rules) as the coercion mechanism — that
  reintroduces exactly the backend-dependent behavior this rule exists to eliminate.

**Cross-links.** `$defs/typedValue` and its member shapes — `$defs/int64Value`,
`$defs/decimalValue`, `$defs/doubleValue`, `$defs/booleanValue`, `$defs/stringValue`,
`$defs/dateValue`, `$defs/datetimeValue` — the closed, tagged union that makes "compilers never
guess" structurally true: a compiler cannot receive an ambiguous scalar in the first place.

---

## SEM-6. Deterministic paging

**Rule.** Schema config declares a required, unique `stableKey` per grid. If the IR's `sort` array
does not already end in that key, the compiler appends it as an implicit trailing tiebreaker —
after every sort key the user actually chose, never ahead of them — before compiling the final
ordering clause. The appended tiebreaker still needs a `nullsPosition` (SEM-3 applies to it exactly
as to any other sort key): since a `stableKey` is required to be unique but not required to be
`NOT NULL`, the compiler cannot skip specifying one. Use the same adapter default SEM-3 pins
(`last` on ascending, which the tiebreaker always is, since it exists purely to break ties and has
no direction the user chose) — i.e. the appended key is always `{ field: stableKey, direction: "asc",
nullsPosition: "last" }`.

> **Superseded in part by GRP-4 (#1640) for a grouped level, and by GRP-6 (#1641) for BM-3's
> distinct-values query.** This rule says the appended key is *always* the schema's `stableKey`, and
> its first Must-NOT below forbids treating anything else as unique. On a **grouped level** that is
> now wrong: the `stableKey` is not in the grouped
> projection at all, so ordering by it is invalid SQL rather than merely undesirable, and the
> **group column** plays its role instead — appended last, ascending, nulls last, and not appended
> twice when the sort already ends in it, all of which are this rule's own mechanics unchanged. The
> Must-NOT's *intent* is untouched and still binds: the substitute is not a key that "merely looks
> unique", it is unique across the level's rows **by construction**, since a `GROUP BY` emits one row
> per distinct value of it.
>
> A **BM-3 distinct-values query appends nothing at all** (GRP-6(e)) — and it is the exclusion this
> enumeration is most likely to be read past, because it carries no `groupSpec` and so answers to
> "not grouped" on the only test a compiler has in front of it. It is nonetheless not a row query:
> the `stableKey` is absent from its one-column projection exactly as it is from a grouped one, so
> appending it is the same invalid SQL, and its single `ORDER BY` key — the distinct column — is
> already unique across the result rows by this blockquote's own by-construction argument, leaving no
> tie for a tiebreaker to break. A port that appends here emits
> `ORDER BY "region" ASC NULLS LAST, "id" ASC NULLS LAST LIMIT $1` and fails
> `group-aggregation/016`-`019` on all seven arms.
>
> Everything else — an ordinary row query, and a fully-consumed drill-down
> **leaf**, which projects rows — keeps `stableKey` exactly as written here. Recorded rather than
> deleted, since the affected artifacts (`group-aggregation/001`-`004`, `007`-`015`, `020`, `021` on
> every arm) were authored against the original.

> **Superseded in part by #1640's review, for the appended tiebreaker's case sensitivity.** The
> appended key resolves **exact** — no case-folding mechanism is spelled on it — whatever SEM-1 says
> about the `stableKey`'s own field, and whether or not the query is grouped. Everything else about
> the tiebreaker is unchanged: still last, still ascending, still nulls-last, still not appended when
> the sort already ends in it.
>
> This is not a stylistic exemption, it is *this rule's own guarantee*. A `stableKey` is declared
> unique, and it is unique under the collation its **storage** uses; a compiler that attaches a
> different one is no longer comparing the text a user asked about, it is re-deciding row identity.
> The fixture suite's `craticula_ci` is declared `deterministic = false`, and under a
> non-deterministic collation `'aB3'` and `'Ab3'` compare **equal** — so a collated tiebreaker leaves
> exactly the ties it exists to break, the same statement may order those rows differently at
> `OFFSET 0` and at `OFFSET 100`, and a row is returned twice or dropped from both pages. That is the
> Rationale immediately below, and the third Must-NOT further down, reintroduced through this rule's
> own mechanism. Spelling nothing sorts the key under its storage collation, i.e. under the one its
> declared uniqueness is stated in. It is GRP-2's argument extended by one step: a key that
> establishes **identity** is not governed by how a field's *values* compare.
>
> The scope is the **appended** key only. A user's own sort key keeps SEM-1 in full, including on the
> stable key's own column when the request sorts by it explicitly (nothing was appended then) — so one
> statement legitimately reads
> `ORDER BY "last_name" COLLATE craticula_ci ASC NULLS LAST, "id" ASC NULLS LAST`, a collated user key
> beside a bare tiebreaker, and `spec/fixtures/sort-paging/001` pins it. Recorded rather than folded
> into the rule because `group-aggregation/005`/`006` are the only artifacts in the whole suite that
> ever pinned the collated form — measured across all seven backends — and both were authored, and
> re-authored, inside #1640.

**Rationale.** Paging over a sort key that isn't unique is non-deterministic at the database level:
nothing in the SQL standard (or in Mongo's or ES's own documentation) guarantees that rows tied on
the chosen sort key come back in the same relative order across two separate executions of the
identical query — including two consecutive page reads of the same filter+sort. A user sorting a
grid by `lastName` alone, with duplicate last names in the dataset, can see a row duplicated across
two pages or silently missing from both, depending on how the engine happened to break the tie that
particular time. This is a uniquely nasty bug class: the query "looks" correct, it reproduces
intermittently, and the AG Grid UI itself gives the end user no way to add a tiebreaker (a user can
only sort by the columns visible in the grid). Requiring the schema to declare a `stableKey` once,
and having every compiler append it automatically, removes the ambiguity without depending on every
sort model the UI might ever construct happening to already be unique.

**Worked example** — user sorts by `lastName ASC` only; schema declares `stableKey: "id"`:

- **SQL** — `ORDER BY` gains the tiebreaker as its final key, with its own explicit null placement
  per SEM-3 (`NULLS LAST` here, since the tiebreaker's direction is always `asc`):
  ```sql
  ORDER BY last_name ASC NULLS LAST, id ASC NULLS LAST
  ```
- **MongoDB** — the `$sort` document gains the same trailing key (object key order is significant
  and preserved for `$sort`); if `id` can itself be null/missing, it goes through the same
  null-bucket emulation SEM-3 shows for `lastName` — **bucket key before the real field**, matching
  SEM-3's own canonical order exactly (not the reverse):
  ```json
  { "$sort": { "__lastNameNullBucket": 1, "lastName": 1, "__idNullBucket": 1, "id": 1 } }
  ```
  The `__idNullBucket` stage is emitted **unconditionally, with no exception** — `stableKey` is
  usually a non-nullable primary/surrogate key in practice, making the bucket a runtime no-op, but
  `grid-schema.schema.json` has no way to *declare* NOT NULL, so there is no schema state a compiler
  could check to justify skipping it. The same unconditional rule applies everywhere else this
  document's examples show the marker (SEM-3's own worked example, and every other tiebreaker
  example in this document) — "the field is probably never null in practice" is never a license to
  omit the mechanism; the mechanism is what makes it not matter if it turns out to be null.
- **OpenSearch / Elasticsearch** — the `sort` array, whose element order *is* the tiebreak order,
  gains the mapped `stableKey` field (or its `.keyword` sub-field) as its last element, with its own
  `missing` parameter:
  ```json
  { "sort": [ { "lastName": { "order": "asc", "missing": "_last" } }, { "id": { "order": "asc", "missing": "_last" } } ] }
  ```

This is v1's *only* paging mechanism (`$defs/rangePage`'s `startRow`/`endRow`, AG-3) — the
tiebreaker's job is to make that offset-based paging deterministic, not to enable an alternative
keyset-paging mode; see the "must NOT" below.

**Must NOT.**
- Must NOT accept a client-supplied sort model that merely *looks* unique (a field that happens to
  have distinct values in a sample dataset) as sufficient without it being the schema's declared
  `stableKey` — apparent uniqueness in a sample is not a guarantee.
- Must NOT insert the tiebreaker ahead of, or interleaved with, the user's chosen sort keys — it is
  always the final key, so every key the user actually asked for keeps full precedence.
- Must NOT treat this rule as license to switch v1 grid paging from offset-based (`$defs/rangePage`)
  to keyset-based paging — SQL keyset pagination is an explicit v2 opt-in (PERF-2), not a v1
  requirement or even a v1 option. The `stableKey` tiebreaker's job in v1 is narrower than "enabling
  keyset paging": it makes the existing `startRow`/`endRow` offset paging deterministic across page
  reads, by guaranteeing the *sort itself* has one and only one possible row ordering. EXP-3's ban
  on `OFFSET`-walking is a separate, narrower rule scoped to the export-streaming fallback path
  (chunking an unpaginated export when a driver can't stream) — it does not apply to, and must not be
  read as banning, ordinary v1 grid paging.
- Must NOT omit the tiebreaker's null-ordering marker (SQL `NULLS`, Mongo bucket, ES `missing`) on
  the theory that `stableKey` is "probably never null" — there is no schema state that licenses this
  omission (see the worked example above); every tiebreaker in every artifact carries the marker,
  unconditionally, including under `page: none` (EXP-2) and export scopes (EXP-1), which use the
  identical sort-compilation path as ordinary paged requests. (This bullet is about the *marker* on a
  tiebreaker that exists. It is not an enumeration of the shapes that get one, and in particular
  "unpaginated" does not imply "still appends": a BM-3 distinct-values query is also unpaginated and
  appends no tiebreaker at all — GRP-6(e), and the first blockquote above.)

**Cross-links.** `$defs/sortItem` (the array the compiler appends to), `$defs/fieldName`
(`stableKey` is resolved through the schema the same way any other IR field reference is),
`$defs/pageSpec` / `$defs/rangePage` (the paging context this rule protects the determinism of).

---

## SEM-7. `contains` on ES/OpenSearch

**Rule.** On OpenSearch/Elasticsearch, `contains`/`notContains` (`$defs/binaryOperator`) compile to
**substring-matching** query types — `wildcard` against a `keyword` field, or a per-field ngram-
analyzed sub-field strategy (schema-configured) — never to a tokenized `match` query. This pins
`contains`'s behavior to exactly what SQL's `LIKE '%pattern%'` does: substring presence, no
tokenization, no stemming, no stopword removal, no relevance scoring. A per-field, explicit opt-in
`searchMode: analyzed` is the documented escape hatch for a host that deliberately wants
relevance-style full-text matching instead of substring semantics for that field.

**Rationale.** ES/OpenSearch's idiomatic "search for text" mechanism is `match` against an analyzed
field, and a naive compiler reaches for it because the operator is literally named `contains` and
`match` reads as "does it contain this". That is the tokenized-match trap: `match` splits both the
indexed field and the query string into analyzer tokens (lowercased, stemmed, stopword-filtered) and
matches when any *token* overlaps — it does not check for a literal substring. `contains: "art"`
against the indexed value `"Smart Watch"` returns **no hit** under `match` (the analyzer produces
whole tokens `"smart"` and `"watch"`, neither of which is `"art"`), while the identical filter
compiled to SQL (`LIKE '%art%'`) or Mongo (`$regex`) matches that row. The same IR silently diverges
across backends — precisely the parity failure G3 exists to prevent, and the TRD's problem statement
(§1) calls out ES/OpenSearch support as the one thing "nothing exists" for in any prior art, largely
because this exact trap is unsolved elsewhere. Pinning substring semantics trades away ES's own
relevance ranking for `contains`; `searchMode: analyzed` exists so a host can make that trade
deliberately, per field, instead of getting it by accident.

**Worked example** — `contains: "art"` against `name`, where a document has `name = "Smart Watch"`:

- **Substring semantics (the default this rule pins)** — `wildcard` against the `keyword` sub-field
  matches, because the literal substring `"art"` is present, exactly mirroring `LIKE '%art%'`:
  ```json
  { "wildcard": { "name.keyword": { "value": "*art*", "case_insensitive": true } } }
  ```
  A per-field ngram-indexed sub-field (e.g. `name.ngram`, matched with a plain `match` against *that*
  field) is the higher-index-cost/lower-query-cost alternative mechanism SC-3 lets a field choose
  instead — same illustrative substring outcome, different index-time/query-time trade-off (PERF-4).
- **The tokenized trap being explicitly ruled out** — shown here only as the anti-pattern the rule
  exists to forbid, never emitted by the compiler for `contains`:
  ```json
  { "match": { "name": "art" } }
  ```
  — no hit against `"Smart Watch"`, which is exactly the silent divergence from SQL/Mongo this rule
  prevents.
- **`searchMode: analyzed` opt-in** — when a field is explicitly configured this way, `contains`
  compiles to the tokenized form instead, as a documented, deliberate divergence:
  ```json
  { "match": { "name": { "query": "art", "operator": "and" } } }
  ```
- **For comparison, the same logical outcome on the other backend families**, achieved without an
  opt-in since neither needs one: SQL `LIKE`-based substring matching, with the pattern's own
  `%`/`_` wildcard characters escaped per SEC-4 **and an explicit `ESCAPE` clause naming the escape
  character used** — required because, unlike PostgreSQL (whose default `LIKE` escape character is
  `\` unless overridden), SQL Server, SQLite and Oracle have **no default escape character at all**
  and MySQL's depends on the `NO_BACKSLASH_ESCAPES` session mode; without a compiler-supplied
  `ESCAPE` clause, SEC-4's escaping is silently a no-op on four of the five SQL dialects, and a
  user-supplied literal `%`/`_` still behaves as a wildcard:
  ```sql
  WHERE name LIKE '%' || :escapedPattern || '%' ESCAPE '\'
  ```
  MongoDB has no native non-regex substring operator, so `contains` there is necessarily a `$regex`
  built from an already-escaped literal — never compiled from a client-supplied pattern, consistent
  with SEC-5's exclusion of user-controlled regex (ReDoS surface). The escaping here is a **distinct
  character class from SEC-4**: SEC-4 covers SQL `LIKE` wildcards (`%`, `_`, `\`) and ES wildcard
  syntax (`*`, `?`) — it says nothing about *regex* metacharacters. Building `$regex` from a
  SEC-4-escaped-only pattern still leaves `.`, `+`, `*`, `(`, `)`, `[`, `]`, `{`, `}`, `|`, `^`, `$`
  live in the pattern, which is both a match-scope-widening bug (the pattern matches more than the
  literal substring the user typed) and, combined with attacker-influenced repetition/alternation,
  a ReDoS vector — the exact surface SEC-5 excludes user-controlled regex operators to avoid,
  reopened here unless this document pins its own escape set. The value MUST be escaped against
  every regex metacharacter (a full backslash-escape of every character outside `[A-Za-z0-9]`, not
  just SEC-4's four LIKE/wildcard characters) before insertion:
  ```json
  { "name": { "$regex": "escaped-pattern-here", "$options": "i" } }
  ```

**Must NOT.**
- Must NOT compile `contains`/`notContains` to a `match` query as the default — that is the
  tokenized-match trap this rule exists to rule out.
- Must NOT emit a SQL `LIKE` pattern without a matching `ESCAPE` clause — SEC-4's wildcard-escaping
  is a no-op on any dialect without a default escape character (SQL Server, SQLite) or with
  backslash-escaping disabled (MySQL's `NO_BACKSLASH_ESCAPES`). **Read this together with SQL-5 in
  the canonical-rendering addendum**, which is where the per-dialect answer lives: the operator, the
  `ESCAPE` clause's spelling, and the escape *alphabet* all vary. Taken literally as a universal
  rule, this bullet is wrong on two of the four v1 SQL dialects — SQLite's case-sensitive path is
  `GLOB`, which has no `ESCAPE` clause at all, and SQL Server's alphabet includes `[` on top of
  SEC-4's three.
- Must NOT build the MongoDB `$regex` value by applying only SEC-4's LIKE/wildcard escape set —
  regex metacharacters are a distinct, wider class (see the worked example above) and must be
  escaped in full before insertion; SEC-4 alone is not sufficient here and must NOT be treated as
  though it were, never compiled from a client-supplied regex (SEC-5).
- Must NOT let `searchMode: analyzed` become a silent or implicit default for any field — it is an
  explicit per-field opt-in (SC-3); its absence always means substring semantics.
- Must NOT assume a `wildcard` query is index-friendly without a `keyword` (or normalizer-equipped)
  field/sub-field behind it — running `wildcard` against an analyzed text field undermines the point
  of pinning substring semantics and belongs, if used, in the per-backend index-guidance docs
  (PERF-4) as an explicitly documented trade-off, not assumed silently here.

**Cross-links.** `$defs/binaryOperator` (`contains`, `notContains`), `$defs/stringValue` (the value
being substring-matched), `$defs/fieldName`.

---

## Addendum — aggregation semantics (not numbered SEM-*, since it's beyond the TRD's own §7
enumeration of SEM-1..SEM-7; pinned here anyway because the conformance fixture suite, #1630,
needed answers that AR-8/BM-2 don't give and no other document owns)

**AGG-1. `count` aggregates non-null occurrences of the named field — never a row count.** AR-8's
`aggregation` is `(field, func)`; for `func: "count"`, the field is not decorative — `count` means
"how many rows have a non-null value for `field`", identically to SQL's `COUNT(field)` (which
already skips NULLs) and Elasticsearch's `value_count` (which already skips missing). **MongoDB's
naive `{ "$sum": 1 }` counts every document in the group regardless of the field's value and MUST
NOT be used** — it silently answers a different question the moment the aggregated field can be
null. The correct, portable Mongo form is a conditional sum:
```json
{ "$sum": { "$cond": [ { "$eq": ["$amount", null] }, 0, 1 ] } }
```
If a future host genuinely wants "count of rows in the group" regardless of nullability, that is a
`count` on the grid's own `stableKey` (never null by SEM-6/BM-2's own tiebreaker guarantee), not a
different meaning smuggled into `count` on an arbitrary field.

**AGG-2. `aggregation.alias` is set from the AG Grid `ColumnVO.id` whenever it differs from
`ColumnVO.field`; otherwise it is absent (compiler default applies).** AG Grid's `valueCols` entries
carry both an `id` (the column's identity in the grid) and a `field` (the data field being
aggregated) — when a host configures a distinct `id` for an aggregated column (a common pattern for
disambiguating two aggregations on the same field, e.g. `sum(amount)` and `avg(amount)` both keyed
off `field: "amount"`), that `id` is the label the grid UI already displays for the column and is the
only signal available for what the output key should be. The adapter therefore sets
`aggregation.alias := ColumnVO.id` **if and only if** `ColumnVO.id !== ColumnVO.field`; when they are
equal (the common case — AG Grid defaults `id` to `field` unless the host overrides it), `alias` is
left absent and the compiler's own default naming applies. This is a one-line, deterministic rule —
"is `id` distinct from `field`" — precisely so that every fixture/port computes the identical answer
from the identical `ColumnVO` shape, rather than each guessing when alias-setting is warranted.

**AGG-3. A `groupKeys` entry is a typed value (AR-7), compiled into an equality predicate against
its group column.** It is a value like any other filter value and is typed from the group column's
declared schema type before the IR is built, never left as a raw wire string.

> **Superseded in part by GRP-2 (#1640).** This rule previously also said that the drill-down match
> "follows the group column's own case-sensitivity setting (SEM-1) — it is not automatically
> exact-match". That is now **wrong**: the grouping addendum makes the drill-down, the `GROUP BY` and
> the grouped `ORDER BY` exact together, because they must agree with one another or a child level
> stops summing to its parent's aggregate, and because a collated `GROUP BY` key is not valid SQL.
> The sentence is recorded here rather than deleted silently, since the affected artifacts
> (`group-aggregation/002`-`012`, `015` on all three backends) were authored against it.

**AGG-4. ES/OpenSearch `terms` aggregation ordering (BM-3 set-filter values, and any composite group
aggregation) is pinned to ascending by key value, not the engine default.** A bare `terms` aggregation
defaults to doc-count-descending, which would return set-filter values (and, for composite grouping,
bucket order) in a different order than Postgres's `ORDER BY` or Mongo's `$sort` on the same logical
request — a user-visible list-order divergence for an operation (BM-3) whose whole purpose is
presenting a sorted list of distinct values. Compilers MUST set `"order": {"_key": "asc"}`
explicitly on any `terms` aggregation backing a set-filter-values or grouped-drill-down request.

**AGG-5. Known, accepted divergence: composite-aggregation null-bucket placement on ES/OpenSearch.**
ES/OpenSearch's composite aggregation `missing_bucket: true` (the mechanism that keeps a null group
key from being silently dropped, matching AR-8/BM-2's intent) has no `missing_order` control in the
pinned 7.10-era subset — the null bucket sorts **first** regardless of the sort direction requested,
while Postgres (`NULLS LAST`/`NULLS FIRST` per SEM-3) and Mongo (the null-bucket emulation) both honor
the requested direction. This is documented here as an accepted, host-visible divergence (per the
TRD §15 risk register's own "documented-divergence register" precedent) rather than worked around
with a client-side reorder, which would defeat server-side paging's whole purpose for the grouped
case. Fixtures exercising composite null-bucket ordering mark this explicitly rather than silently
pinning three-way-identical output that ES/OpenSearch cannot actually produce.

---

## Addendum — grouping semantics (GRP-1..GRP-6; not numbered SEM-*, for the same reason as the
aggregation addendum, and added by #1640 because BM-2's own two open contradictions could not be
settled anywhere a fixture README would be the only record; GRP-6 added by #1641 for BM-3, which is
subsidiary to BM-2 and answers the same bucket-identity question about a different construct)

BM-2 (row grouping over AG Grid SSRM) is a *protocol*, not a single query shape: one request per
expanded level, each carrying the whole group path so far. #1630's fixtures pinned that protocol's
output before anything compiled it, and left two points genuinely undecided — whether a grouped
query folds case, and whether the drill-down conjunction is IR or clause text. Both are settled
here, because a rule that lives only in a category README is a rule the next language port will
re-derive differently.

**GRP-1. One request, one level: the dispatch is `groupKeys.length` against `groupCols.length`.**
Fewer keys than columns → a **grouped level**: bucket by `groupCols[groupKeys.length]` (the next
not-yet-consumed column, and only that one — never the whole remaining list), aggregate the
`valueCols`, and restrict to the already-consumed keys. Equal → the **drill-down leaf**: every
dimension is consumed, so the answer is raw rows restricted by those keys, with no `GROUP BY` and no
aggregation (`spec/fixtures/group-aggregation/005`, `006`). More keys than columns is a malformed
request and MUST be rejected — consuming only as many as fit returns a different bucket's rows under
a label the client did not ask for.

*Where the arity check belongs.* It is a property of the **request**, not of any one backend, and
`ir.schema.json` cannot express it (JSON Schema has no way to compare two arrays' lengths) — so it
belongs in the shared `groupSpec` validator every compiler already runs, not in a compiler. Put it in
one of them and the others must each re-derive it; its failure mode is silent, so the one that
forgets returns wrong rows rather than an error.

*The colIds are client-controlled, and pass the same boundary as a filter's.* `rowGroupCols` and
`valueCols` arrive off the wire exactly as `filterModel` does, so every `groupCols` entry and every
aggregation `field` MUST pass the SC-1 allowlist, and MUST additionally satisfy SC-2's per-field
`groupable` / `aggregatable` capability flags — the one place those flags are enforced at all.
**SC-1's Lenient mode does not apply to either.** Lenient may silently drop an unknown *condition*,
because the result merely widens and a host can knowingly accept that; neither of these is a
condition. Dropping a `groupCol` changes which level the response describes — and, with its
`groupKey`, returns a different bucket's rows under the label the client asked for, arriving
silently at exactly what the paragraph above rejects loudly. Dropping an aggregation hands the grid
a response missing the key it asked for, i.e. a column of blanks indistinguishable from real nulls.
Both are rejected in every mode. Pinned by `spec/fixtures/adversarial/003-unknown-colid-row-group`.

**GRP-2. A grouping request is *exact* on the protocol's own keys: no case-folding mechanism is
spelled on any of them.** There are exactly three: the `GROUP BY` key, the drill-down `WHERE`
predicates a consumed `groupKeys` entry compiles into, and a **grouped level's** `ORDER BY` keys.
All render bare, whatever the column's declared `caseSensitivity`. So `"EMEA"` and `"emea"` are two
buckets with two unambiguous labels — which is also what AG Grid's own client-side grouping does.
(A fourth key answers to this rule without being part of BM-2's protocol at all: BM-3's
`SELECT DISTINCT` column and its `ORDER BY`. GRP-6 makes the argument for it; everything below
applies to it unchanged.)

*Why the three must agree, and why "exact" is the only coherent answer.* Bucket identity is decided
by the `GROUP BY`; the drill-down that expands a bucket must select **exactly** the rows that
produced it, or the child level stops summing to the parent's aggregate; and the `ORDER BY` of that
same level orders the buckets the `GROUP BY` just produced. That rules out every mixed
answer. Between the two coherent ones, "collate everywhere" is not merely undesirable, it is
**invalid SQL**: `SELECT "segment" … GROUP BY "segment" COLLATE craticula_ci` groups by an
expression the select list no longer contains, and Postgres and SQL Server both reject it. Exactness
also avoids inventing a label rule for a folded bucket (is it `EMEA` or `emea`?), which nothing in
AG Grid's wire contract supplies.

*Scope — deliberately narrow, and narrower than #1640's first draft.* GRP-2 governs the keys that
**decide or reproduce bucket identity**, not "every key of a grouping request". Two things inside
one are outside it:

- **A user's own filter.** A pre-aggregation row filter (AR-5) selects the rows that will be
  bucketed and has no say in bucket identity, so it keeps SEM-1's per-field mechanism. One statement
  may therefore compare the same column twice by two mechanisms — `WHERE ("region" = $1 AND "region"
  ILIKE '%' || $2 || '%' ESCAPE '\')` — and that is correct, not the contradiction SEM-1 forbids:
  SEM-1 governs how a field's *values* are compared, GRP-2 governs *bucket identity*, a question
  SEM-1 never asks.
- **A drill-down leaf's `ORDER BY`.** #1640's first draft keyed the `ORDER BY` rule on "the request
  carries a `groupSpec`", which swept the fully-consumed leaf in with it; its review found that
  wrong. A leaf has no `GROUP BY` and no buckets. It orders raw rows by whatever column the user
  picked, which need not be a group column at all — `group-aggregation/005` sorts on `product`,
  neither a group column nor a group key — so the bucket-identity argument above simply does not
  reach it. It is an ordinary row query in this respect and **SEM-1 governs it**, collation
  included. Leaving it bare was also a live defect and not merely an inconsistency: a leaf that
  filters `lastName eq "smith"` and sorts on `lastName` would emit a collated `WHERE` beside a bare
  `ORDER BY` — one field, two mechanisms, one statement, which SEM-1's third Must-NOT forbids
  outright. Its drill-down `WHERE` is still exact, because that IS one of the protocol's keys.
- **A leaf that sorts on a *consumed* group column**, which is the boundary between the two bullets
  above. It emits an exact `WHERE "region" = $1` beside a collated `ORDER BY "region" COLLATE
  craticula_ci` — one field, two mechanisms, one statement, and yet correct: the `WHERE` has pinned
  that column to a single group key, so the ordering is a no-op over the returned rows and there is
  no comparison for the two mechanisms to disagree about. Stated because it looks exactly like the
  SEM-1 violation the bullet above exists to prevent, and a reviewer who spots it should be able to
  find it already answered rather than re-derive it.

The one thing that must never happen is the mix SEM-1 does forbid: a **grouped level** whose
`GROUP BY` and `ORDER BY` disagree with each other, which GRP-2's per-*statement* scope removes.

*Accepted divergence, and the host obligation it creates.* "Spell nothing" means "exact" only where
the engine's own default already is. It is on Postgres (deterministic default collation) and SQLite
(`BINARY`). It is **not** on SQL Server / Azure SQL (stock `SQL_Latin1_General_CP1_CI_AS`), MySQL /
MariaDB (a `_ci` family default), or **Oracle** (default column collation `USING_NLS_COMP`, resolved
through the *session*'s `NLS_COMP`/`NLS_SORT` — the same fact SQL-6 cites as why the case-**sensitive**
direction must be spelled `COLLATE BINARY` there): on all three, a grouped string column folds case
and `"EMEA"`/`"emea"` land in one bucket. A host that wants exact buckets on those engines must
declare the groupable column with a binary / `_as_cs` / `BINARY` collation in its DDL — or, on
Oracle, run the session with `NLS_COMP = BINARY` — the same kind of obligation SQL-6 already records
for MySQL's case-sensitive direction, and for the same reason (no portable in-statement spelling
exists that does not break the `GROUP BY`). Recorded here as a documented divergence rather than
papered over; the execution-equality tier (T-1b, #1624) is what can grade it, and T-1a cannot see it
at all.

*Oracle is on that list for exactly the reason SQL-6 puts it there, and the two must not drift apart.*
A bucket-identity key resolves to `NotApplicable`, so on Oracle it emits nothing — while a
`caseSensitivity: "sensitive"` **filter** on the same column emits `COLLATE BINARY`, precisely because
bare is not exact there. A host running `ALTER SESSION SET NLS_COMP = LINGUISTIC NLS_SORT = BINARY_CI`
therefore gets a drill-down `"region" = :p1` that folds case, so expanding the `"EMEA"` bucket also
returns `'emea'`'s rows and the children stop summing to the parent's aggregate — silently, on
well-formed SQL, and invisibly to T-1a. Stated because the two paragraphs sit four lines apart in this
document and an earlier draft of #1642 had them disagreeing.

*Per-backend consequences beyond SQL.* ES/OpenSearch drops the `case_insensitive: true` parameter
from a drill-down `term`, leaving `{"term": {"<field>.keyword": {"value": "EMEA"}}}`, the same
exact-term form `dialect-parity/014` already uses; a composite `terms` source over a `keyword` field
is exact already and needs nothing. Its per-clause granularity means it can satisfy the scope
carve-outs above exactly — a bare drill-down `term` beside a `wildcard`/`case_insensitive` user
filter, and beside a leaf `sort` on a `search.normalizedSubField`, all in one request.

*Accepted divergence #3 — MongoDB cannot satisfy this rule and the carve-outs above at the same
time.* MongoDB's `collation` is a **query-level** option: it governs `$match`, `$group` and `$sort`
together, and there is no per-stage form. So a grouping request whose scope carve-outs ask for a
case-insensitive comparison somewhere and an exact one somewhere else has no pipeline that expresses
both, and the "a grouping request's pipeline carries no `collation` at all" shorthand an earlier
draft used hid a real choice rather than describing a consequence-free one. Three shapes reach it:

- a grouped request carrying a **user `eq`/`in` filter** on a case-insensitive string (SEM-1 wants
  the collation; the `GROUP BY` and drill-down forbid it),
- a **drill-down leaf** sorting a case-insensitive string (SEM-1 wants it; the drill-down `WHERE`
  forbids it) — which `group-aggregation/005` and `006` now are, and
- a **BM-3 distinct-values request** (GRP-6) carrying a user filter on some *other* case-insensitive
  string — SEM-1 wants the collation for that `$match`, while GRP-6(a)'s exact buckets forbid it on
  the `$group` that computes the distinct set. This one is easy to miss because the conflicting pair
  sits on **two different fields**: the collation is wanted for `segment` and forbidden for `region`,
  yet MongoDB has only one switch for both.

**The protocol's keys win, on all three: the pipeline carries no `collation`.** Bucket identity is a
correctness property — get it wrong and a child level's rows stop summing to the parent's aggregate,
silently — while a case-folded filter or sort is a narrower, visible difference in which rows come
back and in what order. So on MongoDB a user's own filter compares case-**sensitively**, and a
leaf's `$sort` orders case-sensitively, where the five SQL dialects collate both. Recorded here as a
documented, host-visible divergence, in the same register as GRP-2's SQL Server/MySQL DDL obligation
and AGG-5: no fixture reaches the first shape (no grouped case carries both `groupKeys` and a
`filterModel`), `005`/`006`'s Mongo arms pin the second, and `016`–`019`/`022`'s pin the third. The
execution-equality tier (T-1b, #1624) is what can grade it; T-1a cannot see it.

*How the third shape was found, because the same trap is waiting for every other backend with a
query-level switch.* `017`'s Mongo arm was authored in #1641 carrying a query-level `collation`
beside a `$group` on `region` — the exact combination #1640 had already stripped from every grouped
arm, reintroduced on a query nobody thought of as "grouped" because it carries no `groupSpec`. The
SQL arms were right the whole time; only Mongo, where one option governs every stage at once, could
express the contradiction at all. **The test is not "does this request have a `groupSpec`" but "does
this pipeline compute a distinct set" —** and under GRP-6 a BM-3 request always does.

**GRP-3. A grouping request names its output columns at every level; an ungrouped one does not.**
A grouped level projects `<group column> AS <logical field name>` — aliased **always**, even when
the physical column and the logical name coincide — followed by one aggregate per `valueCol`. A
drill-down leaf projects every field the schema declares, in declaration order, aliased back to its
logical name **only where the physical column differs** (`"fiscal_year" AS "fiscalYear"`, but plain
`"region"`). *"Declaration order" means the order of the `fields` object's members in the schema
**document***, and an implementation must take it from there: the natural shortcut — enumerating
whatever map the schema was deserialized into — is a language-specific accident (`Dictionary<K,V>`
in .NET, `dict` in Python and `Map` in JS happen to preserve insertion order; Go's `map` and Java's
`HashMap` emphatically do not), and `group-aggregation/005`/`006` compare this column list byte for
byte. `SELECT *` is not available to the protocol at either level: it returns *physical*
column names, and a grid that asked for `fiscalYear` cannot read a `fiscal_year` key. An **ordinary
row query** is out of GRP-3's scope and still renders `SELECT *`; widening that belongs to the export
feature, which owns the request-supplied column list (EXP-3) any general projection rule has to
compose with. *"Ordinary row query" and not "ungrouped"*: BM-3's distinct-values query also carries no
`groupSpec` and is emphatically not `SELECT *` — it projects the one target column, unaliased
(GRP-6(e)). The same distinction as in SEM-6's first blockquote, for the same reason.

**GRP-4. A grouped level orders by what it projects, and breaks ties on its group column.**
- A sort key naming an **aggregated column** orders by that aggregate's **expression**
  (`ORDER BY SUM("amount")`), never by the raw column, which the projection does not contain. The key
  is matched against `alias ?? field`, i.e. AGG-2's `ColumnVO.id` reconstructed — the grid sends the
  *column's* id, and AGG-2 sets the alias from that id exactly when it differs from the field.
- **Repeat the expression; do not refer back to the output name.** An output column name is only
  *conditionally* usable in `ORDER BY`: PostgreSQL's `SELECT` documentation states that it "has to
  stand alone, that is, it cannot be used in an expression", and T-SQL binds the same way — an alias
  inside a `CASE` resolves against the `FROM` scope and raises "Invalid column name". It rarely
  stands alone here, because SEM-3's null placement is **emulated** on two of the five dialects
  (`CASE WHEN … IS NULL THEN 1 ELSE 0 END` on SQL Server, `ISNULL(…)` on MySQL), so the ordering
  target sits inside an expression on both. The expression form is unambiguously legal in either
  position on all five engines, so it is used uniformly rather than only where the wrapper forces
  it — a per-dialect split would make a *semantic* difference look like a spelling one. (#1640's
  first draft emitted the name; its review found `020`'s mssql and mysql arms were the T-SQL failure
  above.) Mongo and Search are unaffected and keep the name: after `$group` the aggregate is a real
  field of the document a later `$sort` stage reads, and ES's `bucket_sort` addresses a
  sub-aggregation by a `buckets_path`, not by an alias.
- An aggregate with no alias takes the compiler default **`<field>_<func>`** (`amount_sum`,
  `id_count`) — stated here because it lands in query text as an identifier and every port must
  choose the same one. It names the aggregate in the projection and in the response, which is what
  the grid reads it back by; only the `ORDER BY` avoids it.
- SEM-6's tiebreaker still applies, but the **group column** plays the stable key's role: the
  schema's `stableKey` is not in the grouped projection, so ordering by it is invalid SQL, while the
  group column's values are unique across the level's rows by construction. Appended last, ascending,
  nulls last, and not appended twice when the sort already ends in it — SEM-6's own rules, unchanged.
- A sort key that is neither the group column nor an aggregate output MUST be rejected rather than
  rendered; the engine would refuse the statement anyway, and a compiler error names the reason.
  **This is a statement about the IR's colId space, not AG Grid's** — see AG-6 below, which is what
  makes the two the same space by the time a compiler sees them.
- A drill-down **leaf** is an ordinary row query in this respect and keeps SEM-6's `stableKey`.

**AG-6. AG Grid addresses a grouped sort through the row-group *display* column, whose colId the
adapter must resolve before the IR is built.** `sortService.getSortModel()` writes
`colId: column.getId()`, and for the auto group column that id is
`columnUtils.ts`'s `GROUP_AUTO_COLUMN_ID = 'ag-Grid-AutoColumn'` — or, under
`groupDisplayType: 'multipleColumns'`, `` `${GROUP_AUTO_COLUMN_ID}-${rowGroupCol.getId()}` ``
(`autoColService.createOneAutoCol`). Neither matches any `GridSchema.fields` key, because it is a
colId in **AG Grid's** column space rather than the host's, and the SSRM sort listener passes it to
the server untouched. Resolving it is therefore the adapter's job by definition — nothing downstream
knows that AG Grid mints display columns — and until #1640's review nothing did it, which made the
single most common grouped-grid interaction, clicking the group column's header, a hard failure
against GRP-4's rejection above.

- **`ag-Grid-AutoColumn`** shows the whole group path, so it resolves to the group column *this
  request's own level buckets by*: `groupCols[groupKeys.length]`, GRP-1's dispatch exactly.
- **`ag-Grid-AutoColumn-<id>`** names its source column, and the suffix is that column's
  `ColumnVO.id` — looked up against the `rowGroupCols` entries' **ids**, which differ from their
  `field`s whenever a host gives a grouped column its own id.
- On a **drill-down leaf** every group column is consumed, there is no level to name, and every row
  shares the whole group path — so the key would order nothing and is **dropped**. SEM-6's
  tiebreaker still guarantees a total order.
- A suffix naming no `rowGroupCols` entry, and an auto-column colId on a request carrying **no group
  state at all**, are both left **unresolved and carried through verbatim**, so each fails downstream
  as the unknown colId it is. Dropping either is worse than mapping it: the response then comes back
  in a different order from the one the grid's own header shows, with no rejection and no log, and the
  auto-column prefix would be the only colId in the adapter with such a path — every other bogus colId
  reaches a compiler and is refused there. The refusal does come from the **compiler**, which resolves
  each sort field against the schema; sort colIds have no allowlist stage of their own yet (SEM-6's
  own note about the orchestrator that does not exist applies here too), so "it reaches the field
  allowlist" — which an earlier draft of this rule said — is aspirational rather than true.
- **Collapse the coupled set afterwards.** AG Grid's sort *coupling*
  (`gridOptionsUtils._isColumnsSortingCoupledToGroup`, true unless `treeData` or a custom
  `autoGroupColumnDef.comparator`) sets the sort on `[column, ...sortableRowGroupColumns]` — the
  display column **and every source column** `showRowGroupColsService.getSourceColumnsForGroupColumn`
  returns for it — and `getIndexedSortMap` gives them all the display column's index, so they are all
  emitted. For the **default single display column that is every row-group column**, not just the
  level it resolved to: `autoColService.createBaseColDef` sets
  `showRowGroup: rowGroupCol?.getColId() ?? true`, i.e. the literal `true`, and that service answers
  `true` with `rowGroupColsSvc.columns.slice(0)`. So with `rowGroupCols: [region, segment, product]`
  one user click emits `[ag-Grid-AutoColumn, region, segment, product]` — 1 + N, not a pair — and AG
  Grid means **one logical key** by it. Collapse the whole coupled set into the entry the display
  column resolved to, keeping the **first** occurrence so the user's key precedence is preserved.
  A **suffixed** display column is the other arm of the same service method: its `showRowGroup` is one
  column's id, so its coupled set is that one column and only it collapses. Scoped to entries the
  resolution itself accounts for, and only after the display column that named them: a key the client
  sent on its own is left alone. Pinned by `group-aggregation/021`.
  *(Keeping the surplus entries is not a cosmetic duplicate — with the payload above they leave the
  request sorting on a column the drill-down has pinned to one value and on a level this response does
  not produce, both of which GRP-4 rejects outright.)*

**GRP-5. The drill-down is materialised as real IR, and merged with the user's filter through the
same merge primitive as everything else.** Each consumed key becomes an `eq` comparison node against
its group column (AGG-3's typed value), the set is wrapped in an `and` node when there is more than
one, and the result is `and`-merged with the request's own filter tree exactly as `customFilters`
and static filters are (AR-5/OQ-11's one merge primitive). It is **not** assembled as clause text.

*Consequence, and the contradiction this closes.* Because it is ordinary IR, SQL-2's "every composite
wraps" applies to it with no exception: two or more consumed keys render `("region" = $1 AND
"segment" = $2)`, wrapped. #1630's fixtures had them bare, on the reading that a synthesised
conjunction is not an IR construct; that reading is rejected, because any compiler that reuses its
filter renderer for the drill-down — which is every sensible one — gets the wrapped form for free.
A single consumed key is not a composite and does not wrap.

*Order.* The drill-down leads: `WHERE (<group path> AND <user filter>)`. Nothing in the fixture suite
pins this (no case carries both), and it is stated rather than left open because the choice is
visible in `params`, which is compared exactly.

*Provenance must survive the merge.* The two halves are ordinary IR and indistinguishable by shape —
a `groupKeys` match on `region` and a user `eq` on `region` are the same node — yet GRP-2 gives them
different answers. **A compiler must therefore carry the distinction itself and must not try to
recover it from the merged tree.** Reference identity happens to work and is a trap: any later change
that rebuilds nodes between synthesis and rendering (flattening nested `and`s, or routing the group
columns through an allowlist/coercion stage, both of which mint new nodes) silently gives the
drill-down the user's mechanism, `"EMEA"` starts matching `"emea"`, and child rows stop summing to
the parent — with no error and no failing artifact. Rendering each half under its own resolution
context and composing the wrapped `and` from the two results is equivalent, byte for byte, and has no
such invariant to break.

*Resource limits (SEC-3), on two levels.* `groupKeys` is as client-controlled as any filter value, so
N consumed keys are N leaf predicates that must be counted against the SEC-3 leaf/depth budget
**together with** the user's filter, over the merged tree. This is an amplification BM-2 introduced:
before it, group state was ignored entirely. But that check only runs where the filter pipeline runs,
and a host may hand a `groupSpec` straight to a compiler — so `ir.schema.json` additionally carries a
**structural** `maxItems` on `groupCols` (and, since GRP-1's cross-array comparison is inexpressible
there, on `groupKeys` too), enforced by the same shared validator GRP-1's arity rule lives in. That is
an amplification cap in the register of `fieldName.maxLength`, deliberately **not** one of SEC-3's
named, host-configurable limits — its value is fixed at SEC-3's own *default* max-leaves, so a
structurally valid `groupSpec` can never exceed that budget by itself, while a host configuring
something tighter still gets the tighter answer from the runtime validator.

*Type coercion (SEM-5).* A `groupKey` is a filter value, so it must satisfy its group column's
declared type before a compiler sees it — a compiler that binds it is entitled to assume this, exactly
as it does for a filter value. It differs from a filter value in one respect that decides *how*: the
wire already carries each key **pre-typed** as a `typedValue` (`$defs/groupSpec` requires it, and a
frontend adapter resolves the raw wire string using the group column's declared type before building
the request), so there is nothing untyped to parse. A variant that disagrees with the declared type is
therefore a **wrongly-typed request**, rejected at the `typeCoercion` stage with SEM-5's own
`uncoercible-value` reason — never repaired, and never allowed to surface further downstream as a
compiler precondition failure on ordinary client input.

**GRP-6. BM-3's distinct-values query is a bucket query, and its cap is a ceiling rather than a
page.** A set filter asks the server "which values may I offer for this column, given the filters
currently in effect?". The answer compiles to `SELECT DISTINCT <col> … ORDER BY <col> <cap>` (a
`terms` aggregation on ES/OpenSearch, a `$group` on `_id` on MongoDB), and five things about it are
decided here because each of them is a place a port would otherwise guess differently.

*(a) The `DISTINCT` key and its `ORDER BY` are exact — GRP-2's rule, reached by GRP-2's argument.*
`SELECT DISTINCT` computes a distinct set exactly as `GROUP BY` does; it is a bucketing operation
whose buckets happen to have no aggregate attached. A collated one folds `"EMEA"` and `"emea"` into a
single entry whose label is unspecified — and that entry is precisely what the grid hands straight
back, as an `in` value in the column's own filter or as a `groupKey` into a grouped request, where
GRP-2 matches it **exactly** and it selects neither original row. So the values a set filter offers
must be the values the data actually holds, one entry per distinct spelling. Read as an ordinary
query instead, SEM-1 would collate both positions; that reading is rejected, and the reason is the
same one GRP-2 gives: SEM-1 governs how a field's *values* are compared, not what a bucket *is*.
The consequences GRP-2 records carry over verbatim — including that "spell nothing" is exact only
where the engine's own default already is, so exact set-filter values on SQL Server and MySQL are the
same host DDL obligation, gradeable only at the execution tier (T-1b, #1624).

*(b) The target column's own filter is dropped; every other column's is honoured.* This is BM-3's
defining semantic and the AG Grid convention it names. A value list narrowed by the selection already
made on its own column collapses to what is already selected, and the user can never widen it again.
Everything else in the filter model still applies, so the list stays consistent with the rows the grid
is showing. **Where it happens matters**: it is a semantic rule every backend shares, so it belongs in
the request pipeline, not in one compiler and not in a frontend adapter — a host building the IR
directly must get it too. Conditions are pruned, not branches: `(region eq "EMEA" AND segment eq
"Enterprise")` targeting `region` leaves `segment eq "Enterprise"`, and a `not` whose only condition
was on the target vanishes with it rather than becoming a negation of nothing. Pinned by
`spec/fixtures/group-aggregation/017`, whose artifacts are byte-identical with and without the
`region` entry in its `filterModel` — which is the whole point of the fixture and, until #1641, was
not true of it: it carried no `region` filter at all and therefore tested nothing.

*(c) The search prefix is an ordinary `startsWith` predicate on the target column.* The set filter's
inline search box narrows the candidate values; modelled as IR it is a `startsWith` comparison
carrying the raw text, so SEM-1 picks the field's own mechanism for it and SEM-7's escaping applies
unchanged — a literal `%` typed into the box cannot widen into a wildcard, and no backend needs a
second prefix-matching path. It is therefore **not** exempt from (a)'s exactness in the way that
looks contradictory: one statement legitimately matches the column case-insensitively and orders it
exactly, which is GRP-2's own first carve-out (a filter selects rows, it does not decide bucket
identity) applied to a single column. *Order:* the user's filter leads and the prefix trails —
`WHERE (<filter model, minus this column> AND <prefix>)`. It was stated here rather than left open
because the choice is visible in `params`, which is compared exactly, and #1641's review then gave it
an artifact of its own: `spec/fixtures/group-aggregation/022` carries a filter on another column, the
target column's own filter, and a `searchPrefix`, all in one request, so the order is now pinned on
seven backends instead of resting on this sentence. (It is also the only case where the exclusion and
the prefix act on the **same** column, which is what makes "prune the model, then inject the prefix"
distinguishable from "prune the finished tree".) A prefix against a column that is not declared `string` has no meaning and
is a wrongly-typed request, rejected at the `typeCoercion` stage with SEM-5's `uncoercible-value`.

*(d) The cap is applied, never enforced, and never absent.* The compiled limit is
`min(requested ?? default, maximum)`, from two host-configured SEC-3 numbers: a **default** for a
request that asks for no cap of its own, and a **maximum** no request may exceed. A client asking for
more values than the host will serve has not malformed anything — a set filter over a
high-cardinality column asks for as many as it can get by design — so the server clamps and answers
rather than rejecting; `group-aggregation/019` requests 50,000 against a 10,000 maximum and compiles
to 10,000, while `016`-`018` request none and compile to the 1,000 default. The two numbers are
separate knobs, because they answer different questions (what a quiet client gets versus what the
loudest one may ever get) and a host raising the ceiling for one explicit request has not agreed to
send that many values to every set filter that opens. "No cap" is not an available answer at all.

*(e) It is a cap, not a page — so there is no offset, and no tiebreaker.* The list is fetched once;
there is no second page. So the compiled statement carries the row limit **alone**: `LIMIT $1` with no
`OFFSET`, `$limit` with no `$skip`, `"size"` with no `"from"`. On engines whose row-limiting syntax
cannot stand alone this is a real per-dialect difference rather than a shortened clause — T-SQL has no
`LIMIT`, and its `FETCH NEXT` is illegal without an `OFFSET` — so a `Dialect` seam needs its own
member for it, distinct from the paging clause:

| Dialect | Row cap | Parameters bound |
|---|---|---|
| Postgres | `LIMIT $1` | 1 |
| MySQL / MariaDB | `LIMIT ?` | 1 |
| SQLite | `LIMIT ?` | 1 |
| SQL Server | `OFFSET 0 ROWS FETCH NEXT @p1 ROWS ONLY` | 1 — the `0` is **literal** |
| Oracle (#1642) | `FETCH FIRST :p1 ROWS ONLY` | 1 |

SQL Server's zero is written rather than bound because it is not a value the request carries: it is
the syntax that makes "no offset" expressible on an engine whose `FETCH` requires one, in the same
register as the `'%'` anchors and the `ESCAPE` character a compiler already emits as literals. Oracle's
row is a genuinely **fifth** answer and not SQL Server's with a different placeholder style — a
**third clause family** across the five: its row-limiting clause admits a bare `FETCH FIRST` with no
`OFFSET` before it at all, so it needs neither that literal zero nor Postgres's `LIMIT`. A port that
assumes T-SQL's spelling generalises to every `FETCH` engine emits an `OFFSET 0` Oracle does not want
and fails the arm. Every dialect therefore binds exactly one parameter here,
and `params` stays identical across the five arms of a BM-3 fixture. (`SELECT DISTINCT TOP (@p1) …` is idiomatic T-SQL and is deliberately not used: it
would put the cap in the select list, giving one engine a different statement shape.)

For the same reason SEM-6's tiebreaker does **not**
apply: the distinct column's values are unique across the result rows by construction, and the
schema's `stableKey` is not in the projection at all, so ordering by it would be the invalid SQL GRP-4
avoids at a grouped level. The single `ORDER BY` key is the distinct column, ascending, nulls last.
The projection carries **no alias**, unlike GRP-3's: the response is one column of scalars, not rows a
grid maps by key, so there is no name for a client to look anything up by.

*The target `colId` is client-controlled, and no mode may drop it.* It passes the SC-1 allowlist in
**every** schema mode, on GRP-1's reasoning taken one step further: a `groupCol` that Lenient dropped
would at least leave a differently-shaped answer, whereas the `colId` here *is* the question — drop it
and a compiler has no column to select, while an empty value list would tell the grid that the column
has no values rather than that the server declined to say. The capability flag it would be held to is
SC-2's `filterable` (a set filter's value list is the filter UI's own data source, so `groupable` and
`sortable` are the wrong questions even though the compiled query buckets and orders the column) —
that flag is enforced nowhere today, on this path or on the filter path, and closing the gap must
close both at once: refusing a column's set-filter values while still honouring that same column's
`filterModel` entry would be incoherent. The gap is named and owned rather than merely noted —
**#1872**, "Enforce SC-2 capability flags: `filterable` on the filter and set-values paths" — so a
port reading this rule knows it is reading a deferral with a date on it rather than a silent
omission.

**Cross-links.** `$defs/groupSpec` (`groupCols`, `groupKeys`), `$defs/aggregation`,
`$defs/sortItem`, `$defs/setValuesSpec`, AGG-1..AGG-4 (AGG-4 pins the `terms` ordering GRP-6(a)'s
ES/OpenSearch arm depends on), SEM-1, SEM-5, SEM-6, SEM-7, SQL-2, SQL-6, and BM-2/BM-3/AR-8/AR-9 in
the TRD. `spec/fixtures/group-aggregation/` is the artifact oracle for all six.

---

## Addendum — canonical SQL rendering (not numbered SEM-*, for the same reason as the aggregation
addendum: the TRD's §7 enumerates SEM-1..SEM-7 and stops, but the conformance fixture suite needs a
literal answer and no other document owns one)

The SEM-1..SEM-7 sections above pin *semantics* and say so explicitly — their snippets are
illustrative. That was enough while nothing tried to satisfy them. It stopped being enough in #1638:
the fixture suite (#1630) was hand-authored by seven parallel agents with no compiler to check it,
and a research pass found the **same IR construct rendered several different ways from identical
inputs** across categories — two spellings of `notIn`, two of `notContains`, two parenthesisations of
a null-inclusive negative, two of a composite `and`/`or`, two of `notBlank`, and identifier quoting
in one category out of nine. No single compiler can satisfy contradictory oracles under T-1a artifact
equality, so the answers are pinned here, once, and every language port inherits them instead of
rediscovering them.

Each rule below is the form the `spec/fixtures/*/expected/postgres.artifact.json` files now contain.
Postgres is the reference dialect (§5); where a rule is Postgres-specific rather than universal, it
says so and names what the other dialects do instead.

**SQL-0. `GridSchema.source` is where the table comes from.** A compiled query names a physical
collection — `FROM employees`, a Mongo collection, a Search index — and until #1638 that name existed
in no input: not in the AG Grid wire request (no SSRM payload carries one, and none should: it is
exactly the kind of thing SEC-1 keeps client-side callers away from), not in `GridSchema`, not in the
TRD. It was recoverable only from a fixture's directory name. `grid-schema.schema.json` therefore
declares a **required** `source` string, alongside the field allowlist (SC-2) and the per-backend
physical mappings (SC-4) that are already host-declared and already never client-influenced. A
compiler MUST take the collection from `schema.source` and MUST NOT accept one from the request.

**SQL-1. Always quote identifiers — every column, every alias, and the table.** Postgres renders
`SELECT * FROM "employees" WHERE "hire_date" >= $1`, never the bare form.

*Why.* Unquoted identifiers are not "the same thing without the noise" — PostgreSQL **case-folds them
to lowercase**. The fixture suite contains real proof: `dialect-parity/` declares camelCase physical
columns (`lastName`, `employeeCode`, `jobTitle`) and, before #1638, emitted them unquoted, so
`WHERE lastName ILIKE ...` would reach the server as `where lastname ilike ...` and fail against the
table those fixtures describe. The artifact-equality tier (T-1a) cannot see that class of bug at all
— it compares strings — so it would have sat in the oracle until the execution-equality tier (T-1b,
#1624) ran the query against a real database. Quoting also makes `Dialect.QuoteIdentifier`
unambiguous: it is applied to *every* identifier, so there is no second rule about when to skip it,
and no dialect needs a keyword blocklist to stay correct.

*Must NOT.* Must NOT quote conditionally ("only when it needs it", "only mixed-case names") — that
reintroduces the blocklist and makes the fixture oracle depend on a keyword list that differs per
dialect and per server version. Must NOT quote things that are not identifiers: the collation name in
`COLLATE craticula_ci`, parameter placeholders, and the `'%'` / `'\'` / `''` literals of the ILIKE and
blank scaffolding are all left exactly as they are.

*The rule is universal, and as of #1639 so is the fixture suite.* #1638 re-authored only the
**Postgres** artifacts, because the *rule* ("always quote") is dialect-independent while the *quote
character* is not, and choosing each engine's is part of writing that engine's `Dialect`. #1639 wrote
the other three and closed the gap: **all 55 `mysql`/`mssql`/`sqlite` arms in
`spec/fixtures/dialect-parity/` are now quoted**, so `006`-`022`'s parity sets are no longer mixed —
seven artifacts each since #1642 added the `oracle` arm, six before it. The characters, with the
escape each engine uses for one appearing *inside* an identifier:

| Dialect | Quote | Escape inside an identifier |
|---|---|---|
| Postgres | `"` | `"` → `""` |
| SQL Server | `[…]` | `]` → `]]` |
| MySQL / MariaDB | `` ` `` | `` ` `` → ```` `` ```` |
| SQLite | `"` | `"` → `""` |
| Oracle (#1642) | `"` | `"` → `""` |

MySQL uses backticks rather than `"` because `"` only quotes an identifier under `ANSI_QUOTES`, which
is off by default and is a session mode a compiler must not assume of a host's connection.

**Oracle is the second engine where quoting is a correctness rule rather than a hygiene one**, and it
folds the *other* way from Postgres: an unquoted identifier is upper-cased, so `FROM employees`
reaches the server as `FROM EMPLOYEES`. Always-quoting neutralises that in the statement but moves
the obligation into the host's DDL, because Oracle also *stores* an unquoted-`CREATE`d name folded —
so a table created as `CREATE TABLE employees` cannot be reached by `FROM "employees"` at all
(ORA-00942). Every `source` and physical column in this fixture suite is lowercase or camelCase
(`employees`, `sales_fact`, `lastName`, `fiscal_year`), so on Oracle they all require quoted DDL;
that is recorded as a host obligation in `spec/fixtures/dialect-parity/README.md`, not silently
assumed. Identifier **length** is a non-issue in the other direction: Oracle's limit is 30 bytes
before 12.2 and 128 from 12.2, this project's Oracle dialect assumes 12.2+ (see SQL-6), and the
longest identifier any fixture declares is `employeeCode` at 12 characters.

SQL-3 and
SQL-4 are now exercised on every dialect too (`017`/`018`'s new arms), and SQL-4 is where the five
genuinely part company: Postgres binds one array parameter, the other **four** — SQL Server, MySQL,
SQLite and Oracle — bind N scalars, so even `params.length` differs across the arms of one fixture.

**SQL-2. Parenthesisation is structural, not cosmetic — one rule per construct, applied always.**
Correct SQL admits many equivalent bracketings; artifact equality admits one. The four cases:

- **Composite `and` / `or` (`$defs/andNode`, `$defs/orNode`): the composite is wrapped, its children
  are rendered bare.** `(a AND b)`, `(a OR b OR c)` — never `(a) AND (b)`, never `((a)) AND ((b))`. A
  child that is itself a composite (or a construct with its own wrap, below) carries its own
  parentheses, so nesting composes: `((x OR y) AND z)`. A single-child composite still wraps: `(x)`.
- **A null-inclusive negative (SEM-2) is always wrapped**, whether or not it is the whole `WHERE`
  clause: `("age" <> $1 OR "age" IS NULL)`. Never bare at the top level. This is what makes the leaf
  safe to drop into any parent without the parent knowing what it contains — a top-level `OR` left
  unwrapped inside an `AND` parent is a silent correctness bug, and pinning the wrap unconditionally
  is what removes the whole class. **Unlike the quote character, this is not a per-dialect choice at
  all**: parenthesisation is structural, so a query builder emits it and the `Dialect` interface has
  no say in it (an artifact for *any* dialect that shows the disjunction bare is therefore
  unsatisfiable by construction, not merely unusual — `dialect-parity/008`'s `mysql`/`mssql`/`sqlite`
  artifacts were fixed on exactly that ground).
- **A tree-level `notNode` (`Not(child)`)** renders `(NOT (<child>) OR <col> IS NULL [OR ...])` — the
  inner parentheses around the child, the outer around the whole null-inclusive disjunction, and one
  `IS NULL` disjunct per distinct field referenced anywhere in the child subtree, in first-appearance
  order.
- **`blank` / `notBlank`.** `blank` is `("c" IS NULL OR "c" = '')` for a `string` field and `"c" IS
  NULL` for every other type (SEM-2's fourth must-not). `notBlank` is `NOT (<blank>)` **literally
  applied to that text**, which yields `NOT (("c" IS NULL OR "c" = ''))` for a string and
  `NOT ("c" IS NULL)` otherwise. The doubled parentheses in the string case are not an accident to be
  tidied away: they are SEM-2's "negate the compiled `blank` condition, never re-derive it" made
  visible in the output. Collapsing them by hand is the first step toward the two forms drifting.
  **On an engine whose empty string *is* NULL the text arm is not this text — see SQL-7**, which
  varies the arm and leaves the `NOT (<blank>)` composition above untouched.

**SQL-3. A negative operator renders with the negated operator, not an outer `NOT (...)`.**
`notContains` is `"c" NOT ILIKE '%' || $1 || '%' ESCAPE '\'`, not `NOT ("c" ILIKE ...)`.

*Why.* Every other negative operator already does this — `neq` renders `<>`, not `NOT (=)`; `notIn`
renders `<> ALL`, not `NOT (= ANY)` — so the outer-`NOT` spelling made `notContains` the single
exception, for no gain. Keeping the negation in the operator also keeps the `ESCAPE` clause attached
to the pattern-match expression it belongs to, which is what SEM-7 requires. Note the deliberate
contrast with the previous rule: an outer `NOT (...)` is correct and required for a **tree-level
`notNode`**, because there the negation applies to an arbitrary subtree, not to one comparison. The
distinction is the IR shape, never the surface similarity of the two.

**SQL-4. Set membership is one array parameter on Postgres — `= ANY($1)` / `<> ALL($1)` — and that is
a dialect rendering, not a universal one.** The set's values are bound as a **single** array
parameter, so the placeholder count never depends on the value count: `"status" = ANY($1)` with
`params: [["active", "pending"]]`, never `IN ($1, $2)` with two scalars.

*Why it is behind the dialect interface.* `ANY(array)` does not exist in SQL Server, MySQL or SQLite.
#1639 renders those three as `IN (...)` with N scalar parameters. The query builder therefore MUST
NOT emit either spelling itself — it asks the dialect for an in-list rendering and passes it the
field and the set. A builder that contains an `if` for this is the BM-1 smell
`docs/architecture/c4-components-sql-compiler.md` warns about. (This rule is also the reason the two
spellings must not be treated as interchangeable in a fixture: `params.length` differs between them,
so a port that picks the other one fails on the parameter list even when the SQL text is accepted.)

**SQL-5. The substring family's pattern operator, escape clause and escape alphabet are all per
dialect — three axes, not one.** SEM-7 mandates "a SQL `LIKE` pattern with a matching `ESCAPE`
clause" and, read literally, that is wrong on two of the four v1 dialects. The rule as the fixture
suite actually pins it (#1639's D1/D3, and its review pass; `spec/fixtures/dialect-parity/README.md`
keeps the story-level rationale):

| Dialect | Operator | Escape clause | Escape alphabet |
|---|---|---|---|
| Postgres | `ILIKE` (CI) / `LIKE` (CS) | `ESCAPE '\'` | `\` `%` `_` |
| SQL Server | `LIKE` (both, + a collation — SQL-6) | `ESCAPE '\'` | `\` `%` `_` **`[`** |
| MySQL / MariaDB | `LIKE` (both) | **`ESCAPE '\\'`** | `\` `%` `_` |
| SQLite | `LIKE` (CI) / **`GLOB`** (CS) | `ESCAPE '\'` / **none** | `\` `%` `_` / **class-wrapping** |
| Oracle (#1642) | `LIKE` (both, + a collation — SQL-6) | `ESCAPE '\'` | `\` `%` `_` |

Three of those cells are correctness, not spelling, and each is invisible to the artifact-equality
tier if a port gets it wrong in isolation:

- **MySQL spells the escape character `'\\'`.** With `NO_BACKSLASH_ESCAPES` off — MySQL's default —
  a backslash is the string-literal escape character, so `ESCAPE '\'` escapes the closing quote: the
  literal never terminates and *the statement does not parse*. The escape **character** is still `\`
  on all five dialects (Oracle included — a backslash is an ordinary character in an Oracle literal),
  so the bound value is byte-identical across the arms; only the SQL text differs.
- **SQLite renders `GLOB` for a case-sensitive substring match, and drops the `ESCAPE` clause.**
  Its `LIKE` is ASCII-case-insensitive unconditionally (it is a function call, ignores collating
  sequences, and `PRAGMA case_sensitive_like` is a connection-level setting a compiler must not
  depend on), so emitting `LIKE` for a field declared `caseSensitivity: "sensitive"` silently
  returns case-insensitive rows. `GLOB` is a different pattern language, not a synonym: its
  wildcards are `*`/`?`, so the anchoring becomes `'*' || :v || '*'`, and it accepts **no `ESCAPE`
  clause at all** — a literal `*`, `?` or `[` is neutralised by wrapping it in a single-member
  character class (`[*]`, `[?]`, `[[]`). SQL-3 still applies: the negated form is `NOT GLOB`.
- **SQL Server escapes `[` as well.** T-SQL's `LIKE` treats `[` as a character-class opener, so an
  unescaped user value `A[BC]` matches `AB` or `AC` rather than itself, and `[^x]`/`[a-z]` widen the
  match set further — SEM-7's own "must NOT" about a user-supplied literal still behaving as a
  wildcard. `]`, `^` and `-` need no escaping in turn, on either this dialect or SQLite's GLOB path:
  all three are special only *inside* a class, and once the opener is neutralised no class from the
  value can exist.

**Oracle contributes no fourth surprise, and that is worth stating** so a port does not copy the
wrong neighbour: it has no `ILIKE` (so the case-insensitive direction is a collation, like SQL
Server's), but a backslash inside an Oracle string literal is an *ordinary character*, so the clause
is `ESCAPE '\'` and MySQL's doubled spelling would be wrong; and its `LIKE` has exactly two
metacharacters, `%` and `_`, so the shared escape alphabet is the whole alphabet and T-SQL's `[` is
T-SQL's alone.

*Must NOT.* A port MUST NOT read SEM-7's "`LIKE` … with a matching `ESCAPE` clause" as universal:
on the SQLite case-sensitive path there is no `LIKE` and no `ESCAPE` clause to attach one to, and a
port that emits them anyway both fails `dialect-parity/021`'s sqlite arm and returns the wrong rows.
It MUST NOT widen the shared escape alphabet to the union of all five either — `\[` in a
Postgres/MySQL/SQLite/Oracle pattern is a literal backslash followed by a bracket, which is not what
the user searched for.

**SQL-6. Where an engine's default collation disagrees with the field's declared case sensitivity,
the compiler MUST spell the mechanism explicitly — in *both* directions.** SEM-1's second Must-Not
("must NOT default to case-sensitive because the engine's default is") has a mirror image that is
just as wrong and much easier to ship: leaving a field declared `caseSensitivity: "sensitive"`
*bare* on an engine whose default collation folds case. SQL Server and Azure SQL ship
`SQL_Latin1_General_CP1_CI_AS`; MySQL's server default is a `_ci` family; Oracle's default column
collation is `USING_NLS_COMP`, which resolves through the *session*'s `NLS_COMP`/`NLS_SORT`. On all
three, "emit no case-insensitivity mechanism" can mean "compare case-insensitively", so the host's
explicit opt-in is silently discarded and the SQL reads as though it were correct.

| Dialect | Case-insensitive | Case-sensitive |
|---|---|---|
| Postgres | `COLLATE craticula_ci` | nothing — default collation is deterministic |
| SQL Server | `COLLATE Latin1_General_CI_AS` | **`COLLATE Latin1_General_CS_AS`** |
| MySQL / MariaDB | nothing — column's declared collation | nothing — **host DDL obligation** (see below) |
| SQLite | `COLLATE NOCASE` | nothing — default sequence is `BINARY` |
| Oracle (#1642) | `COLLATE BINARY_CI` | **`COLLATE BINARY`** |

*Why Oracle spells both, and what version that pins.* Oracle has no `ILIKE`, so there is exactly one
mechanism to choose and it has to serve the exact-match family, ordering, set membership, the
`ORDER BY` **and** the substring family. Two candidates exist and only one can:

- **`COLLATE BINARY_CI`** — the 12.2 data-bound-collation operator. Applies to `LIKE` as well as to
  `=`/`IN`/`ORDER BY`, which is what makes SEM-1's "the same mechanism in every context on a field"
  achievable at all. **This is the choice**, and it pins **Oracle Database 12.2 or later** (with
  `COMPATIBLE >= 12.2` and `MAX_STRING_SIZE = EXTENDED`, data-bound collation's documented
  prerequisite) — a hard version floor of the same kind SQLite's 3.30.0 `NULLS FIRST/LAST` floor is,
  and equally a loud failure when unmet (ORA-43929) rather than a silent one.
- **`NLSSORT(col, 'NLS_SORT=BINARY_CI')`** — works back to 9i and pins no version, and is **rejected**:
  it cannot express a case-insensitive *substring* match, because a collation key of a substring is
  not a substring of that string's collation key. The substring family would have to fall back to
  `UPPER(col) LIKE UPPER(:v)`, leaving one field folding case by two different mechanisms depending
  on the operator — precisely what SEM-1 forbids. It also wraps the bound *value* in a function call.

Setting `NLS_COMP = LINGUISTIC` on the session is rejected on the same ground as SQLite's
`PRAGMA case_sensitive_like`: connection-level state a compiler must not depend on. And because the
default is that session state, the case-**sensitive** direction cannot be left bare either — hence
`COLLATE BINARY`. Unlike MySQL, Oracle has one portable spelling for it, so it is a token rather than
a host DDL obligation.

Whatever a dialect spells, it spells on **every construct where SEM-1 applies at all**, per SEM-1's
"same mechanism in every context on a field": the scalar comparison, the set membership, the
substring match and the `ORDER BY`. `dialect-parity/014` (`eq`), `/018` (`notIn`) and `/021`
(`contains`) pin the case-sensitive direction; `/006`, `/016`, `/007` pin the insensitive one.

The **exception is the bucket-identity keys**, where SEM-1 does not apply in either
direction: a `GROUP BY` key, a `groupKeys` drill-down match and a grouped level's `ORDER BY` spell
nothing at all (GRP-2), and so do BM-3's `SELECT DISTINCT` column and its `ORDER BY` (GRP-6). This
table therefore has no column for them, and on the **three** engines whose default folds case — SQL
Server, MySQL and Oracle, i.e. every row of this table that spells the case-sensitive direction as
something other than "nothing" — that "nothing" is a host DDL obligation of exactly the kind the MySQL
row below records — see GRP-2's own accepted-divergence paragraph. A drill-down **leaf**'s `ORDER BY` is not one of these keys and is
covered by the table normally; so is a set filter's search prefix, which is a filter on the distinct
column rather than the distinct key itself.

*Why MySQL is the exception, and what a host owes.* Case sensitivity there is a property of the
**column's** declared collation, which the statement cannot override portably — MySQL's
case-sensitive collation names are charset-specific (`utf8mb4_bin`, `utf8mb4_0900_as_cs`, MariaDB's
own set) rather than one spelling a compiler can pin. A field declared `caseSensitivity: "sensitive"`
therefore requires a `_bin`/`_as_cs` **column** collation in the host's DDL, or the declaration has
no effect. That is a documented host obligation (PERF-4's per-backend index-guidance page), not a
gap the compiler silently fills with a guessed collation name.

*Must NOT.* A compiler MUST NOT infer "case-sensitive" from the *absence* of a case-insensitivity
mechanism, and a `Dialect` seam MUST NOT carry this decision as a boolean: `false` would have to
mean both "text, compare case-sensitively" and "not a text comparison at all", and the two need
opposite renderings — SQL Server rejects a `COLLATE` clause on an `int`/`date` expression outright
("COLLATE clause cannot be used on expressions of type int"), and Oracle likewise accepts `COLLATE`
only on a character operand. The decision is three-valued at the seam or it is wrong at one end
of it.

**SQL-7. On an engine that treats the empty string as NULL, `blank`'s text arm is `IS NULL` and
nothing more.** SEM-2 and SQL-2 give `blank` two shapes chosen by *field type*:
`("c" IS NULL OR "c" = '')` for `string`, `"c" IS NULL` for everything else. On **Oracle** the first
one is wrong, and the second is what a `string` field must compile to as well.

| Dialect | `blank` on a `string` field | `blank` on any other type |
|---|---|---|
| Postgres | `("c" IS NULL OR "c" = '')` | `"c" IS NULL` |
| SQL Server | `("c" IS NULL OR "c" = '')` | `"c" IS NULL` |
| MySQL / MariaDB | `("c" IS NULL OR "c" = '')` | `"c" IS NULL` |
| SQLite | `("c" IS NULL OR "c" = '')` | `"c" IS NULL` |
| **Oracle** | **`"c" IS NULL`** | `"c" IS NULL` |

*Why this is a correctness rule and not a redundant-disjunct tidy-up.* Oracle documents that a
character value of length zero is NULL. So `"c" = ''` is `"c" = NULL`, which under SQL's three-valued
logic is **UNKNOWN for every row** — not FALSE, and that distinction is the whole rule:

| operator | a row whose value is NULL | a row whose value is `'abc'` |
|---|---|---|
| `blank` = `(c IS NULL OR c = '')` | `TRUE OR UNKNOWN` → TRUE → **matched** ✓ | `FALSE OR UNKNOWN` → UNKNOWN → **not matched** ✓ |
| `notBlank` = `NOT (…)` | `NOT TRUE` → FALSE → not matched ✓ | `NOT UNKNOWN` → **UNKNOWN → not matched** ✗ |

A `WHERE` clause keeps only rows evaluating to TRUE, so `blank` is accidentally right and
**`notBlank` returns the empty result set** — every row filtered out, on syntactically valid SQL,
with no error. That is a silently empty grid, and the artifact-equality tier (T-1a) cannot see it,
which puts it in the same class as MySQL's unparseable `ESCAPE '\'` (SQL-5) and a bare `LIKE` on a
case-sensitive SQLite field (SQL-5's second bullet).

*Where the decision belongs.* This is an **engine fact**, not a rendering: the `blank` composition —
and therefore SEM-2's "`notBlank` is `blank`'s compiled text negated, never re-derived" — stays in
the query builder, which asks the dialect a single yes/no question ("is `''` NULL here?") and picks
the arm. A `Dialect.renderBlank` method would instead have four of five implementations byte-identical,
and every one of the five would have to reach back into the semantics layer for the *field-type* half
of the answer. Equally, the question must NOT be answered wherever that field-type half is resolved
(Craticula's `SemanticsResolver`): that layer knows the schema and nothing about engines, and teaching
it a dialect name inverts the dependency the `Dialect` seam exists to draw. ("Otherwise `blank` and
`notBlank` could drift apart" is *not* one of the reasons, though it is the first one a reader
reaches for: the negation is composed over whatever text the `blank` half returned either way, so no
design in the running admits drift.)

*Must NOT.* A compiler MUST NOT emit the `= ''` disjunct on an engine where `''` is NULL "because it
is harmless" — it is not harmless, it poisons the negation. It MUST NOT fix only `blank` and leave
`notBlank` alone: `blank` is the operator that already worked, and a port that tests only the
positive direction will see nothing wrong. And it MUST NOT reach the collapsed form by special-casing
the *field type* (e.g. treating `string` as non-text on Oracle) — the type answer and the engine
answer are different questions that happen to coincide here, and conflating them changes SEM-1's
mechanism selection for the same field.

*The Must-NOTs above are not the whole truth about Oracle, and this list is not exhaustive.* One
Oracle value escapes the rule entirely: `EMPTY_CLOB()` produces a **zero-length LOB that is not
NULL**, so a column holding it is found by neither `IS NULL` nor `= ''`, and `blank` misses it on
either arm. Detecting it needs `dbms_lob.getlength(c) = 0`, which the compiler cannot emit because
`grid-schema.schema.json` carries no physical *type* — a field declares `string`, and whether the
host stored that as `VARCHAR2`, `NVARCHAR2` or `CLOB` is not in the contract. This is therefore a
**documented limitation of `blank` on Oracle CLOB columns**, not a defect in the rule: a host that
maps a `string` field onto a CLOB and writes `EMPTY_CLOB()` into it gets a row that `blank` does not
match and `notBlank` does. Recorded so the list above reads as complete about what it covers rather
than complete about Oracle. Closing it would need a physical-type declaration in the schema, which is
a `/v1/` contract change and belongs to a story of its own.

**SQL-7(b). The same engine fact makes an empty bound *value* uncompilable on Oracle, and a compiler
targeting it MUST reject rather than render.** `$defs/stringValue`'s `value` carries a `maxLength` and
no `minLength`, so `""` is a legal IR value on every backend. On Oracle it binds as NULL, and the
resulting statement is valid SQL that returns the wrong rows in silence:

| Request | Postgres / SQL Server / MySQL / SQLite | Oracle, if rendered |
|---|---|---|
| `eq ""` | the rows whose value is the empty string | **no rows** (`c = NULL` is UNKNOWN) |
| `neq ""` | every row except those, NULLs included (SEM-2) | **only the NULL rows** — wrong rows, not no rows |
| `contains ""` | every non-NULL row (`LIKE '%%'`) | **no rows** — `'%' \|\| NULL \|\| '%'` is itself NULL |
| `in ["", "x"]` | either value matches | the `""` member is inert; `notIn` matches **no row at all** |

So the rejection covers **all three paths a string value can reach** — the scalar comparison family,
set membership, and the substring family — and not merely `eq`. It is a compiler error naming the
engine fact, in the same register as GRP-4's "a sort key the projection does not contain is rejected
rather than rendered": a plausible statement returning wrong rows is worse than a refusal.

*Why this is scoped to the dialect and not to the IR.* Adding `minLength: 1` to `stringValue` would
forbid, across the whole contract and every backend, an input the other four engines handle correctly
— a `/v1/` schema change with its own compatibility question, deliberately **deferred to its own
story** rather than taken as a side effect of adding a dialect. A caller that wants to ask "is this
value empty or absent" is asking for SEM-2's `blank`/`notBlank`, which SQL-7 above compiles correctly
on Oracle. No fixture binds an empty value on any backend, so nothing in the suite changes.

*Pinned by* `dialect-parity/019`, whose `oracle` arm is the only one of that fixture's five SQL arms
that is not its Postgres arm with tokens substituted; SQL-7(b) is pinned by unit tests alone
(`DialectRenderingTests`), there being no artifact for a request that does not compile.

**Cross-links.** `grid-schema.schema.json` (`source`), `$defs/andNode`, `$defs/orNode`,
`$defs/notNode`, `$defs/unaryOperator` (`blank`, `notBlank`), `$defs/binaryOperator` (`notContains`,
`contains`), `$defs/setOperator` (`in`, `notIn`), `$defs/textOptions` (`caseSensitivity`), SEM-1,
SEM-7, SEC-4, and BM-1's `Dialect` interface.
