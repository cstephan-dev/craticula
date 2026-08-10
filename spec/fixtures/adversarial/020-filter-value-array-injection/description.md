# 020 — filter value sent as an array where a single scalar is expected

## What makes this adversarial
The text filter's `filter` value (a single string per `iTextFilter.ts`) is sent as the array
`["a", "b", "c"]` — an attempt to smuggle multiple values into a slot the wire format and the
IR's `binaryComparisonNode` both define as exactly one `typedValue`. This is the array
counterpart to case 018's object-smuggling attempt: same structural-injection class, different
shape mismatch.

## Which validation stage must catch it
**Request parsing.** The value shape (array) never matches the single-scalar shape a text
`equals` condition requires, so this is rejected at parse time — before allowlist/limits/
coercion, all of which assume a value shape has already been validated as the correct arity for
the operator (contrast with `in`/`notIn`, whose `values` array is the *correct* shape for a
set operator — this case sends an array to a binary operator, not a set one).

## Requirement ids
SEC-2, SEC-6 (injection attempts), AR-6 (binary vs. set operator value-arity distinction),
`spec/ir.schema.json`'s `binaryComparisonNode` vs. `setComparisonNode`.
