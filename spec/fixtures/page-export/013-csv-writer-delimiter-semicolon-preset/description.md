# CSV writer — first-class semicolon delimiter preset

**Proves:** EXP-4's `;` delimiter preset ("Excel in French and most European locales expects
`;` and misparses `,`"), as a documented expectation.

## Scenario

Same request/artifact shape as fixture 012 (a single representative backend, Postgres — this is a
writer-layer concern, not a compiler-layer one); the difference under test is entirely in the
writer's delimiter configuration, which is not part of the AG Grid wire contract (EXP-1 does not
expose a delimiter field on the request) and is therefore not represented in `agGridRequest.json`
— it is host/writer configuration, documented here in prose.

## Documented writer expectation (EXP-4)

- **Delimiter:** `;` (semicolon), via the writer's first-class French/European-locale preset —
  an explicit configuration choice, not something inferred from the request or schema.
- Everything else (header row on, CRLF line endings, RFC 4180 quoting) is unchanged from fixture
  012 — only the delimiter character changes, including which characters trigger quoting (a
  value containing `;` must now be quoted; a value containing a plain `,` no longer needs to be,
  since `,` is no longer the delimiter). See EXP-5 for the related decimal-separator collision
  the writer must guard against when `;` is active (a `,`-decimal-separator locale colliding
  with a `;`-delimiter locale is fine; a `,`-decimal-separator field value combined with a
  `,`-delimiter would not be, which is exactly the collision EXP-5 requires the writer to
  detect and warn/fail on).
