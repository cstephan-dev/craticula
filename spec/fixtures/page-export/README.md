# `page-export/` — page spec + export fixtures

Backs `spec/fixtures/README.md`'s category index row: "AG-3/EXP-2 page spec, EXP-1/EXP-6/EXP-8
export + hostile CSV values" (~20 target, 22 authored — see 022 below, added to round out EXP-6's
trigger-character coverage). Requirement ids referenced below are
[TRD](../../../docs/TRD-grid-query-compiler.md) §6 (`AG-3`) and §6.1 (`EXP-1`..`EXP-8`).

## Shape notes specific to this category

- **Export wire-contract extension.** Fixtures 004-022 carry `exportFormat`/`exportScope`/
  `exportColumns` directly on `agGridRequest.json`, **alongside** the normal
  `filterModel`/`sortModel`/`startRow`/`endRow` fields — EXP-1 is an *extension* of the AG Grid
  SSRM wire request, not a separate request shape. Fixtures 001-003 (plain AG-3 paging) omit the
  export fields entirely, as a baseline.
- **CSV writer + hostile-value fixtures (012-020, 022) document, rather than assert, writer
  behavior.**
  No CSV writer exists yet in this codebase (only the artifact-equality tier, T-1a, is delivered
  by this story — see `docs/feat/1630/context.md` decision #3). These fixtures carry a real
  compiled artifact (the row-stream source a future writer would consume) plus a documented
  expectation in `description.md` for what the writer must do with that stream. They are **not**
  byte-exact CSV file fixtures, and intentionally use a single representative backend
  (`postgres.artifact.json`) since the behavior under test is at the writer layer, not the
  compiler layer.
- **Hostile values are safe-handling, not rejection.** Distinct from the `adversarial/` category's
  `expected/rejection.json` shape: an EXP-6 hostile export value is a perfectly legal filter value
  that compiles normally. The compiled artifact shows the raw, unmodified value; hardening
  (leading-apostrophe prefixing) is documented as a future CSV-writer obligation, not enforced
  here.
- **`grid-schema.schema.json` gap.** The shared meta-schema (SC-1..SC-5), authored ahead of the
  real `GridSchema` model (#1631), has no per-field `exportable` flag and no per-field `label` yet
  — both assumed by EXP-1's default-projection wording. Fixtures 009 and 021 (and, by extension,
  every other export-scope-"all" fixture) document the assumption used to bridge that gap: every
  field in `schema.fields` is treated as exportable, in the object's own key order, with the bare
  `colId` standing in for a schema label. See fixture 009's `description.md` for the full note.
- **Postgres physical-to-logical column aliasing applies category-wide, not just to the
  `exportColumns` fixtures.** `schema.json`'s `physical.sql.column` differs from the logical field
  name for `orderDate` (`order_date`), `customerName` (`customer_name`), and `totalAmount`
  (`total_amount`). Every `postgres.artifact.json` `SELECT` list in this category aliases those
  three back to their logical camelCase names (`"order_date" AS "orderDate"`, etc.) — the same "alias
  only when the physical name differs from the logical field name" convention `group-aggregation/`'s
  005-006 fixtures use (see 008's `description.md` for the full rationale). `ORDER BY`/`WHERE`
  clauses reference the physical column name directly, never the alias, matching every other
  category's convention. This applies uniformly, including to fixtures whose own point is
  unrelated to column projection (001-007, 010-022) — a fixture's artifact must be correct in
  every detail, not just the one property it's authored to exercise.
- **Canonical SQL rendering (#1638).** All 22 Postgres artifacts gained identifier quoting — every
  column, every alias, and the table — per `spec/semantics.md`'s "Addendum — canonical SQL
  rendering" (SQL-1). One artifact also had a rendering rewritten: `005`'s `region IN ($1, $2)` with
  two scalar parameters became `"region" = ANY($1)` with a **single array parameter**
  (`params: [["EMEA", "APAC"]]`), SQL-4's Postgres in-list form, which is what `operators/035`-`038`
  and `dialect-parity/016` already used. This category is otherwise out of scope for the SQL
  compiler story (#1638 §"Scope boundaries") — the rewrite happened here anyway because leaving one
  category spelling `in` differently would have preserved exactly the contradiction the pass exists
  to remove.

## Cases

| # | Slug | Proves | Requirement ids |
|---|---|---|---|
| 001 | `001-range-page-first-offset` | First-page range paging, `startRow: 0` | AG-3 |
| 002 | `002-range-page-mid-offset` | Mid-stream offset range paging, descending sort | AG-3, SEM-3 (adapter default) |
| 003 | `003-range-page-small-size-odd-offset` | Small page size at a non-round offset | AG-3 |
| 004 | `004-export-format-csv` | `exportFormat: "csv"` never affects compilation | EXP-1 |
| 005 | `005-export-format-tsv` | `exportFormat: "tsv"` never affects compilation | EXP-1 |
| 006 | `006-export-scope-all-compiles-page-none` | `exportScope: "all"` ignores `startRow`/`endRow`, compiles `page: none` | EXP-1, EXP-2 |
| 007 | `007-export-scope-page-honors-range` | `exportScope: "page"` honors `startRow`/`endRow` | EXP-1 |
| 008 | `008-export-columns-subset-custom-headers` | `exportColumns` explicit subset, custom order + headers | EXP-1 |
| 009 | `009-export-columns-default-schema-order` | `exportColumns` absent -> default schema-order projection | EXP-1 |
| 010 | `010-export-all-ignores-single-level-group-state` | Export flattens an active single-level group+agg to leaf rows | EXP-1 (group-export carve-out) |
| 011 | `011-export-all-ignores-multi-level-group-and-multiple-aggs` | Same carve-out under a two-level group + two aggregations | EXP-1 (group-export carve-out) |
| 012 | `012-csv-writer-delimiter-comma-default` | Default `,` delimiter, header row on, CRLF, RFC 4180 quoting (documented) | EXP-4 |
| 013 | `013-csv-writer-delimiter-semicolon-preset` | First-class `;` delimiter preset (documented) | EXP-4, EXP-5 |
| 014 | `014-csv-writer-bom-on-default` | UTF-8 BOM on by default (documented) | EXP-4 |
| 015 | `015-csv-writer-bom-off-configured` | UTF-8 BOM explicitly disabled (documented) | EXP-4 |
| 016 | `016-hostile-value-formula-equals` | Leading `=` passes through compiler unchanged (documented writer prefix) | EXP-6, EXP-8 |
| 017 | `017-hostile-value-formula-plus` | Leading `+` passes through compiler unchanged (documented writer prefix) | EXP-6, EXP-8 |
| 018 | `018-hostile-value-formula-minus` | Leading `-` passes through compiler unchanged (documented writer prefix) | EXP-6, EXP-8 |
| 019 | `019-hostile-value-formula-at` | Leading `@` passes through compiler unchanged (documented writer prefix) | EXP-6, EXP-8 |
| 020 | `020-hostile-value-cr-quote-newline-delimiter` | Leading CR + embedded quote/newline/delimiter (documented writer prefix + RFC 4180 quoting) | EXP-6, EXP-8, EXP-4 |
| 021 | `021-export-max-rows-limit-default` | Default max export rows (1,000,000) is an execution-adapter concern, not a compiled-artifact field (documented) | EXP-7 |
| 022 | `022-hostile-value-formula-tab` | Leading tab passes through compiler unchanged (documented writer prefix) — rounds out EXP-6's trigger-character coverage alongside 016-020 | EXP-6, EXP-8 |

## Backend coverage

Per `spec/fixtures/README.md`'s policy, these are general/structural fixtures: Postgres + Mongo +
shared Search, except:

- Fixtures 012-020 and 022 (CSV writer behavior + hostile values): Postgres only. The behavior
  under test lives entirely at the writer layer; a single representative backend is sufficient
  (see the shape note above).
- Fixture 008's Mongo artifact demonstrates column narrowing via a `$project` stage; the shared
  Search artifact schema (`search-artifact.schema.json`) has no source-filtering property, so
  Search's own column narrowing cannot be shown structurally there (documented in that fixture's
  `description.md`).

### Search arms of 004-007 corrected (#1644 review pass)

This category is excluded from `SearchConformanceFixtureTests` because #1623 owns `exportScope`, not
because its Search artifacts are exempt from `spec/semantics.md`'s OSE-* addendum — and four of them
had drifted while nothing could compile them:

- **004-007 wrapped an ordinary hit-returning query in `{"bool": {"filter": [...]}}`.** OSE-3 reserves
  that container for a **grouping or distinct-values** request; a row query renders its filter tree
  directly. Corrected on all four.
- **004 also spelled its `term` with the bare shorthand** (`{"status.keyword": "shipped"}`). OSE-1
  keys a `term`'s form on the field's declared **type**, so a `string` always takes the long
  `{"value": …}` form — `status` is declared `sensitive`, which is exactly why it carries no
  `case_insensitive` and why "long only when a parameter is present" is not the rule. This was the
  **fourth** short-form string `term` in the suite, and the only one that was an ordinary client value
  rather than `blank`'s empty-string sentinel; the other three (`operators/029`, `/030`,
  `dialect-parity/019`) were corrected in #1644 itself.

The four are compiled and compared by
`SearchQuerySortAndPagingTests.PageExport004To007_Compile_To_Their_Own_Artifacts_Once_The_Page_Is_Supplied`,
with only the `PageSpec` supplied directly — the same treatment `sort-paging/012` already gets, and
the reason the exclusion does not have to mean "unchecked".
