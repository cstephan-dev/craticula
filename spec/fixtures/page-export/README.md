# `page-export/` — page spec + export fixtures

Backs `spec/fixtures/README.md`'s category index row: "AG-3/EXP-2 page spec, EXP-1 export +
EXP-4/EXP-5/EXP-8 byte-exact writer output + EXP-6 hostile CSV values" (~20 target, 26 authored — see
022, 023, 024, 025 and 026 below). Requirement ids referenced below are
[TRD](../../../docs/TRD-grid-query-compiler.md) §6 (`AG-3`) and §6.1 (`EXP-1`..`EXP-8`); the
compile-time half of them is pinned normatively in `spec/semantics.md`'s **EXP-1/EXP-2** addendum,
which this category is the artifact oracle for. The *writer* half (EXP-4..EXP-8) is deliberately not
in `semantics.md` — it has no compiled form — so the TRD §6.1 prose is its normative home and the
expected-output files below are its oracle.

**In scope in all three conformance runners since #1650.** Every arm here — 26 Postgres, 14 Mongo and
14 Search — is compiled end to end from the fixture's own request and compared byte for byte. It was
excluded from all three until the AG Grid adapter learned `exportScope`; see the "#1650" section of
`spec/fixtures/README.md` for the five disagreements that surfaced when the arms were first executed.

**Twelve cases also carry a CSV/TSV writer oracle** — five since #1651 (EXP-4/EXP-5), six more since
#1652 (EXP-6), and `026` since #1653 (EXP-5's per-field overrides). That is every writer-facing case in
the category except `021`; see the shape note below.

## Shape notes specific to this category

- **Export wire-contract extension.** Fixtures 004-026 carry `exportFormat`/`exportScope`/
  `exportColumns` directly on `agGridRequest.json`, **alongside** the normal
  `filterModel`/`sortModel`/`startRow`/`endRow` fields — EXP-1 is an *extension* of the AG Grid
  SSRM wire request, not a separate request shape. Fixtures 001-003 (plain AG-3 paging) omit the
  export fields entirely, as a baseline.
