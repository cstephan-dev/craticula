# `spec/` — the language-neutral source of truth

Per **AR-1** of the [Technical Requirements Document](../docs/TRD-grid-query-compiler.md), this
project is specification-first: `spec/` is not tied to any single language's type system, and
every language implementation (C#, TypeScript, Python, Go — see AR-2, ML-1/ML-2) conforms to what
lives here. The spec never conforms to an implementation. If a language port needs a shape the
spec doesn't have, the spec changes first, then the port follows.

## What's here today

- **`ir.schema.json`** — the canonical IR (intermediate representation) document, expressed as a
  [JSON Schema, draft 2020-12](https://json-schema.org/draft/2020-12/schema). It defines the
  vendor-neutral shape every frontend adapter produces and every backend compiler consumes
  (AR-2 layer 2): a recursive filter tree (`And`/`Or`/`Not`/`Comparison`, AR-5), the closed
  17-operator set (AR-6), fully-typed values (AR-7), group spec, aggregation spec (AR-8), sort
  spec (AR-9), and a page spec covering both bounded ranges and the unpaginated `none` mode used
  for full exports (EXP-2). The schema carries no `maxItems`/`maximum` bounds tied to SEC-3's
  resource limits (max depth, max leaves, max in-values, max page size) — those are host-configurable,
  so a fixed structural bound would reject a legitimately higher-configured host's IR. All of SEC-3
  is enforced by a runtime validator built in a later work item, not by this schema. The one
  exception is a handful of amplification caps unrelated to SEC-3 (string/identifier `maxLength`,
  `int64` numeric bounds) that exist purely to keep the *shape* well-formed, not to police policy.

## What's coming (separate, later work items — not covered here)

- **`semantics.md`** — the normative semantics document (§7 of the TRD: case sensitivity, null
  handling and ordering, date/zone resolution, type coercion, deterministic paging, `contains` on
  search backends). Each rule there gets conformance fixtures.
- **`fixtures/`** — the shared, language-agnostic conformance fixture suite (§11, T-1): grid
  request + schema config + expected per-backend artifact (and, for a subset, expected rows
  against seeded data). This is what makes identical semantics across backends *and* across the
  four language implementations (G3, G4) enforceable rather than aspirational.

Both are intentionally out of scope for this change; see the TRD for the full requirements they
must satisfy.
