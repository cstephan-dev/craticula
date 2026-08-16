Proves AR-6's `notBlank` operator on a `string` field: SEM-2 requires `notBlank` to be `blank`'s exact Boolean negation, never independently re-derived (e.g. never hand-written as `IS NOT NULL AND <> ''` separately) — this fixture's artifacts negate the compiled `blank` condition directly.

*Search-arm correction (#1644):* the same empty-string `term` long-form fix as `029`'s (**OSE-1**).
The `bool.must_not` around the compiled `blank` was already the single-clause OBJECT form **OSE-4**
requires for a compiler-composed negation — `dialect-parity/019` was the one artifact that spelled it
as an array, which is reserved for a tree-level `notNode`.
