# 002 — unknown colId in `sortModel`

## What makes this adversarial
`sortModel` requests a sort on `"internalRank"`, a colId absent from `schema.json`'s
`fields`. Sorting on an unlisted field would let a client discover/exercise a physical column
the host never intended to expose, even without filtering on it.

## Which validation stage must catch it
**Allowlist.** Every colId reference — filter, sort, group, or aggregation — is checked against
the same allowlist before anything else runs (SC-1). This case exists specifically to prove the
allowlist check applies to `sortModel`, not only `filterModel`.

## Requirement ids
SC-1, SEC-1, AR-9 (sort spec) gated by the same allowlist as AR-5/AR-6.
