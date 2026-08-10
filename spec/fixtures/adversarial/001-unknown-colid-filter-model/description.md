# 001 — unknown colId in `filterModel`

## What makes this adversarial
The request's `filterModel` keys a condition on `"secretNotes"` — a colId that does not
appear in the accompanying `schema.json`'s `fields` allowlist. The colId itself is a
syntactically ordinary identifier (no injection attempt in its shape); the adversarial part is
simply that it was never allowlisted by the host (SC-1).

## Which validation stage must catch it
**Allowlist** (first stage in the pinned order — allowlist -> limits -> coercion -> static
injection, per `docs/architecture/c4-components-core-ir.md`). In `Strict` mode (this schema's
`mode: "strict"`, and the TRD §4 `[DEFAULT]`), an unlisted colId throws rather than being
silently stripped (that stripping behavior is reserved for `Lenient` mode, not exercised here).
Because allowlist runs first, this request must never reach type coercion or resource-limit
counting — there is nothing further to validate once the field itself is rejected.

## Requirement ids
SC-1, SEC-1 (TRD §4, §9); `docs/architecture/c4-components-core-ir.md`'s
Allowlist Validator component.
