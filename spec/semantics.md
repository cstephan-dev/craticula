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

All SQL/Mongo/OpenSearch examples below are illustrative snippets to convey intent, not the literal
compiler output for any one dialect — dialect-specific rendering (identifier quoting, parameter
style) is BM-1's concern, not this document's.

---

## SEM-1. Case sensitivity

**Rule.** Every operator in `$defs/binaryOperator` (`eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `contains`,
`notContains`, `startsWith`, `endsWith`) and `$defs/setOperator` (`in`, `notIn`), when applied to a
`stringValue`, is **case-insensitive by default** — including the four ordering comparisons
(`gt`/`gte`/`lt`/`lte`), which fold case in their lexicographic comparison exactly as the
equality/substring operators fold case in theirs: case sensitivity is a per-field property (SC-3),
not a per-operator one (see the third "must NOT" below). Per-field, a schema author may opt a field
into case-sensitive comparison instead. Whichever mode is active, the compiler MUST reach it through
an index-friendly, backend-native mechanism (`ILIKE`, a case-insensitive collation, an ES/OpenSearch
case-insensitive query option, or an analyzed sub-field) rather than a function wrapper that defeats
ordinary indexing.

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

**Worked example** — `contains` on `name`, case-insensitive, matching `"Bob Smith"` against pattern
`"smith"`:

- **PostgreSQL** — `ILIKE`, backed by a `pg_trgm` GIN index (PERF-4 index guidance) rather than a
  functional index on `lower(name)`:
  ```sql
  WHERE name ILIKE '%' || :pattern || '%'
  ```
- **SQL Server / MySQL** — a case-insensitive collation on the column (or applied inline), so the
  engine's own comparison semantics do the folding and a plain B-tree index remains usable:
  ```sql
  -- SQL Server: column declared/queried with a CI collation
  WHERE name COLLATE Latin1_General_CI_AS LIKE '%' + @pattern + '%'
  -- MySQL: column already uses a _ci collation (e.g. utf8mb4_0900_ai_ci)
  WHERE name LIKE CONCAT('%', :pattern, '%')
  ```
- **MongoDB** — the index-friendly CI mechanism (a query-level collation) applies to `$eq`/`$in`/
  sort/range comparisons, not to `$regex`: a collection or query collation with `strength: 2` makes
  `$eq`/`$in`/`$gt`/`$lt`/`$sort` compare case-insensitively (and can still use a supporting index),
  which is the mechanism this document means wherever it says "the collation option for CI" (e.g.
  TRD §5's Mongo row):
  ```json
  db.items.find(
    { name: "smith" },
    { collation: { locale: "en", strength: 2 } }
  )
  ```
  `contains` specifically has no collation-compatible substring operator in MongoDB — it necessarily
  compiles to `$regex` with the `"i"` option instead, which is *not* index-friendly the way the SQL
  `pg_trgm`/collation forms above are; SEM-7 covers that mechanism (and its escaping requirements) in
  full, since it is really a `contains`-specific concern (substring semantics) rather than a
  case-sensitivity one.
- **OpenSearch / Elasticsearch** — a term-level query's `case_insensitive` parameter (available on
  `term`, `wildcard`, `prefix`, and `regexp` since 7.10 — **not** `range`, whose case-insensitivity
  question does not arise the same way for the ordering operators; see the third "must NOT" below)
  against a `keyword` field, so no separate analyzed sub-field or runtime lowercasing is required for
  this rule specifically (SEM-7 governs the *substring* mechanics of `contains` itself):
  ```json
  { "wildcard": { "name.keyword": { "value": "*smith*", "case_insensitive": true } } }
  ```
  For the ordering operators (`gt`/`gte`/`lt`/`lte`) on a text field, ES/OpenSearch has no
  `case_insensitive` flag on `range` — case-insensitive ordering there means comparing against a
  normalized (lowercased) `keyword` sub-field maintained at index time via an analyzer/normalizer,
  since there is no query-time equivalent.

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
  on the same field — case sensitivity is a per-field property, not a per-operator one.

**Cross-links.** `$defs/binaryOperator`, `$defs/setOperator`, `$defs/stringValue`.

---

## SEM-2. Null handling

**Rule.** The unary operators `blank`/`notBlank` (`$defs/unaryOperator`) resolve as: `blank` ⇔
`IS NULL OR = ''` for text fields, `IS NULL` for every other type; `notBlank` is `blank`'s exact
Boolean negation (never a separately-derived condition that could disagree with it at the edges).
Negative binary/set operators — `neq`, `notContains`, `notIn` — MUST include NULL-valued rows in
their match set. A negative comparison compiles as, e.g., `col <> :v OR col IS NULL`, never a bare
`col <> :v`.

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

- **SQL** — the explicit `OR IS NULL` is mandatory precisely because bare `<>` drops NULLs:
  ```sql
  WHERE name <> :v OR name IS NULL
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
- SQL: must NOT emit a bare `<>`/`NOT IN (...)` for a negative operator without the accompanying
  `OR col IS NULL` — three-valued logic drops NULLs from a bare comparison, which is the exact bug
  this rule exists to prevent.
- MongoDB: must NOT add a defensive `{ field: { $ne: null } }` guard "to be safe" or "for parity with
  SQL" — that guard would *exclude* null-valued documents from a negative comparison, which is the
  opposite of this rule and a Mongo-specific regression introduced by SQL-shaped intuition.
- Must NOT derive `notBlank` independently from `blank` (e.g. hand-writing `IS NOT NULL AND <> ''`
  separately from `blank`'s own `IS NULL OR = ''`) — always negate the compiled `blank` condition so
  the two can never drift apart.
- Must NOT apply the text-specific `= ''` clause of `blank`/`notBlank` to non-text field types.

**Cross-links.** `$defs/unaryOperator` (`blank`, `notBlank`), `$defs/binaryOperator` (`neq`),
`$defs/setOperator` (`notIn`), `$defs/nullValue`.

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
  pipeline, then sorted on the bucket first:
  ```json
  [
    { "$addFields": { "__amountNullBucket": { "$cond": [{ "$eq": ["$amount", null] }, 1, 0] } } },
    { "$sort": { "__amountNullBucket": 1, "amount": 1 } }
  ]
  ```
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
- **MongoDB** — dates are stored as BSON `Date` (always a UTC instant internally); both local-date
  and instant fields compare with the operators selected from the inclusivity flags:
  ```json
  { "createdAt": { "$gte": { "$date": "2026-01-01T00:00:00Z" }, "$lt": { "$date": "2026-02-01T00:00:00Z" } } }
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
  null-bucket emulation SEM-3 shows for `lastName`, not a bare `$sort` entry:
  ```json
  { "$sort": { "lastName": 1, "__lastNameNullBucket": 1, "id": 1, "__idNullBucket": 1 } }
  ```
  (in practice `stableKey` is usually a non-nullable primary/surrogate key, so the `__idNullBucket`
  stage is frequently a no-op the compiler can only omit if the schema declares `stableKey` NOT
  NULL — this document does not assume that, so the mechanism is shown in full.)
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
  `\` unless overridden), SQL Server and SQLite have **no default escape character at all** and
  MySQL's depends on the `NO_BACKSLASH_ESCAPES` session mode; without a compiler-supplied `ESCAPE`
  clause, SEC-4's escaping is silently a no-op on three of the four v1 SQL dialects, and a
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
  backslash-escaping disabled (MySQL's `NO_BACKSLASH_ESCAPES`).
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
