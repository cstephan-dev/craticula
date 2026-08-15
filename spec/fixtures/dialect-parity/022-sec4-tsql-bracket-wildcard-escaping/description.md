# 022 — SEC-4/SEM-7: `[` is a **fourth** `LIKE` wildcard, on SQL Server only

**Proves:** SEM-7's Must-Not — a user-supplied literal must not still behave as a wildcard — on the
one v1 dialect where SEC-4's shared escape set (`\`, `%`, `_`) is *not the whole set*. T-SQL's
`LIKE` additionally treats `[` as a character-class opener, so an unescaped filter value `A[BC]`
matches rows containing `AB` or `AC` rather than the literal string `A[BC]`, and `[^x]` / `[a-z]`
widen the match set further. Postgres, MySQL and SQLite have no character class in `LIKE` at all, so
the same value is bound completely literally on those three.

This fixture's `schema.json` is **byte-identical to `020`'s**, deliberately — same free-text
`remarks` field, same `contains` operator, same request shape — so the only variable between the two
cases is the filter value, and any difference between their artifacts is attributable to the escape
alphabets and nothing else. `020` exercises SEC-4's set against the *regex* set; this one exercises
the fourth character only one SQL dialect has.

Filter value: `"A[BC]"` on `remarks`, `contains`. It carries both halves of the rule at once:

- `[` is escaped **on SQL Server only**, as `\[`, under the `ESCAPE '\'` clause that arm already
  emits. The bound value therefore differs across the five SQL arms of one fixture — the same shape
  `021` produces for SQLite's GLOB, and the reason this belongs in `dialect-parity/` rather than in
  `operators/`.
- `]` is **not** escaped, on any dialect. It is special only *inside* a character class, and after
  the `[` is escaped no class from the value can be opened — the same closed-class argument the
  category README's **D3** writes down for GLOB's `]`/`^`. `-` and `^` are left alone for the
  identical reason (see `DialectRenderingTests`, which covers `[^x]` and `[a-z]` directly; one
  fixture value cannot reach every combination).

Per backend:

- **Postgres**: `ILIKE` with the mandatory `ESCAPE '\'`, param `A[BC]` **unescaped** — `[` is an
  ordinary character to PostgreSQL's `LIKE`/`ILIKE`, which has no character-class syntax.
- **SQL Server**: `LIKE` with the `+` concat operator, the CI collation (`remarks` is not opted into
  case sensitivity), and param `A\[BC]` — the one arm whose bound value differs. Escaping happens on
  top of SEC-4's shared routine, not by replacing it: the escape character is doubled first, so the
  `\` introduced here is never itself escaped.
- **MySQL**: `LIKE` wrapped in `CONCAT`, `ESCAPE '\\'` (category README's **D1**), param `A[BC]`
  unescaped — MySQL's `LIKE` has no character class either.
- **SQLite**: `LIKE` with `||` concatenation, param `A[BC]` unescaped. Note the contrast with `021`:
  `[` *is* a metacharacter to SQLite's **GLOB**, and is neutralised there as `[[]` — but this field
  is case-insensitive, so this fixture takes the `LIKE` path, where it is not.
- **MongoDB**: `$regex` with the `"i"` option and the full metacharacter escape `020` pins (every
  character outside `[A-Za-z0-9]`), so **both** brackets are escaped: `A\[BC\]`. `[`/`]` open and
  close a regex character class, so unlike the SQL arms the closing bracket genuinely needs it here
  — the clearest demonstration in the suite that the two escape alphabets are not one another's
  subset in either direction.
- **Search**: `wildcard` against the `.keyword` sub-field with `case_insensitive: true`, value
  `*A[BC]*` — ES/OpenSearch's `wildcard` query recognises only `*` and `?` as wildcard syntax
  (`020`, `021`), so neither bracket is escaped and both anchoring `*` remain the query's own.

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6.

**Coverage tier:** all seven backends — all five SQL dialects + MongoDB + Search. Authored during
#1639's review pass, when the `[` gap was found in `MssqlDialect`: the escaping fix without a fixture
would have left T-SQL's fourth wildcard with unit tests alone, which is exactly the state `021` was
authored to fix for GLOB.

**Requirement ids:** SEM-7, SEC-4, SEM-1 (CI mechanism, incidental), SEM-6 (tiebreaker, incidental).
