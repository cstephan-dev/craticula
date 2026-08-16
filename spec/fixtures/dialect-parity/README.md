# `dialect-parity/` — dialect-specific mechanics + cross-backend parity showcases (SEM-1/2/3/7)

Per `spec/fixtures/README.md`'s category index, this directory proves two distinct things, kept
structurally separate by coverage tier (see each case's own `description.md` for its tier and the
specific SEM rule/mechanism it pins):

1. **Dialect-specific mechanics** (`001`-`005`): a single backend's own native rendering of a
   semantics rule — MySQL's `ISNULL` null-ordering emulation, SQL Server's `OFFSET`/`FETCH NEXT`
   paging syntax, SQLite's default ASCII-case-insensitive `LIKE`, Postgres's `pg_trgm`-index-eligible
   `ILIKE`. One artifact per fixture, targeting only the dialect under test.
2. **Cross-backend parity showcases** (`006`-`023`): the same rule proved to reach an *identical
   logical outcome* on **every** backend — all five SQL dialects (Postgres/MSSQL/MySQL/SQLite, plus
   **Oracle** since #1642) + MongoDB + Search — through each backend's own native mechanism.
   18 fixture directories × 7 backend artifacts = 126 artifact files, still only 18 fixture cases.

   *Identical logical outcome, not identical text — and `019` is where that distinction stops being
   pedantic.* Oracle's `''` is NULL, so `blank`'s text arm collapses there (**SQL-7**); its arm is the
   one artifact in this category whose structure differs from its Postgres arm rather than only its
   tokens, and it differs precisely in order to reach the same rows.

`011`-`020` cover the SEM-7 `contains`-vs-tokenized-`match` trap (correct artifact + anti-pattern
documentation), the `searchMode: "analyzed"` escape hatch, and further SEM-1/2/7 mechanics not
otherwise exercised by `001`-`013`. They were authored Postgres-only under the suite's default
coverage policy and **were promoted to full parity coverage in #1639** (30 new mssql/mysql/sqlite
arms) — see **D2** below for which constructs earned arms and which deliberately did not. `021` and
`022` were **authored outright** in the same story, each closing a coverage hole the promotion
exposed: nothing in the suite reached a case-sensitive substring match, which is the single place a
dialect changes *operator* rather than *token* (SQLite's `GLOB`, **D3**); and no filter value
contained a `[`, which is a `LIKE` wildcard on SQL Server and nowhere else (**D5**).

`023` was authored the same way in **#1643's review pass**, closing the equivalent hole on the other
artifact family: every fixture in the suite compared **one** string field, so nothing could show what
a *pipeline-wide* mechanism does to a second field that declared the opposite case sensitivity. It is
the first case whose interesting arm is the MongoDB one (**MDB-9**'s `$expr` escape) while all five
SQL arms are unremarkable — the mirror image of `021`/`022`.

All cases model the same `employees` domain `sort-paging/` uses (`id` int64 stableKey, `lastName`
string, `age` int64, `salary` decimal, `department` string), plus a few fixture-local additions
(`employeeCode`, `jobTitle`, `remarks`) introduced only where a case's own mechanic needs a field
with a distinct configuration (case-sensitive opt-in, `searchMode: "analyzed"`) that would be
confusing to bolt onto a field reused elsewhere in this category.

## Cases

| # | Slug | Coverage tier | Proves | Requirement ids |
|---|---|---|---|---|
| 001 | `mysql-isnull-null-order-desc-numeric` | dialect-specific (MySQL) | `ISNULL` null-ordering emulation, `desc` sort, `decimal` field | SEM-3, SEM-6 |
| 002 | `mysql-isnull-null-order-desc-string` | dialect-specific (MySQL) | Same emulation on a `string` field (type-independence) | SEM-3, SEM-6 |
| 003 | `mssql-offset-fetch-paging` | dialect-specific (SQL Server) | `OFFSET`/`FETCH NEXT` paging syntax at a non-zero offset; incidental `CASE WHEN` null emulation | AG-3, SEM-3, SEM-6 |
| 004 | `sqlite-ascii-case-insensitive-like` | dialect-specific (SQLite) | Default ASCII-CI `LIKE`, no `COLLATE` needed (contrast with `eq`'s `COLLATE NOCASE` in `006`) | SEM-1, SEM-7 |
| 005 | `postgres-ilike-pg-trgm-contains` | dialect-specific (Postgres) | `ILIKE`, `pg_trgm`-index-eligible pattern shape | SEM-1, SEM-7, PERF-4 |
| 006 | `parity-sem1-case-insensitive-eq` | parity showcase (all 7) | `eq` case-insensitive by default, one native mechanism per backend | SEM-1, AR-6 |
| 007 | `parity-sem1-case-insensitive-contains` | parity showcase (all 7) | `contains` case-insensitive by default, SEM-7's substring mechanism per backend | SEM-1, SEM-7, AR-6 |
| 008 | `parity-sem2-neq-includes-nulls` | parity showcase (all 7) | `neq` includes NULL rows, one composition per backend | SEM-2, AR-6 |
| 009 | `parity-sem3-null-order-asc` | parity showcase (all 7) | `nullsPosition: "last"` (adapter default on `asc`), one mechanism per backend | SEM-3, SEM-6, AR-9, AG-3 |
| 010 | `parity-sem3-null-order-desc` | parity showcase (all 7) | `nullsPosition: "first"` (adapter default on `desc`), mirror of `009` | SEM-3, SEM-6, AR-9, AG-3 |
| 011 | `sem7-contains-wildcard-correct` | parity showcase (all 7) | Correct `wildcard` artifact for `contains` (positive half of the `012` pair) | SEM-7, SEM-1 |
| 012 | `sem7-contains-tokenized-match-antipattern` | parity showcase (all 7) | The forbidden tokenized-`match` form, documented in prose only; `expected/` ships only the correct `wildcard` form | SEM-7 |
| 013 | `sem7-searchmode-analyzed-optin` | parity showcase (all 7) | Explicit per-field `searchMode: "analyzed"` opt-in compiles `contains` to `match` on Search only | SEM-7, SC-3 |
| 014 | `sem1-case-sensitive-optin-eq` | parity showcase (all 7) | Per-field case-**sensitive** opt-in — and, per **D4**, that each engine reaches it differently — **four answers across five dialects**: SQL Server spells `COLLATE Latin1_General_CS_AS`, Oracle spells `COLLATE BINARY` (SQL-6/D6: its bare default is the *session*'s `NLS_COMP`), and Postgres, MySQL and SQLite spell nothing (for three different reasons) | SEM-1, SC-3 |
| 015 | `sem1-case-insensitive-ordering-gt` | parity showcase (all 7) | CI on an ordering comparison (`gt`) on a text field; ES targets `physical.search.normalizedSubField` and binds the value **exactly as typed** (no `case_insensitive` on `range`, and **no folding of the bound value** — corrected in #1644, see **OSE-6**); Postgres uses the canonical `craticula_ci` collation | SEM-1, **OSE-6**, OQ-11 |
| 016 | `sem1-case-insensitive-in-set` | parity showcase (all 7) | CI on `in` via the `craticula_ci` collation (`= ANY($1)`), never `ILIKE ANY`; ES decomposes into `bool`/`should` of `term` queries (`terms` has no `case_insensitive`) | SEM-1, AR-6 |
| 017 | `sem2-notcontains-includes-nulls` | parity showcase (all 7) | Null-inclusion on `notContains`, composed with SEM-1 CI + SEM-7 substring mechanism | SEM-2, SEM-1, SEM-7 |
| 018 | `sem2-notin-includes-nulls` | parity showcase (all 7) | Null-inclusion on `notIn`, on a case-sensitive field (orthogonal to SEM-1); reached via the `customFilters` wire extension (AG Grid's `set` filter alone cannot express `notIn`) | SEM-2, SC-3, OQ-11 |
| 019 | `sem2-blank-notblank-exact-negation` | parity showcase (all 7) | `notBlank` rendered as an explicit negation of `blank`'s own compiled form, never derived independently — **and, since #1642, the one case where the form being negated is itself per engine** (Oracle's `''` is NULL, so its text arm collapses to `IS NULL`; the uncollapsed form would make `notBlank` match no row at all) | SEM-2, **SQL-7** |
| 020 | `sem7-mongo-regex-full-metacharacter-escaping` | parity showcase (all 7) | A filter value exercising SEC-4's LIKE-wildcard set and the wider regex-metacharacter set side by side | SEM-7, SEC-4 |
| 021 | `sem1-case-sensitive-contains` | parity showcase (all 7) | Case-**sensitive** `contains` — `014`'s schema verbatim with only the operator changed. The one case where a dialect changes operator rather than token: SQLite renders `GLOB` (D3), since its `LIKE` is unconditionally ASCII-CI. Its `*` filter value is a metacharacter in two of the four escape alphabets | SEM-1, SEM-7, SC-3, SEC-4 |
| 022 | `sec4-tsql-bracket-wildcard-escaping` | parity showcase (all 7) | `[` is a **fourth** `LIKE` wildcard on SQL Server and on no other v1 dialect — `020`'s schema verbatim with only the filter value changed, so the mssql arm is the only one whose bound value is escaped (D5) | SEM-7, SEC-4 |
| 023 | `sem1-mixed-case-sensitivity-one-request` | parity showcase (all 7) | An insensitive **and** a sensitive string compared in one request — `006`'s and `014`'s schemas merged. The five SQL dialects attach `COLLATE` per operand and need nothing new; MongoDB's collation is query-level, so the sensitive comparison escapes into `{"$expr": …}` (**MDB-9**). The suite's only `$expr` artifact | SEM-1, SC-3, SQL-6, **MDB-9** |

## Notes on artifact construction

- **Parameter placeholder style** (this category's own convention, since no dialect-specific
  fixture existed before it to set one): Postgres positional `$1, $2, ...` (matching `sort-paging`'s
  own convention); SQL Server named `@p1, @p2, ...`; MySQL/SQLite positional `?`.
- **MySQL vs. SQL Server null-ordering emulation are not the same mechanism**: MySQL's single-argument
  `ISNULL(col)` is a null-test returning `1`/`0`; SQL Server's own `ISNULL(expr, replacement)` is a
  **two-argument** substitution/`COALESCE`-equivalent function, so reusing that name for a null-test
  would be a real bug, not a naming preference. SQL Server therefore emulates with
  `CASE WHEN col IS NULL THEN 1 ELSE 0 END` instead (`003`, `009`, `010`).
- **SQLite supports native `NULLS FIRST`/`NULLS LAST` since 3.30.0** — treated like Postgres in the
  parity showcases (`009`/`010`), not given a MySQL/MSSQL-style emulation.
- **MongoDB query-level `collation`** (SEM-1's mechanism for `$eq`/`$in`/ordering/sort) is a
  find()/aggregate() **command-level** option, not a pipeline stage — `mongo.artifact.json` uses the
  `{ pipeline, collation }` wrapper object added to `spec/fixtures/schemas/mongo-artifact.schema.json`
  for this category (`oneOf` alongside the original bare-array shape; every fixture not needing a
  non-default collation still uses the plain array). `contains`/`notContains` never use this wrapper
  — SEM-1 is explicit that `$regex` has no collation-compatible substring form.
- **Search's `terms` (plural) query has no `case_insensitive` parameter** — only `term`/`wildcard`/
  `prefix`/`regexp` (singular forms) do. Case-insensitive `in` (`016`) decomposes into a `bool`/
  `should` of per-value `term` queries rather than a single `terms` query.
- **Every SQL `LIKE`/`ILIKE` artifact in this category carries an explicit `ESCAPE` clause** —
  including Postgres, whose own default escape character already happens to be `\`, per SEM-7's
  dialect-independent Must-Not. The escape *character* is `\` on all five dialects (so the bound
  value, escaped by the shared SEC-4 routine, is byte-identical across the arms), but **MySQL spells
  it `ESCAPE '\\'` and the other four — Postgres, SQL Server, SQLite and Oracle — spell it
  `ESCAPE '\'`** — see D1 below. (SQLite's case-**sensitive** path is the exception to the clause
  existing at all: `GLOB` takes none, D3.)
- **Every MongoDB `$regex` artifact escapes the full regex-metacharacter class** (not just SEC-4's
  four LIKE/wildcard characters) and carries the `"i"` option — visibly exercised (not just a
  structural no-op) in `020`. `021` is the exception to the *option*, not to the escaping: a field
  declared `caseSensitivity: "sensitive"` drops `$options` entirely, because MongoDB's own default
  regex matching is already case-sensitive (unlike the SQL engines SQL-6 makes spell that direction).
- **A pipeline-wide `collation` reaches every construct in it, so a case-**sensitive** comparison
  beside a case-insensitive one is rendered as an `$expr`** (`023`, **MDB-9**). This is the one place
  where MongoDB needs a mechanism the five SQL dialects do not: `COLLATE` attaches per operand, so a
  SQL arm simply spells two different things in one `WHERE`, while MongoDB has a single query-level
  switch. Aggregation-expression comparison ignores that switch, which is what makes the escape
  correct rather than merely different. The escaped value operand is wrapped in `{"$literal": …}`,
  which is the *only* place in the suite a value is bound by something other than a parameter or a
  request slot — see MDB-9 for why an unwrapped one stops being a value at all. A case-sensitive
  *sort key* has no such escape and is a compiler error instead — hence no artifact for it, the same
  shape as SQL-7(b).
- **`019`'s MongoDB arm was wrong, and #1643 re-authored it.** It rendered `notBlank` as
  `{"$nor": [{"lastName": null}, {"lastName": ""}]}` — the negation of a `$or` form that no `blank`
  artifact in the suite ever compiles to, i.e. a *separately-derived* condition, which is exactly what
  SEM-2's third Must-NOT forbids and what this very fixture is named for. The suite's compiled `blank`
  on a `string` is `{"status": {"$in": [null, ""]}}` (`operators/029`) and its operator-level negation
  is `$nin` (`operators/030`), so the arm is now `{"lastName": {"$nin": [null, ""]}}`. Spelling it
  `$nor` also made a leaf operator indistinguishable from a one-child tree-level `notNode`, which is
  what `$nor` renders everywhere else in the suite (`filter-tree/007`-`015`) — the same conflation
  SQL-3 rules out for `notContains`.
- **`eq`/`in` (exact-match and set-membership) on a case-insensitive field use a collation, never
  `ILIKE`** (SEM-1's mechanism-note split, pinned this session): Postgres uses this project's
  canonical `craticula_ci` ICU collation (`COLLATE craticula_ci = $1` for `eq`, `COLLATE
  craticula_ci = ANY($1)` against a single array parameter for `in` — the same array-parameter
  shape `operators/035-in-string` already established) — `ILIKE`/pattern-match is reserved for
  `contains`/`notContains`/`startsWith`/`endsWith` (`007`'s territory), where the operator itself
  is a pattern match. `015`'s ordering-comparison (`gt`) case uses the same `craticula_ci` name for
  consistency, and Search's case-insensitive-ordering sub-field is declared via
  `physical.search.normalizedSubField` (not an overload of `keywordSubField`).
- **Four artifacts in this category were re-authored in #1638 to the canonical SQL renderings**
  (`spec/semantics.md`, "Addendum — canonical SQL rendering"), because this category held every one
  of the suite's rendering minorities: `008` gained the mandatory parentheses around its
  null-inclusive disjunction (`("age" <> $1 OR "age" IS NULL)`, SQL-2); `017` moved the negation
  into the operator (`"lastName" NOT ILIKE ...`, SQL-3) and gained the same wrap; `018` moved from
  `NOT IN ($1, $2)` with two scalar parameters to `<> ALL($1)` with one array parameter (SQL-4) and
  gained the wrap; `019`'s `notBlank` became the literal negation of `blank`'s own compiled text,
  `NOT (("lastName" IS NULL OR "lastName" = ''))` (SQL-2) — the form four of the five SQL dialects
  now carry, Oracle's collapsing to `NOT ("lastName" IS NULL)` under SQL-7. All 16 Postgres artifacts also gained
  identifier quoting — which matters most *here*, since this category is the one that declares
  camelCase physical columns (`lastName`, `employeeCode`, `jobTitle`) that unquoted PostgreSQL would
  fold to lowercase.
- **`008`'s `mysql`/`mssql`/`sqlite` artifacts gained the SQL-2 wrap too, and that was not optional.**
  Parenthesisation is *structural*: a query builder emits it and the `Dialect` interface has no say
  in it, so the previously bare `WHERE age <> ? OR age IS NULL` was unsatisfiable by construction on
  every dialect, not merely unusual. It stayed wrapped through #1639's re-authoring; do not "restore"
  it.
- **The quoting gap is CLOSED (#1639): all 55 `mysql`/`mssql`/`sqlite` arms are quoted.** SQL-1
  ("always quote") is universal and explicitly forbids quoting conditionally; what was deferred was
  never *whether* to quote but *which character*, which is part of writing each engine's `Dialect`.
  The characters, and their escapes, are now pinned: **`[…]` with `]`→`]]` on SQL Server**,
  **backticks with `` ` ``→```` `` ```` on MySQL** (not `"`, which only quotes identifiers under the
  non-default `ANSI_QUOTES` mode a compiler must not assume), and **`"` with `"`→`""` on SQLite**.
  `006`-`010`'s six-artifact parity sets are no longer mixed. The re-authoring was mechanical — each
  engine's quote character applied to that fixture's own `schema.json` `source` + physical columns,
  word-boundary matched, leaving `Latin1_General_CI_AS`, `NOCASE`, `ISNULL`, `CASE`/`WHEN`/`THEN`/
  `ELSE`/`END`, `ROWS`/`FETCH`/`NEXT`/`ONLY`, `CONCAT`, `@p1`, `?` and the `'%'`/`'\'`/`''` literals
  untouched.
- **Every fixture's compiled statement includes SEM-6's `stableKey` tiebreaker and its own
  null-ordering marker (`NULLS LAST`/Mongo bucket/ES `missing`), unconditionally** — every
  `agGridRequest.json` in this category carries `startRow`/`endRow`, and every `schema.json`
  declares `stableKey: "id"`, so every compiled artifact (SQL/Mongo/Search alike) ends in the `id`
  tiebreaker and its paging clause, regardless of whether the fixture's own focus is sorting at
  all — mirroring `filter-tree/001`'s complete-statement convention for the identical request
  shape.
- **Every MongoDB `$sort` stage that uses a null-bucket key is immediately followed by a `$unset`
  stage dropping every `__<field>NullBucket` key it used**, per SEM-3's mandatory bucket-cleanup
  rule — the same convention `sort-paging/` and `filter-tree/` already apply. `006`/`007`/`009`/
  `010` unset both `__<field>NullBucket` and `__idNullBucket` (two sort keys); every other Mongo
  artifact in this category unsets only `__idNullBucket` (the bare `stableKey` tiebreaker being its
  only sort key).

## #1639's decisions, recorded

> **D1**-**D3** were taken while writing the three dialects; **D4** and **D5** came out of the
> story's code review, which found that "case-sensitive" and "no case mechanism" had been treated as
> one answer, and that T-SQL has a `LIKE` wildcard the shared escape routine does not cover. All five
> are now normative in `spec/semantics.md` (**SQL-5** for D1/D3/D5, **SQL-6** for D4) — this section
> keeps the story-level rationale; the spec keeps the rule, so a language port inherits it without
> having to read a fixture README.

**D1 — MySQL renders `ESCAPE '\\'`, and that is a bug fix.** `007`'s mysql artifact previously
rendered `ESCAPE '\'`. With `NO_BACKSLASH_ESCAPES` off (MySQL's default) a backslash is the
string-literal escape character, so `'\'` escapes the closing quote: the literal never terminates and
**the statement does not parse**. Postgres (`standard_conforming_strings = on`), SQLite and SQL
Server all read `'\'` as a one-character string, which is why the same spelling is correct in their
arms and wrong only here. Oracle reads it that way too — a backslash in an Oracle string literal is an
ordinary character — so #1642 added no new answer here. The escape *character* is unchanged on every
dialect, so SEC-4's escaping and the bound value are identical across the five arms — only the SQL
text differs. This is exactly
the class of defect the artifact tier (T-1a) cannot see, since it compares strings; it was found by
reading, like SQL-1's camelCase-folding bug.

**D2 — why `011`-`020` gained per-dialect arms and other cases did not.** Those ten exercise every
construct whose SQL rendering *genuinely varies per dialect*: substring variants (`011`/`012`/`013`),
the case-**sensitive** scalar path (`014`), CI ordering (`015`), in-list (`016`), `notContains`
(`017`), `notIn` (`018`), `blank`/`notBlank` (`019`) and metacharacter escaping (`020`).

Deliberately **not** given per-dialect arms, and this is a decision rather than an omission:
tree-level `NOT`, composite `and`/`or`, `inRange`, and the unary `true`/`false` shorthand are
**builder-owned structure that cannot vary per dialect by construction** — the `Dialect` seam has no
say in any of them (`spec/semantics.md` SQL-2 says so for parenthesisation in as many words). Arms
for them would re-prove the query builder rather than the dialects, while multiplying files against
this suite's own backend-coverage policy. `019` was included precisely to make that claim *visible*
once: its four SQL arms differed in nothing but the quote character.

**That last sentence is no longer true of `019`, and #1642 is the reason** — see **D6**. Its five SQL
arms now differ in the `blank` composition itself. What survived is the stronger half of the claim:
the composition is still builder-owned and still emitted in one place; only a single yes/no engine
fact varies inside it, and no *other* construct in this list gained a per-dialect arm.

D2's reading of `019` was **narrowed by #1642**: see D6 below. The rest of D2 stands.

D2 surfaced one hole, and **`021` closes it.** Promoting `011`-`020` made it visible that *no fixture
anywhere in the suite reached a case-SENSITIVE substring match*: the only two case-sensitive fields
declared were `014`'s `employeeCode` (used with `eq`) and `018`'s `department` (used with `notIn`),
which left D3's `GLOB` path — the single place a dialect changes *operator* rather than *token*, with
a silent failure mode — resting on unit tests alone. `021` reuses `014`'s `schema.json` **verbatim**
so the operator is the only variable between the two cases, and its filter value carries a `*`, a
metacharacter in exactly two of the four *distinct* escape alphabets this category deals in (LIKE's
`\ % _`, T-SQL's `\ % _ [`, GLOB's class-wrapping `* ? [`, and Mongo's regex class — Oracle shares the
first and so added no fifth). It has an artifact
oracle now; `DialectRenderingTests` still covers the escaping's edges (`?`, `[`, an
already-class-shaped value, the deliberately-unescaped `]`/`^`) that one fixture value cannot reach.

**D3 — SQLite uses `GLOB` for a case-sensitive substring match.** SQLite's `LIKE` is ASCII-case-
insensitive *unconditionally* (it is a function call; it ignores collating sequences entirely, and
`PRAGMA case_sensitive_like` is a connection-level setting a compiler must not depend on). Emitting
`LIKE` for a field declared `caseSensitivity: "sensitive"` would therefore silently return
case-insensitive rows — the mirror image of SEM-1's "must NOT default to case-sensitive because the
engine's default is". Pinned by `021`'s sqlite arm. `GLOB` is a genuinely different pattern
language, not a synonym:

- its wildcards are `*`/`?`, not `%`/`_` — so the anchoring becomes `'*' || ? || '*'`;
- **it accepts no `ESCAPE` clause at all**, so SEC-4's prefix-with-a-character algorithm does not
  apply; a literal `*`, `?` or `[` is neutralised by wrapping it in a single-member character class
  (`[*]`, `[?]`, `[[]`). `]` and `^` are deliberately left alone — `]` is ordinary outside a class
  and every emitted class is closed immediately, and `^` only negates when it is the first character
  *inside* one;
- SQL-3 still applies: the negated form is `NOT GLOB`, never an outer `NOT (...)`.

**D4 — the case-SENSITIVE direction needs a spelled mechanism too, wherever the engine's default
disagrees.** The original promotion of `014`/`018`/`021` rendered all four SQL arms bare, on the
reading that `caseSensitivity: "sensitive"` means "apply none of `006`'s CI mechanisms". That
reading holds on Postgres (deterministic default collation) and SQLite (`BINARY`), and is **wrong on
SQL Server**, whose stock server collation is `SQL_Latin1_General_CP1_CI_AS` — case-**in**sensitive.
A bare `=`/`LIKE`/`IN`/`ORDER BY` there compares case-insensitively, so the host's explicit opt-in is
silently discarded and the SQL reads as though it were correct. That is SEM-1's second Must-Not with
the sign flipped, and the same class of silent failure **D3** pays an operator switch to prevent on
SQLite.

**Fix**: SQL Server renders `COLLATE Latin1_General_CS_AS` — the mechanical counterpart of the
`CI_AS` name already pinned, same family and accent sensitivity, only the `CI`/`CS` component
differs — on the scalar, substring, set-membership and sort-key paths alike, per SEM-1's "same
mechanism in every context on a field". `014`, `018` and `021`'s mssql arms were re-authored for it
(the complete set: those are the only three fixtures in the whole suite that declare a case-sensitive
field *and* carry a non-Postgres arm).

This could not be fixed inside the old `Dialect` seam, which is why it is a decision rather than a
patch: that seam passed case sensitivity as a **boolean**, which spells "text, compared
case-sensitively" and "not a text comparison at all" identically. Emitting `COLLATE …_CS_AS` from
the `false` branch would also have emitted it for `[age]`/`[salary]`, and SQL Server rejects that
outright ("COLLATE clause cannot be used on expressions of type int"). The seam is three-valued now
(`NotApplicable`/`Insensitive`/`Sensitive`), which is the only reason the two cases can be told
apart. Normative as **SQL-6**.

**D5 — `[` is a `LIKE` wildcard on SQL Server, and SEC-4's shared set does not cover it.** T-SQL's
`LIKE` treats `[` as a character-class opener, so an unescaped filter value `A[BC]` matches `AB` or
`AC` rather than itself, and `[^x]`/`[a-z]` widen it further — SEM-7's own "must NOT" about a
user-supplied literal still behaving as a wildcard, on the one dialect it had not been checked for.
SQL Server escapes it as `\[`, valid under the `ESCAPE '\'` clause that arm already emits; the shared
`SqlPatternEscaping` routine is **not** widened, because `\[` in a Postgres/MySQL/SQLite pattern is a
literal backslash followed by a bracket, i.e. not what the user searched for. `]`, `^` and `-` need
no escaping, by the same closed-class argument D3 writes down for GLOB: all three are special only
*inside* a class, and once the opener is neutralised no class from the value can exist. Pinned by
`022`; normative as part of **SQL-5**.

## #1642's decision, recorded

**D6 — the `blank` composition stays in the builder; the dialect supplies one engine fact.** Oracle
treats the zero-length string as NULL, so SEM-2's text arm `("c" IS NULL OR "c" = '')` does not merely
carry a redundant disjunct there: `"c" = ''` is `"c" = NULL`, i.e. **UNKNOWN for every row**, which
leaves `blank` accidentally right and makes `notBlank` — `NOT (FALSE OR UNKNOWN)` = `NOT UNKNOWN` =
UNKNOWN — match **no row at all**. Normative as **SQL-7**, with the truth table; pinned by `019`'s
oracle arm, the only artifact in this category that is not its Postgres arm with tokens substituted.

The fix is a `bool EmptyStringIsNull` on the `Dialect` seam, consulted by the query builder, rather
than a `Dialect.renderBlank` method:

- four of five dialects would otherwise carry a byte-identical `renderBlank`, the
  duplicate-instead-of-share pattern #1639's review flagged for the comparison symbols;
- every one of the five would additionally have to reach into the *semantics* layer for the
  field-type half of the answer (`SemanticsResolver.ResolveBlankCondition`) — a dependency no dialect
  has today, and one that would have to be repeated five times;
- resolving it where that field-type half is decided (Craticula's `SemanticsResolver`) was rejected
  as a layering inversion: that component knows the schema and nothing about engines.

*One argument was considered and dropped, because it does not discriminate*: "`blank` and `notBlank`
must not be able to drift apart". They cannot under either design — `notBlank` is composed as
`NOT (<blank>)` over whatever text the `blank` half produced, and a `renderBlank` member would have
fed that same composition. It reads persuasively and rules nothing out, so it is not part of the
record.

**This is the seam's third widening** (#1639's tri-state case sensitivity, #1641's `RenderRowLimit`,
this) and it is not free — every existing dialect and every language port pays for it. It is also a
**different kind** of member from the nine rendering methods: an engine *fact* the builder consumes,
not SQL text the builder splices. Recorded as such so the next contributor knows which of the two
shapes to reach for.

**D6(b) — Oracle is also the one dialect that REJECTS an input the IR allows, and that is the same
fact pointed the other way.** `stringValue.value` has no `minLength`, so `""` is a legal filter value;
on Oracle it binds as NULL, so `eq ""` returns nothing, `neq ""` returns **only the NULL rows** (wrong
rows, not no rows), `contains ""` matches nothing because the whole anchored pattern is NULL, and a
`notIn` list carrying an empty member matches no row at all. `OracleDialect` therefore throws a
`SqlCompilationException` on every path a string value can reach — scalar comparison, set membership,
substring match — rather than emit a statement that is valid SQL and silently wrong. Normative as
**SQL-7(b)**.

The obvious alternative, a `minLength: 1` on `stringValue` in `ir.schema.json`, was **considered and
deferred**: it would forbid across the whole `/v1/` contract, on every backend, an input the other
four engines handle correctly — a compatibility decision that deserves its own story rather than
riding along with a dialect. No fixture in this suite binds an empty value, so no artifact changed;
the behaviour rests on unit tests, which is the correct place for a request that does not compile.

**Oracle's other renderings are ordinary, and its version floor is not.** `"` quoting with `"`→`""`
(as Postgres/SQLite), `:pN` named binds, `||` concatenation, native `NULLS FIRST`/`NULLS LAST`,
`IN (...)` with N scalars (SQL-4), `OFFSET … ROWS FETCH NEXT … ROWS ONLY` binding the offset first
(as T-SQL), `FETCH FIRST … ROWS ONLY` for GRP-6's bare cap — legal without an `OFFSET` here, unlike
T-SQL's, which is the spelling #1641 predicted would fit `RenderRowLimit` unchanged. SEM-1 is
`COLLATE BINARY_CI` / `COLLATE BINARY`, which **pins Oracle Database 12.2+**; SQL-6 records why
`NLSSORT` was rejected despite pinning nothing, and SQL-1 records the upper-casing trap and the
identifier-length limit that 12.2 also settles.

**Two further per-dialect facts worth stating, both non-obvious and both now pinned by artifacts:**

- **SQLite attaches `COLLATE NOCASE` to the VALUE in a scalar comparison but to the COLUMN in an
  in-list** (`006` vs `016`). SQLite resolves a binary comparison's collation from the left operand's
  explicit collation, else the right's — but it resolves `x IN (y, z)` from `x`'s alone, so a
  collation on a list element is silently ignored. The asymmetry is SQLite's, not a house style.
- **MySQL emits no collation token in either direction**, because on that engine case sensitivity is
  a property of the column's declared collation (SEM-1's own worked example pins the bare
  `WHERE name = :v`). Unlike SQL Server (D4), that silence is a **deferral, not an answer**: MySQL's
  server default is a `_ci` family, so a field declared `caseSensitivity: "sensitive"` is honoured
  only by host DDL. No inline `COLLATE` is emitted because MySQL's case-sensitive collation names are
  charset-specific (`utf8mb4_bin`, `utf8mb4_0900_as_cs`, MariaDB's own set) rather than one portable
  spelling a compiler could pin, and inventing one would have put an unverifiable token into the
  oracle. See the host obligations below.

## Host obligations these artifacts imply (PERF-4's per-backend index-guidance page owns the DDL)

The compiled statement is only half the contract on **four of the five** SQL dialects — SQL Server is
the only one that owes nothing — and, since #1643 and #1644 compiled the Mongo and Search arms, on
those two as well. Recorded here because the index-guidance page does not exist yet; move them there
when it does.

| Dialect | Obligation | Consequence if unmet |
|---|---|---|
| MongoDB | Server **≥ 4.2**, for `$unset` as an aggregation stage — which SEM-3's null-bucket trio emits in *every* pipeline, since SEM-6 makes the sort tiebreaker unconditional. `{"$not": {"$regex": …}}` (MDB-4's `notContains`) additionally needs **≥ 4.0.7**, so 4.2 is the binding floor. | The server rejects the pipeline — an unrecognised stage is a loud failure, in the same class as SQLite's 3.30.0 and Oracle's 12.2 floors rather than MySQL's silent one. |
| Postgres | `CREATE COLLATION craticula_ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);` | Every case-insensitive comparison fails: the collation does not exist. |
| MySQL / MariaDB | A field declared `caseSensitivity: "sensitive"` needs a `_bin`/`_as_cs` **column** collation. | The declaration is silently ignored — the compiler's output says nothing either way, so comparisons run case-**in**sensitively under the server's `_ci` default. |
| SQL Server | **None.** Handled inline as of D4 (`COLLATE Latin1_General_CS_AS` / `Latin1_General_CI_AS`), so neither direction depends on the database's own collation. | — |
| SQLite | Library **≥ 3.30.0**, for the native `NULLS FIRST`/`NULLS LAST` every `sqlite` arm pins. | The statement is rejected outright — a loud failure, unlike MySQL's. |
| Oracle | Server **≥ 12.2** with `COMPATIBLE >= 12.2` and `MAX_STRING_SIZE = EXTENDED`, for the `COLLATE` operator every `oracle` arm's SEM-1 mechanism uses (SQL-6). | The statement is rejected outright (ORA-43929) — loud, like SQLite's. |
| Oracle | The table and every column must be **created with quoted identifiers** matching the fixture's `source`/`column` spelling exactly. Oracle folds an unquoted-`CREATE`d name to UPPERCASE, and every identifier in this suite is lowercase or camelCase. | `ORA-00942: table or view does not exist` — loud, but it names the table rather than the cause. |
| Oracle | Every **groupable** column and the `stableKey` need `COLLATE BINARY` in DDL, or the session must run `NLS_COMP = BINARY`. GRP-2's bucket-identity keys (the `GROUP BY`, the drill-down `WHERE`, a grouped level's `ORDER BY`, and BM-3's `SELECT DISTINCT`) spell **no** collation at all, and on Oracle bare means the session's `NLS_COMP`/`NLS_SORT` — the same fact that makes `COLLATE BINARY` mandatory in the *filter* direction (SQL-6/D4). | Grouped buckets fold case with **no error**: `"EMEA"` and `"emea"` land in one bucket and expanding it returns both, so child rows stop summing to the parent's aggregate. Same class as the MySQL row above, and invisible to T-1a. |
| OpenSearch / Elasticsearch | Server **≥ 7.10** (Elasticsearch) or **≥ 1.0** (OpenSearch, which forked 7.10.2 and inherited it), for the `case_insensitive` parameter SEM-1's mechanism uses on `term`/`wildcard` (OSE-14). This is the shared subset's floor and the reason one compiler serves both engines with no target flag. | The request is rejected with `unknown parameter [case_insensitive]` — loud, like SQLite's and Oracle's, not like MySQL's. |
| OpenSearch / Elasticsearch | Every field a `term`/`terms`/`wildcard` addresses needs the `keyword` sub-field its schema declares as `physical.search.keywordSubField` — a `multi_field` mapping in the index template, since the artifacts name it (`lastName.keyword`) rather than assuming a convention. | A `wildcard` against an analyzed `text` field matches per-token rather than per-value, so SEM-7's substring semantics silently become the tokenized ones SEM-7 exists to rule out; a `term` matches an analyzer token rather than the stored value. Silent, and invisible to T-1a. |
| OpenSearch / Elasticsearch | Every field declaring `physical.search.normalizedSubField` needs that sub-field mapped `keyword` **with a folding `normalizer`** (SEM-1's ICU-folding one). This is SEM-1's genuine index-time cost, and the compiler refuses to compile a case-insensitive `range`/`sort` on a field that declares no such name at all (OSE-6) — the declaration existing and the mapping not is the case it cannot see. | Case-insensitive ordering silently orders by raw bytes: `"Zebra"` before `"apple"`. Silent, and invisible to T-1a. |
| OpenSearch / Elasticsearch | A `decimal` field needs a mapping that preserves the precision the IR carries (`scaled_float` with a sufficient `scaling_factor`, or `keyword` for exact-match-only use). OSE-8 writes the IR's exact decimal token as a JSON number; a `double`/`float` mapping is what loses it. | Silent rounding at index and query time — the same class of defect AR-7's `decimal`/`double` split exists to prevent, moved one layer down. |

## Known divergences deferred to the execution-equality tier (T-1b, Feature #1624)

T-1a compares strings, so a difference that only appears when the statement *runs* is invisible to
this suite by construction. Recorded rather than fixed:

- **A zero-width page window is an error on SQL Server and an empty page on Postgres, MySQL and
  SQLite** (Oracle unmeasured — see below).
  `{"startRow": 100, "endRow": 100}` is a structurally valid `RangePage` — `PageSpecValidator` rejects
  only `endRow < startRow` — and compiles to `LIMIT 0 OFFSET 100` on Postgres/MySQL/SQLite, which
  returns zero rows, but to `OFFSET @p1 ROWS FETCH NEXT @p2 ROWS ONLY` with a limit of `0` on SQL
  Server, where `FETCH NEXT 0 ROWS ONLY` raises **msg 10744** ("The number of rows provided for a
  FETCH clause must be greater than zero"). Same request, empty result on three engines and a server
  error on the fourth. **Oracle's behaviour here is not stated, deliberately**: its row-limiting
  clause is spelled like T-SQL's but is not T-SQL's, nothing in this repo has ever executed against an
  Oracle instance, and guessing which side of the divergence it falls on would put an unverified claim
  into a register whose whole point is recording what T-1a cannot grade. **Owner: Feature #1624
  (T-1b).** Deliberately not fixed here: whether the
  right answer is a validator rejection, a builder short-circuit to a no-row query, or a per-dialect
  rendering is a behavioural decision that needs the execution tier to grade it, and any of the three
  would change artifacts this tier cannot evaluate. No fixture exercises it (every fixture pages
  `0..100`).
