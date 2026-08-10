# 005 — PostgreSQL `ILIKE`, `pg_trgm`-index-eligible `contains`

**Proves:** SEM-1's pinned Postgres mechanism for case-insensitive `contains`: `ILIKE` rather than
wrapping the column in `lower()` (which would make the predicate non-sargable, defeating ordinary
indexing — SEM-1's rationale and first Must-Not). Specifically, this predicate's shape
(`ILIKE '%' || :pattern || '%'`, i.e. a leading-**and**-trailing wildcard) is the shape that is
**`pg_trgm`-index-eligible**: a `GIN` index on `lastName gin_trgm_ops` (via the `pg_trgm` extension)
can serve this pattern, which a plain B-tree index cannot (PERF-4 index guidance). The index itself
is DDL, not part of the compiled artifact — this fixture pins the compiled SQL shape only; the
`pg_trgm` eligibility is documented here for context on *why* this exact shape matters, not asserted
structurally.

The mandatory `ESCAPE` clause (SEM-7) is included even though Postgres's own `LIKE`/`ILIKE` default
escape character is already `\` (unlike SQL Server/SQLite, which have none) — SEM-7's Must-Not is
dialect-independent ("must NOT emit a SQL LIKE pattern without a matching ESCAPE clause"), so the
compiler emits it explicitly on every dialect rather than relying on a default that a session-level
setting could override.

**Dialect-specific mechanic:** PostgreSQL only, per this suite's backend-coverage policy.

**Requirement ids:** SEM-1, SEM-7, PERF-4 (index guidance, contextual only).
