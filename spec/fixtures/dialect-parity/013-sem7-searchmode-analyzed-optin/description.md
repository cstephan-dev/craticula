# 013 — SEM-7's documented escape hatch: per-field `searchMode: "analyzed"`

**Proves:** SEM-7's explicit opt-in mechanism: a field's `text.searchMode` set to `"analyzed"` in
`schema.json` makes `contains` compile to the tokenized `match` form on Search **deliberately** —
the exact query shape `012` names as the forbidden silent default, here chosen explicitly and
documented, which is precisely what SEM-7's Must-Not permits ("must NOT let `searchMode: analyzed`
become a silent or implicit **default**" — its presence as an explicit per-field configuration is
the rule's own escape hatch, not a violation of it).

`jobTitle` is configured `text.searchMode: "analyzed"`; filtering `contains: "engineer"` compiles to:

```json
{ "match": { "jobTitle": { "query": "engineer", "operator": "and" } } }
```

Postgres and MongoDB are **unaffected** by this field's `searchMode` — SEM-7 is titled "`contains`
on ES/OpenSearch" and its `analyzed` opt-in is scoped to that one backend family; this spec defines
no analogous "tokenized" alternative substring mechanism for SQL/Mongo, so both continue to compile
the ordinary case-insensitive `LIKE`/`$regex` substring form regardless of this field's
`searchMode` — visibly demonstrated by the two artifacts below being unremarkable relative to
`007`/`011`.

This fixture's `agGridRequest.json` carries no `sortModel`, so the only sort key in every compiled
artifact is SEM-6's `stableKey` tiebreaker (`id`, `asc`, `nullsPosition: "last"`), appended
unconditionally per SEM-6.

**Coverage tier:** all six backends — all four SQL dialects + MongoDB + Search. Promoted from
Postgres-only in #1639 (D2). The SQL arms are deliberately identical in shape to `011`'s: the
`searchMode: "analyzed"` opt-in is a *Search-only* divergence, and four SQL artifacts that ignore it
in unison is the clearest available statement of that.

**Requirement ids:** SEM-7, SC-3 (per-field text configuration), SEM-6 (tiebreaker, incidental).
