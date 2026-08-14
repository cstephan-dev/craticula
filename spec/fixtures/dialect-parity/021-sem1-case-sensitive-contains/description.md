# 021 — Cross-backend parity showcase: SEM-1 case-**sensitive** `contains`

**Proves:** the case-sensitive half of SEM-1 applied to SEM-7's *substring* family — `014`'s
per-field `caseSensitivity: "sensitive"` opt-in, carried onto `contains` instead of `eq`. This
fixture's `schema.json` is **byte-identical to `014`'s**, deliberately: the only variable between the
two cases is the operator, so any difference between their artifacts is attributable to the
substring mechanism and to nothing else.

It exists because that combination turns out not to be a token swap on one backend — and, as #1639's
review pass established, not the *same* token swap on the other three either. **Only Postgres's
`LIKE` is case-sensitive on its own**, so only its arm is `007`'s minus the CI mechanism and nothing
else. SQL Server's `LIKE` folds case under the stock server collation, so its arm swaps
`Latin1_General_CI_AS` for `Latin1_General_CS_AS` rather than dropping the clause (see `014` and the
category README's **D4**). MySQL's follows the column's declared collation and is a host DDL
obligation the statement cannot express either way. **SQLite has no case-sensitive `LIKE` at all**:
it is ASCII-case-insensitive *unconditionally*
(`004` pins that as a feature), it is implemented as a function call and so ignores collating
sequences entirely, and `PRAGMA case_sensitive_like` is a connection-level setting a compiler must
not depend on. Emitting `LIKE` here anyway would silently return case-insensitive rows for a field
the host explicitly declared sensitive — the mirror image of SEM-1's second Must-Not, and invisible
to a reader of the SQL. SQLite therefore switches **operator**, to `GLOB`.

Filter value: `"AB*10"` on the case-sensitive `employeeCode` field, `contains`. The `*` is chosen
because it is a metacharacter in exactly two of the four escape alphabets this suite deals in, so the
same input produces four visibly different bound/embedded values (the same technique `020` uses for
`%` and `(`/`)`):

- `*` is **not** a SQL `LIKE` wildcard and is **not** in SEC-4's set (`\`, `%`, `_`) — so the three
  `LIKE` dialects bind it completely literally.
- `*` **is** GLOB's "any sequence" wildcard — so SQLite must neutralise it, and cannot do so the way
  every other artifact in this suite does (below).
- `*` **is** a regex metacharacter, so Mongo's full-metacharacter escape (`020`: every character
  outside `[A-Za-z0-9]`) backslash-escapes it.
- `*` **is** one of the two wildcard-syntax characters ES/OpenSearch's `wildcard` query recognises
  (`*` and `?`, per `020`), so the Search pattern backslash-escapes it too — while still using `*` as
  its own *anchoring* wildcard on both ends, which is what makes the escaped and unescaped uses
  visible side by side in one value.

Per backend:

- **Postgres**: plain `LIKE`, not `ILIKE` — the `005`/`007` pattern shape with the CI mechanism
  removed, and the mandatory `ESCAPE` clause retained (SEM-7 requires naming the escape character
  whether or not the value needs it). Param `AB*10`, unescaped: SEC-4 covers `\`, `%` and `_` only.
- **SQL Server**: `LIKE` with the `+` concat operator and an inline
  `COLLATE Latin1_General_CS_AS` — `007`'s arm with the `CI_AS` collation *swapped*, not removed.
  Dropping it (which this arm did until #1639's review pass) would leave the pattern match running
  under the server's stock `SQL_Latin1_General_CP1_CI_AS`, i.e. case-**in**sensitively, which is the
  same silent wrong answer SQLite's `LIKE` would give. T-SQL has no `ILIKE`, so both directions of
  SEM-1 reach the substring family through the collation here; only the `CI`/`CS` component differs.
- **MySQL**: `LIKE` wrapped in `CONCAT`, `ESCAPE '\\'` (see the category README's **D1** — `'\'`
  would not terminate as a string literal with `NO_BACKSLASH_ESCAPES` off), and again no `COLLATE`
  clause. **Read `014`'s note before assuming this arm is case-sensitive at execution time**: MySQL
  resolves case sensitivity from the *column's declared collation*, whose server default is a `_ci`
  family, so honouring this schema requires a `_bin`/`_as_cs` column (host DDL, PERF-4) — the
  statement itself says nothing either way. This artifact pins the SQL text; the execution-equality
  tier (T-1b, #1624) is what will pin the behaviour.
- **SQLite**: `GLOB`, and it is a genuinely different pattern language rather than a synonym for a
  case-sensitive `LIKE`:
  - its wildcards are `*`/`?`, not `%`/`_`, so the anchoring becomes `'*' || ? || '*'`;
  - **`GLOB` accepts no `ESCAPE` clause in SQLite at all**, so SEC-4's prefix-with-a-character
    algorithm is not merely spelled differently here — it has nothing to attach to. The only way to
    neutralise a metacharacter is to wrap it in a single-member character class, so the bound value
    is `AB[*]10`. (`]` and `^` need no escaping in turn: `]` is an ordinary character outside a
    class, every class emitted here is closed immediately so no `]` from the value can close one,
    and `^` only negates when it is the first character *inside* a class.)
  - SQL-3 still applies to the negated form: `notContains` would render `NOT GLOB`, never an outer
    `NOT (...)`.
- **MongoDB**: `$regex` **without** the `"i"` option — the same `contains`-compiles-to-`$regex`
  mechanism as `007`/`011`, with SEM-1's case-insensitivity dropped. No query-level `collation`
  wrapper either, for the same reason `014` has none *and* the reason `007` has none: a
  case-sensitive comparison needs no collation, and `$regex` has no collation-compatible form
  regardless.
- **Search**: a `wildcard` query against the `.keyword` sub-field with the `case_insensitive`
  parameter **omitted entirely**, exactly as `014`'s `term` query omits it — not set to `false`, so
  the artifact reads as "no CI mechanism applied" rather than "CI explicitly disabled".

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6.

**Coverage tier:** all six backends — all four SQL dialects + MongoDB + Search. Authored in #1639 to
close the gap that story found: before it, **no fixture anywhere in the suite reached a
case-sensitive substring match** (the only two case-sensitive fields declared were `014`'s
`employeeCode` under `eq` and `018`'s `department` under `notIn`), which left SQLite's `GLOB` path —
the one place a dialect changes *operator* rather than *token* — with no artifact oracle at all.

**Requirement ids:** SEM-1, SEM-7, SC-3, SEC-4 (contrast across the four escape alphabets), SEM-6
(tiebreaker, incidental).
