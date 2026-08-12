# `spec/fixtures/filter-tree/` — AR-5 And/Or/Not composition

Proves AR-5's filter tree composition (`And(nodes[])`, `Or(nodes[])`, `Not(node)`, `Comparison(field, op, value|values)`) and the depth variation SEC-3 places a configurable limit on (default max depth 8). See `spec/fixtures/README.md` for the general fixture shape and backend coverage policy; every fixture here uses the default policy (Postgres + MongoDB + one shared Search artifact).

## Why `customFilters` is load-bearing here

AG Grid's own wire `filterModel` is structurally shallow: a flat per-column map (current `conditions[]` array, legacy `condition1`/`condition2`, or the `multi` filter type's stack), always combined with AND or OR — and it has **no negation concept at all**. The per-column condition count is *not* fixed at two: it is bounded by `ISimpleFilterParams.maxNumConditions`, a documented grid option that merely **defaults** to 2 (only the legacy `condition1`/`condition2` pair is structurally limited to two, and it is deprecated). `ag-grid-majors/001` is a legitimate three-condition column on exactly that basis. What raising it cannot do is add a level: the tree is `And(columns...)` over `And`/`Or(conditions...)` and stops there, however many conditions each column holds.

So `customFilters` is load-bearing here for **negation and depth**, which the wire format genuinely cannot express. It is *not* the only route to a large **leaf count** — see the known gap recorded in `spec/fixtures/README.md`'s `customFilters` bullet. Cases proving `Not`, and every depth-variation case below, therefore inject a `{type:"not"|"and"|"or", ...}` IR fragment via the `customFilters` extension (OQ-11) rather than contorting a `filterModel` into looking deep — see `spec/fixtures/README.md`'s wire-shape reference for why this is the *only* way to reach real tree depth from AG Grid's wire format.

## Merge convention assumed by these fixtures

No compiler exists yet to check this semantically, so every merged fixture below hand-applies the same rule, consistent with `docs/architecture/c4-components-core-ir.md`'s Custom Filter Merge component ("merges the host's customFilters IR fragment into the filter tree", run *before* allowlist/limit validation so the combined tree is checked as one unit):

- Native filter present + `customFilters` present -> merged root is `And([nativeTree, customFragment])`, the native tree nested as one opaque child (never flattened into the merge's own `And`, regardless of the native tree's own shape).
- `customFilters` present, no native filter (`filterModel: null`) -> merged root is the `customFilters` fragment itself, unwrapped (no spurious single-child `And`).
- Native filter present, no `customFilters` -> merged root is the native tree, unchanged.

## Depth-counting convention

Depth is counted from the root node inward, root = depth 1; each `And`/`Or`/`Not` wrapper adds one level; a bare leaf `Comparison` at the root would be depth 1. This is the convention SEC-3's "max filter tree depth (default 8)" is measured against in these fixtures.

## Postgres compilation conventions used in these artifacts

> Suite-wide rendering rules (always-quoted identifiers, composite/negation parenthesisation,
> `NOT ILIKE`, `= ANY($1)`) are normative in `spec/semantics.md`'s "Addendum — canonical SQL
> rendering". The bullets below are what is specific to *this* category's tree shapes.

- **`notContains` renders with the negated operator, `<col> NOT ILIKE ...`, not `NOT (<col> ILIKE
  ...)`** (SQL-3). Fixture `006`'s second leaf is the one place this category shows it; the outer
  `NOT (...)` form below belongs to a **tree-level `Not` node** only, which is a different IR shape
  even though the two look alike in the text.
- **`eq`/`neq` leaves use a collation, never `ILIKE`.** Per SEM-1's pinned exact-match mechanism,
  a case-insensitive `eq`/`neq` leaf on a `string` field compiles as `<col> COLLATE craticula_ci =
  $n` (`<>` for `neq`) — `ILIKE` is reserved for `contains`/`notContains`/`startsWith`/`endsWith`,
  where the operator itself is a pattern match. `contains`/`notContains` leaves keep `ILIKE` and
  additionally carry `ESCAPE '\'`, since SEC-4's wildcard-escaping is a no-op without an explicit
  `ESCAPE` clause naming the escape character.
