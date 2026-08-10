# 005 — filter tree depth 20 (deep bomb, well beyond the boundary)

## What makes this adversarial
Same construction as 004 (`customFilters` with a `not`-chain), but deliberately far past the
boundary — 19 nested `not` nodes wrapping one leaf comparison, depth **20** against SEC-3's
default max of 8. Where 004 pins the precise off-by-one, this case represents the "depth bomb"
framing SEC-6 names explicitly: a request engineered to force expensive/unbounded recursive
validation or compilation work.

## Which validation stage must catch it
**Resource limits**, same stage as 004. This case also exercises the ordering guarantee from
`spec/ir.schema.json`'s root description: the depth check must run *during* descent, not only
after a full structural parse of the (here, deliberately large) tree — so this fixture doubles as
a "does the validator bail out early" check, not just a "does it eventually reject" check.

## Requirement ids
SEC-3 (max filter tree depth = 8), SEC-6 (depth bombs must produce clean rejections), AR-5,
OQ-11.
