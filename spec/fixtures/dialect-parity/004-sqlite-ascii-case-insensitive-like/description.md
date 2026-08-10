# 004 — SQLite's default ASCII-case-insensitive `LIKE`

**Proves:** SEM-1's SQLite-specific default mechanism for `contains`: unlike PostgreSQL (needs
`ILIKE`) or SQL Server/MySQL (need an explicit/columnar CI collation, see `006`/`007`), SQLite's
`LIKE` operator is **already** case-insensitive **for ASCII characters** by default — no
`COLLATE NOCASE` and no function wrapper is needed for the pattern below to match `"Smith"`,
`"SMITH"`, or `"smith"` alike. This is an ASCII-only guarantee: SQLite's built-in case-folding table
does not cover non-ASCII characters, unlike a real Unicode-aware collation — a limitation worth
flagging in PERF-4's per-dialect index-guidance docs for hosts with non-ASCII text data, not
something this fixture can encode structurally.

Contrast with SQLite's `=` operator (case-insensitive `eq`, exercised in the parity-showcase case
`006`): `=` **does** need an explicit `COLLATE NOCASE`, because SQLite's default `TEXT` affinity
comparison for `=` is `BINARY`. `LIKE` and `=` do not share a default collation in SQLite — this
fixture and `006` are a deliberate before/after pair proving that distinction.

The mandatory `ESCAPE` clause (SEM-7) is still required regardless of this ASCII-CI default, since
SQLite has no default `LIKE` escape character either (SEM-7's Must-Not: never emit `LIKE` without a
matching `ESCAPE` clause, dialect-independent).

**Dialect-specific mechanic:** SQLite only, per this suite's backend-coverage policy.

**Requirement ids:** SEM-1, SEM-7.