- **A tree-level `Not` always adds `OR <field> IS NULL` outside its own `NOT (...)`, per SEM-2.**
  This applies uniformly and recursively at every `Not` node in the tree, regardless of nesting
  depth or of how many distinct fields the wrapped subtree's leaves reference (one `OR <field> IS
  NULL` clause per distinct field, e.g. fixture `008`'s `Not(And(region, quantity))` gets both).
  Each `Not` template self-wraps in its own parens (`(NOT (...) OR ... IS NULL)`), so nested `Not`/
  `And`/`Or` combinations (fixtures `012`, `013`) accumulate the same redundant-but-harmless paren
  layers a non-optimizing recursive compiler would produce — these fixtures never algebraically
  simplify a double negation. This is the tree-level analogue of `neq`'s/`notContains`'s own
  already-mandatory `OR <col> IS NULL` (visible in fixture `006`), not a new mechanism.
- Every Mongo pipeline's `$sort` stage (the SEM-6 `id` tiebreaker's null-bucket emulation, present
  in all 15 fixtures regardless of what each one's own point is) is immediately followed by a
  `$unset` stage dropping `__idNullBucket`, per SEM-3's mandatory bucket-cleanup rule.

## Cases

| # | Case | Proves | Requirement ids |
|---|---|---|---|
| 001 | [`001-implicit-and-four-columns`](./001-implicit-and-four-columns/) | multi-column implicit AND (4 columns) | AR-5, AR-6 |
| 002 | [`002-within-column-or-conditions-array`](./002-within-column-or-conditions-array/) | within-column OR, current `conditions[]` form | AR-5, AR-6 |
| 003 | [`003-within-column-or-legacy-condition1-condition2`](./003-within-column-or-legacy-condition1-condition2/) | within-column OR, legacy `condition1`/`condition2` form | AR-5, AR-6, AG-1 |
| 004 | [`004-within-column-and-legacy-condition1-condition2`](./004-within-column-and-legacy-condition1-condition2/) | within-column AND, legacy `condition1`/`condition2` form, `operator: "AND"` | AR-5, AR-6, AG-1 |
| 005 | [`005-native-and-or-combination`](./005-native-and-or-combination/) | AND and OR combined, entirely from native `filterModel` (no `customFilters`) | AR-5, AR-6 |
| 006 | [`006-multi-filter-type-stacked-and`](./006-multi-filter-type-stacked-and/) | within-column AND via the `multi` filter type's `filterModels[]` stack | AR-5, AR-6, SEM-2 |
| 007 | [`007-customfilters-not-wraps-leaf-merged`](./007-customfilters-not-wraps-leaf-merged/) | `customFilters` NOT wraps a single leaf, merged with a native column filter | AR-5, OQ-11 |
| 008 | [`008-customfilters-not-wraps-and-merged`](./008-customfilters-not-wraps-and-merged/) | `customFilters` NOT wraps an And of two leaves, merged with a native column filter | AR-5, OQ-11 |
| 009 | [`009-customfilters-not-nested-in-or-merged`](./009-customfilters-not-nested-in-or-merged/) | `customFilters` NOT nested inside an Or, at a different tree position than `007`/`008` | AR-5, OQ-11 |
| 010 | [`010-customfilters-only-not-no-native-filter`](./010-customfilters-only-not-no-native-filter/) | `customFilters` NOT with no native `filterModel` at all — merge degenerates to the fragment itself | AR-5, OQ-11 |
| 011 | [`011-customfilters-depth-3`](./011-customfilters-depth-3/) | deliberate nesting depth 3 via `customFilters` (no native filter) | AR-5, SEC-3, OQ-11 |
| 012 | [`012-customfilters-depth-5`](./012-customfilters-depth-5/) | deliberate nesting depth 5 via `customFilters` (no native filter) | AR-5, SEC-3, OQ-11 |
| 013 | [`013-customfilters-depth-8-boundary`](./013-customfilters-depth-8-boundary/) | deliberate nesting depth 8 via `customFilters` — the SEC-3 default max-depth boundary, ACCEPTED | AR-5, SEC-3, OQ-11 |
| 014 | [`014-native-multicolumn-and-plus-customfilters-merge`](./014-native-multicolumn-and-plus-customfilters-merge/) | native multi-column And (3 columns) merged with a customFilters fragment | AR-5, OQ-11 |
| 015 | [`015-kitchen-sink-and-or-not-merge`](./015-kitchen-sink-and-or-not-merge/) | capstone: native And+Or combined with a customFilters Not(And) fragment, merged as one tree | AR-5, AR-6, OQ-11 |

## Depth reached, boundary cases

| Case | Depth reached | Note |
|---|---|---|
| `011-customfilters-depth-3` | 3 | well under the SEC-3 default max (8) |
| `012-customfilters-depth-5` | 5 | mid-range, still under the default max |
| `013-customfilters-depth-8-boundary` | 8 | **exactly** the SEC-3 default max — ACCEPTED, not an adversarial/rejection case; depth 9 (exceeding the limit) belongs to `adversarial/` |

Total: 15 fixtures.

