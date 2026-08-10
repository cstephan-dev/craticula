# 021 — duplicate JSON keys inside a filter condition object

## What makes this adversarial
The raw wire payload (see `agGridRequest.json` — hand-authored as literal text, not emitted by
`JSON.stringify`, specifically so the duplicate survives) declares the `filter` key **twice**
inside the same `name` condition object, with two different values: once `"a"`, once an
injection-shaped string. RFC 8259 does not define duplicate-key behavior; different JSON
libraries resolve it inconsistently (first-wins, last-wins, merge, or outright error) — SEC-6
calls out deserializer fuzzing precisely because this class of ambiguity is a known source of
"the validator saw one value, the compiler saw another" bugs (a request-smuggling-style
inconsistency, not merely a cosmetic parse quirk).

## Which validation stage must catch it
**Request parsing.** A strict/canonical deserializer configured to reject duplicate keys (rather
than silently pick one) catches this before the ambiguous condition object is ever handed to
allowlist/limits/coercion — the whole point being that no later stage should have to guess which
of the two `filter` values is "the real one".

## Requirement ids
SEC-6 ("deserializer fuzzing in CI for at least the reference implementation" — this fixture is a
static representative of that class), SEC-2.