- **CSV/TSV writer fixtures: twelve assert, one is prose because it has no output (#1651, #1652,
  #1653).**
  Until #1651 no CSV writer existed and all ten writer-facing cases that existed then (012-020, 022 —
  025 is #1651's own) carried a compiled artifact plus a documented expectation in prose.
  `Craticula.Export.Csv` now exists and so do its EXP-6 hardening and EXP-5's per-field overrides, so
  all twelve of the cases whose subject it implements carry a real oracle and are compared **byte for
  byte**, BOM included:
  **012, 013, 014, 015** (EXP-4's delimiter presets and BOM on/off) and **025** (EXP-4's tab delimiter,
  authored by #1651), then **016-020 and 022** (EXP-6's six trigger characters, converted by #1652
  alongside the `'`-prefixing they document — `018` also carries the suite's only negative number, which
  is what pins EXP-6's *scope*), then **026** (EXP-5's per-field date pattern and decimal separator,
  authored by #1653 — the only one of the twelve whose override lives in `schema.json` rather than in
  `writerConfig.json`, because that is where EXP-5 puts it). **021** is the one left in prose: it is
  EXP-7's row limit, an execution-adapter concern with no writer output at all to compare.

  A case with an oracle carries two extra inputs beside its request and schema, and one extra
  expected file:

  | File | What it is |
  |---|---|
  | `rowStream.json` | The rows an execution adapter yields for this fixture's compiled query, keyed by logical colId, in the compiled `ORDER BY`'s order. Validates against `../schemas/row-stream.schema.json`. |
  | `writerConfig.json` | EXP-4/EXP-5's **host** configuration (`delimiter`, `bom`, `headerRow`, `decimalSeparator`). **Present only when the case overrides a default** — nine of the twelve carry none (only 013, 015 and 026 do), which is what makes them oracles for the defaults, EXP-6's "default on" included. Validates against `../schemas/writer-config.schema.json`. Note what is *not* here: EXP-5's **per-field** overrides, which EXP-5 puts in schema config and which therefore reach the writer from `schema.json` through the request adapter, exactly as a `label` does (026). |
  | `expected/export.csv` / `expected/export.tsv` | The bytes. The extension is the request's own `exportFormat`, which is the only place in the fixture tree that field is visible at all (004/005 prove it reaches no compiled artifact). |

  **Not `dataset.json`/`expectedRows.json`.** Those two names are reserved for the execution-equality
  tier (T-1b, #1655), where they mean a *seeded database* and the row ids a query returns from it. A
  writer input is not a seed: it is what an adapter hands back **after** filtering, sorting,
  projection and physical-to-logical aliasing (`order_date` → `orderDate`), which is why it is keyed
  by logical colId and carries exactly the projected columns. Both can coexist in one directory later
  without either meaning the other.

  **How the rows were derived.** One cell of every row is *derived*: the one the request's `equals`
  filter names, because an `equals` filter's value is exactly what any matching row's cell contains —
  `status` is `"shipped"` in every row of 012-015, 025 and 026, and `notes` is the hostile value in every
  row of 016-020 and 022, which is the argument those fixtures' `description.md`s already made in prose. The
  row *order* is the compiled query's `ORDER BY`: `order_date ASC` then SEM-6's `id` tiebreaker for
  012-015/025/026, whose requests carry a `sortModel`, and the bare `id` tiebreaker for 016-020/022,
  whose `sortModel` is empty. Everything else is **chosen, not derived**, and each `description.md` says so
  and says what it was chosen to exercise (a delimiter inside a value, an inner quote, an embedded LF, a
  null, non-ASCII text, a tab).

  **Which cases may carry an EXP-6 trigger character, and which may not.** In 012-015, 025 and 026 no
  cell begins with `=`, `+`, `-`, `@`, tab or CR and no number is negative: those are EXP-6's triggers,
  and one here would make an EXP-4/EXP-5 case's bytes say something about EXP-6 too. In 016-020 and 022
  exactly one cell does — `notes`, which is the whole point — and `018` additionally carries a **negative
  `totalAmount`**, the only one in the suite, because EXP-6's scope (every cell and header except the
  ones the writer formatted from a number, a date/time or a boolean) is pinned by nothing else. No header in the category begins with a trigger
  either; that branch is unit-tested instead, since a twenty-seventh case would move the compiler runners'
  artifact counts for a property that has nothing to do with compilation.

  All twelve still use a single representative backend (`postgres.artifact.json`) for their compiled
  arm, since the behavior under test is at the writer layer, not the compiler layer.
- **One EXP-5 branch has no fixture oracle and cannot have one; the other now has 026.** The
  **collision guard** (`decimalSeparator == delimiter`) cannot be a fixture: the writer refuses before
  producing any bytes, so there is no expected file to compare — a rejection-shaped writer fixture
  would need the `adversarial/` category's `expected/rejection.json` treatment, which is a bigger
  change than the one branch justifies. It is pinned in `Craticula.Export.Csv.Tests`, in both its
  forms — the writer-wide separator and, since #1653, a **per-field** one, which is the identical
  defect and whose refusal names the offending field.

  The **legal** `;` + `,` pairing (the French/European locale the preset is named for) **is fixture
  026**, authored by #1653. It is not 013: 013 overrides the delimiter alone, because EXP-4 makes `;`
  a delimiter preset while EXP-5 keeps formatting on the invariant culture until a per-field override
  says otherwise, and coupling the two *in 013* would have made an EXP-4 oracle's bytes depend on a
  coupling the TRD does not state — one fixture would then have been testing two things. 026 is
  instead 013's request, row stream and writer config byte for byte, with a `schema.json` declaring
  `format.decimalSeparator: ","` on `totalAmount` and `format.datePattern: "dd/MM/yyyy"` on
  `orderDate`, so the diff between the two expected files is exactly EXP-5's two overrides with
  EXP-4's quoting held constant. *(Until #1653 this note attributed the missing schema member to
  #1631. That was wrong: #1631's DoD is the allowlist, the physical mappings and the text options, and
  it never owned formatting — the work was unclaimed, not blocked, which is what #1653 found.)*

  What remains unit-tested rather than pinned in bytes is the **writer-wide** decimal separator (a
  host configuration no schema declares, so no `schema.json` can carry it) and the disabled EXP-6
  hardening — the same reasoning in both cases: a fixture can only grade what a fixture's own inputs
  can express.

- **Hostile values are safe-handling, not rejection.** Distinct from the `adversarial/` category's
  `expected/rejection.json` shape: an EXP-6 hostile export value is a perfectly legal filter value
  that compiles normally. The compiled artifact shows the raw, unmodified value; the hardening
  (leading-apostrophe prefixing) belongs to the CSV writer and is asserted in `expected/export.csv`,
  never in the artifact. Each of the six therefore has **two** expected files that must disagree — the
  raw value in the artifact's `params`, the prefixed one in the bytes — which is the cheapest statement
  of "the compiler has no opinion on this value" the suite can make.
- **`exportable`, `label` and (since #1653) `format` exist now — the "`grid-schema.schema.json` gap"
  this README used to record is CLOSED.** `spec/grid-schema.schema.json` carries all three per-field
  properties and `FieldConfig.Exportable` defaults to `true`, so 009/021's "treat every field as exportable" is no
  longer an assumption bridging a gap; it is simply what that schema declares. Fixture **023** is the
  one that exercises the other branch (`"exportable": false`), and is also the only schema in the
  suite carrying a `label` — which appears in **no** artifact, since a header is the CSV writer's
  (EXP-4), exactly as 008's three `header` overrides appear in none of its three arms.
- **A projection appears if and only if the request is an EXPORT (EXP-1(b)), and 001-003 are the
  baseline that proves it.** Fixtures 004-026 carry export members and therefore name their output
  columns: Postgres a `SELECT` list, MongoDB a trailing `$project`, and Search nothing at all
  (OSE-13). Fixtures **001-003 carry no export member** and render `SELECT *` with no `$project`,
  exactly like every other ordinary row query in the suite — GRP-3's carve-out, which EXP-1(b)
  deliberately did not generalise. *(A previous revision of this README claimed the projection applied
  "category-wide, including 001-007 and 010-022". It did not: those three fixtures' own Mongo arms
  never had a `$project`, and #1650 corrected their Postgres arms rather than the rule.)*
- **Postgres physical-to-logical column aliasing, inside a projection.** `schema.json`'s
  `physical.sql.column` differs from the logical field name for `orderDate` (`order_date`),
  `customerName` (`customer_name`), and `totalAmount` (`total_amount`). Every `SELECT` list that
  exists in this category aliases those three back to their logical camelCase names
  (`"order_date" AS "orderDate"`, etc.) and leaves the other four bare — the same "alias only when the
  physical name differs from the logical field name" convention `group-aggregation/`'s 005-006
  fixtures use (see 008's `description.md` for the full rationale). `ORDER BY`/`WHERE` clauses
  reference the physical column name directly, never the alias, matching every other category's
  convention.
- **MongoDB's `$project` writes `_id: 0` LAST**, after the output fields, matching
  `group-aggregation/005`/`006`. MongoDB accepts the suppression anywhere; the output list's order is
  what is normative and byte-compared, so the key that names no output column goes after it. Eight
  arms here wrote it first until #1650 executed them.
- **Canonical SQL rendering (#1638).** Every Postgres artifact in the category (22 of them at the
  time) gained identifier quoting — every
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
| 012 | `012-csv-writer-delimiter-comma-default` | Default `,` delimiter, header row on, CRLF, RFC 4180 quoting — **byte-exact** | EXP-4, EXP-8 |
| 013 | `013-csv-writer-delimiter-semicolon-preset` | First-class `;` delimiter preset; quoting follows the active delimiter — **byte-exact** | EXP-4, EXP-8 |
| 014 | `014-csv-writer-bom-on-default` | UTF-8 BOM on by default, over non-ASCII cells — **byte-exact** | EXP-4, EXP-8 |
| 015 | `015-csv-writer-bom-off-configured` | UTF-8 BOM explicitly disabled; 014's file minus three bytes — **byte-exact** | EXP-4, EXP-8 |
| 016 | `016-hostile-value-formula-equals` | Leading `=` passes through the compiler unchanged; the writer prefixes it with `'` — **byte-exact** | EXP-6, EXP-8 |
| 017 | `017-hostile-value-formula-plus` | Leading `+` on a phone number, prefixed by the writer — **byte-exact** | EXP-6, EXP-8 |
| 018 | `018-hostile-value-formula-minus` | Leading `-` prefixed on the **string** cell while the **negative `decimal`** beside it is not — EXP-6's scope, pinned nowhere else — **byte-exact** | EXP-6, EXP-8 |
| 019 | `019-hostile-value-formula-at` | Leading `@` passes through the compiler unchanged; the writer prefixes it with `'` — **byte-exact** | EXP-6, EXP-8 |
| 020 | `020-hostile-value-cr-quote-newline-delimiter` | Leading CR + embedded quote/newline/delimiter: prefix **and** RFC 4180 quoting on one cell, apostrophe inside the quotes — **byte-exact** | EXP-6, EXP-8, EXP-4 |
| 021 | `021-export-max-rows-limit-default` | Default max export rows (1,000,000) is an execution-adapter concern, not a compiled-artifact field (documented) | EXP-7 |
| 022 | `022-hostile-value-formula-tab` | Leading tab prefixed but **not** quoted, the delimiter being `,` — rounds out EXP-6's trigger-character coverage alongside 016-020 — **byte-exact** | EXP-6, EXP-8 |
| 023 | `023-export-columns-default-skips-non-exportable-field` | Default projection omits a field declared `exportable: false`, which is still filterable | EXP-1, EXP-1(b) |
| 024 | `024-export-all-ignores-drill-down-group-keys` | Export discards a **non-empty `groupKeys`** too — no drill-down predicate, no grouped level | EXP-1 (group-export carve-out), EXP-1(a) |
| 025 | `025-tsv-writer-tab-delimited` | `exportFormat: "tsv"` makes the writer emit tabs, and a tab inside a value is quoted while a `,`/`;` is not — **byte-exact**, with a compiled arm byte-identical to 012's | EXP-4, EXP-8 |
| 026 | `026-csv-writer-per-field-format-overrides` | Per-field `datePattern` and `decimalSeparator` from **schema config**, on two different fields of one row, five untouched beside them — **byte-exact**, with a compiled arm byte-identical to 013's; the legal `;`+`,` locale pairing this README had reserved | EXP-5, EXP-8 |

## Backend coverage

Per `spec/fixtures/README.md`'s policy, these are general/structural fixtures: Postgres + Mongo +
shared Search, except:

- Fixtures 012-020, 022, 025 and 026 (CSV/TSV writer behavior + hostile values + per-field formats):
  Postgres only. The
  behavior under test lives entirely at the writer layer; a single representative backend is
  sufficient (see the shape note above). Their Postgres arm is nonetheless an ordinary compiled query
  and is compared like any other — which is why this category contributes 26 arms to the Postgres
  runner and 14 to each of the other two.
- The twelve writer oracles are backend-agnostic by construction: a row stream is what *any* adapter
  yields, so there is no per-backend expected output and never will be. That is EXP-4's "backend-
  agnostic module" restated as a fixture shape.
- Fixture 008's Mongo artifact demonstrates column narrowing via a `$project` stage; the shared
  Search artifact schema (`search-artifact.schema.json`) has no source-filtering property, so
  Search's own column narrowing cannot be shown structurally there (documented in that fixture's
  `description.md`).

### Search arms of 004-007 corrected (#1644 review pass)

At the time, this category was excluded from `SearchConformanceFixtureTests` because #1623 owned
`exportScope` — not because its Search artifacts were exempt from `spec/semantics.md`'s OSE-*
addendum — and four of them had drifted while nothing could compile them:

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

#1650 lifted the exclusion, and the same reasoning predicted more: executing the arms for the first
time found four *Mongo* drifts of exactly the kind the Search pass had found (an implicit `eq`, a
bare-number `decimal` bound twice, and eight `_id: 0` placements). The stand-in test that compiled
004-007 with only the `PageSpec` supplied is gone, superseded by the real end-to-end runner.
