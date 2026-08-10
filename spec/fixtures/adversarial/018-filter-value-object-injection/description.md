# 018 — filter value sent as a nested object where a scalar is expected

## What makes this adversarial
This is the **structural** injection class SEC-6 targets: the text filter's `filter` value
(normally a plain string per `iTextFilter.ts`) is sent as the JSON object `{"$ne": null}` —
an attempt to smuggle a raw Mongo-style operator through the value slot, hoping a downstream
layer treats the "value" as already-trusted structure instead of a leaf scalar. This is
**not** the "SQL metacharacters inside a normal string" case (that is safely handled by
parameterization and does not belong here) — the adversarial part is the *shape* (object instead
of string), not any character content.

## Which validation stage must catch it
**Request parsing.** `spec/ir.schema.json`'s `binaryComparisonNode.value` is a `typedValue`
tagged object (`{type: "string", value: <string>}`), never a raw client-supplied object
substituted directly — the adapter must reject a filter payload whose value shape doesn't match
the expected scalar before it ever considers building a comparison node, let alone touching
allowlist/coercion.

## Requirement ids
SEC-2 (values are parameters/JSON values, never trusted structure), SEC-6 (injection attempts),
AR-7 (typedValue as the only value shape the IR accepts).
