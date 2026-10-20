# 028 — unknown colId in `sortModel`, after the stable key

## What makes this adversarial
`sortModel` is `[id asc, internalRank asc]`: the first key is the schema's stable key, the second a colId absent from `schema.json`'s `fields`. SEM-6's reduction (Bug #2565) drops every key after an exact stable key, because none of them can change the order — and a key that is dropped is never rendered, so a compiler that reduced **before** it looked the field up would silently accept the unknown colId. This case exists to prove that it does not: an undeclared colId is rejected wherever it sits in the sort, exactly as in `002`.

## Which validation stage must catch it
**Allowlist** (SC-1): the reduction leaves a sort alone when any key in it is unknown, so the compiler's own field check still sees it. The same holds on a grouped level for a key after the group column that the level does not produce (GRP-4).

Requirement ids: SC-1, SEC-1, SEC-6, AR-9, SEM-6.
