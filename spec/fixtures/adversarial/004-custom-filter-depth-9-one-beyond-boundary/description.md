# 004 — filter tree depth 9 (one level beyond the default max of 8)

## What makes this adversarial
AG Grid's own `filterModel` is structurally shallow (a flat per-column map, at most two
conditions per column via the combined form) — it cannot express a depth bomb on its own. This
case uses the `customFilters` wire extension (OQ-11, see `spec/fixtures/README.md`'s
"customFilters wire-shape note") to inject a filter fragment nesting `not` **8** levels deep
around a single leaf comparison. Counting the root node as depth 1 and each nested node (leaf
included) as +1, this tree has depth **9**: 8 `not` nodes + 1 comparison leaf.

SEC-3's default max filter tree depth is **8** (`docs/TRD-grid-query-compiler.md` §9). A
depth-8 tree (see `filter-tree/`'s own boundary fixture) must compile successfully; this case
is deliberately exactly one level past that boundary, to pin the off-by-one behavior precisely
rather than only proving "very deep trees fail".

## Which validation stage must catch it
**Resource limits**, the second stage in the pinned order (allowlist -> limits -> coercion ->
static injection). Every field referenced here (`amount`) *is* allowlisted, so this case must
fall through allowlist cleanly and be caught by the depth counter — proving the two stages are
independent checks, not one catching the other's failures by accident.

## Requirement ids
SEC-3 (max filter tree depth = 8, default), AR-5 (filter tree), OQ-11 (customFilters merge),
`docs/architecture/c4-components-core-ir.md`'s Resource Limits Validator + Custom Filter Merge
components. Runtime validator must count depth during descent per `spec/ir.schema.json`'s
root-level description (never only after full structural parse, so this can't be used as a
stack-exhaustion vector either).
