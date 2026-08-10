# 019 — a `filterModel` key containing injection-shaped characters, still just an unknown colId

## What makes this adversarial
The `filterModel` key is literally `"amount'); DROP TABLE users; --"` — a string containing
embedded quotes and statement-terminator/comment syntax, chosen to look like a SQL-injection
attempt. But no spec document (`spec/ir.schema.json`, `docs/architecture/c4-components-core-ir.md`)
defines a separate "colId shape" check ahead of the allowlist lookup — `fieldName` is a plain
bounded string at the IR layer, and the allowlist check (SC-1) is a membership test against
`GridSchema.fields`, not a character-class validator. This string is simply absent from the
allowlist, exactly like `secretNotes` in case 001; its suspicious shape doesn't change which
validation stage catches it or why. This is what makes the case adversarial: an attacker cannot
gain anything by shaping the colId this way, because it never gets close enough to being treated
as an identifier/column name for the shape to matter — the allowlist rejects it before any
SEC-2 concern (building query text from client strings) is even reachable.

## Which validation stage must catch it
**Allowlist** (same stage and reason as 001-003). In `Strict` mode (this schema's
`mode: "strict"`), the colId `"amount'); DROP TABLE users; --"` is not a key of
`GridSchema.fields`, so it is rejected at the allowlist lookup — the same first stage in the
pinned order (allowlist -> limits -> coercion -> static injection) that catches every unlisted
colId, regardless of whether the string happens to look like an injection payload.

## Requirement ids
SC-1, SEC-1, SEC-2 (never build query text/identifiers from unvalidated client strings — moot
here since the string never reaches that stage), SEC-6 (injection attempts).
