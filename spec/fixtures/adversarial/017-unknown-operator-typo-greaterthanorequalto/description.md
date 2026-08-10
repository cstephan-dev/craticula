# 017 — `"greaterThanOrEqualTo"`, a typo'd variant outside the closed set

## What makes this adversarial
The `quantity` number filter's `type` is sent as `"greaterThanOrEqualTo"` — plausible at a
glance, but the actual closed-set member (AR-6) is `"gte"` at the IR level (AG Grid's own wire
vocabulary for this op is `"greaterThanOrEqual"`, still not this spelling). This represents the
class of "near-miss operator name" mistakes/attacks that a naive string-based dispatch (e.g. a
big `switch` with a wildcard default) might mishandle by falling through to an unintended
branch instead of rejecting.

## Which validation stage must catch it
**Request parsing**, same stage as 016 — an operator string that never resolves to a closed-set
member must be rejected before any IR node is built, regardless of how close it looks to a valid
one.

## Requirement ids
AR-6 (closed 17-operator set — no fuzzy/near matching), SEC-6.
