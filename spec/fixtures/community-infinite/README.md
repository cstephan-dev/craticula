# `spec/fixtures/community-infinite/` — AG Grid Community (Infinite Row Model) flat requests

Proves **AG-7** (the maintainers' private TRD, §6): a request in the shape AG Grid Community's Infinite Row
Model sends compiles on every backend with the same semantics as the Server-Side Row Model. Community has no row grouping,
aggregation, pivot or set filter (all Enterprise), so every case is a **flat** request.

## What a Community request looks like

The Infinite Row Model's `getRows` params carry `startRow`, `endRow`, `sortModel` and `filterModel` (and an optional
`context`). There is no `rowGroupCols`, `valueCols`, `pivotCols`, `pivotMode` or `groupKeys`, and no `requestContext`. The cases here
leave those fields out as Community does; the host adds nothing. No `requestContext` is sent, so a `datetime` field would resolve on
`AgGridParseOptions.DefaultZone` (UTC unless the host sets another); the date cases here are on `date` fields, which need no zone.

The filter model follows AG Grid's own filter classes: `{filterType: "text", type, filter}`, `{filterType: "number", type, filter}`,
`{filterType: "date", type, dateFrom}` (on a `date` column `dateFrom` is `YYYY-MM-DD`, as AG Grid 36 sends it), and two
conditions as `{filterType, operator: "AND" | "OR", conditions: [..]}` (the form AG Grid 36 sends; majors before it sent
`condition1`/`condition2`, which the parser also accepts and `filter-tree/003` and `ag-grid-majors/` pin).

Not modelled: AG Grid 35 and later also send a `type` on every sort item and `filterModel: {}` (not `null`) when no filter is
set. The parser ignores both; `ag-grid-majors/005` and `006` cover them, and a captured payload carrying them still equals its
oracle (the oracle script compares requests after dropping those differences).

## Provenance: `derived` or `captured`

Every `description.md` carries one of two labels in its first line:

- **`derived`** — the request was written from AG Grid's published sources (the filter model types of the text, number and date
  filters and the Infinite Row Model's `getRows` params), not recorded from a running grid.
- **`captured`** — the request is a payload recorded from a real Community grid (version noted in the description).

All cases start as `derived` (maker's decision 2026-10-16: derived now, real capture later). To capture one: run the demo's
Community page (`examples/demo`, `/community.html`), set the filter, press **Copy request**. The copied body uses the demo's
column names (`orders.schema.json`: `customer`, `amount`, `orderedAt`, ...), which are not the fixtures' (`productName`, `age`,
`birthDate`, ...), so a captured payload does not drop into an existing case. Either (a) rename its columns to the case's
schema by hand and keep the case's other files, or (b) add it as a new case with the demo's `orders.schema.json` as its
`schema.json` and its own dataset and expected artifacts. Then run `bash scripts/conformance.sh`; if the artifacts match (case
a) change the label to `captured` and name the AG Grid version; if they differ, the difference is a finding about the
derivation and is handled as a bug or a spec question first.

## Oracle

Where a case's compiled IR equals an existing `operators/` or `sort-paging/` case, the expected artifacts equal that case's
artifacts for the same backend (`scripts/check-community-oracle.py` checks it). A difference is explained in the case's
`description.md`, never silently accepted.

## Backend coverage

`postgres` + `mongo` + `search` on every case, per the suite-wide policy (`../README.md`); the five SQL dialects stay with
`dialect-parity/`. Every case also carries `dataset.json` + `expectedRows.json` for the execution-equality tier (XEQ-1..XEQ-4).

## Scope boundaries (deliberate)

- Native `inRange` is **not** a fixture: a rejection has no compiled artifact (`../../conformance-runner.md`); it is pinned by a unit
  test in `Craticula.AgGrid.Tests`.
- No grouping, aggregation, pivot or set filter: they do not exist on Community.
- The response shape (`successCallback(rows, lastRow)`) is a host concern (AG-3) and has no fixture.

## Cases

All cases are `derived`. The oracle column is `oracle.json`'s mapping.

| # | Case | Oracle |
|---|---|---|
| 001 | `001-text-equals-single/` | hand-derived from `operators/001-eq-string` |
| 002 | `002-text-equals-or-two-conditions/` | same IR as `operators/001-eq-string` |
| 003 | `003-text-notequal/` | same IR as `operators/002-neq-string` |
| 004 | `004-text-contains/` | same IR as `operators/025-contains-string` |
| 005 | `005-text-notcontains/` | same IR as `operators/026-notcontains-string` |
| 006 | `006-text-startswith/` | same IR as `operators/027-startswith-string` |
| 007 | `007-text-endswith/` | same IR as `operators/028-endswith-string` |
| 008 | `008-text-blank/` | same IR as `operators/029-blank-string` |
| 009 | `009-text-notblank/` | same IR as `operators/030-notblank-string` |
| 010 | `010-number-equals/` | same IR as `operators/003-eq-int64` |
| 011 | `011-number-notequal-and-two-conditions/` | same IR as `operators/004-neq-int64` |
| 012 | `012-number-greaterthan/` | same IR as `operators/009-gt-int64` |
| 013 | `013-number-greaterthanorequal/` | same IR as `operators/013-gte-int64` |
| 014 | `014-number-lessthan/` | same IR as `operators/017-lt-int64` |
| 015 | `015-number-lessthanorequal/` | same IR as `operators/021-lte-int64` |
| 016 | `016-number-blank/` | same IR as `operators/031-blank-int64` |
| 017 | `017-number-notblank/` | same IR as `operators/032-notblank-int64` |
| 018 | `018-date-equals-or-two-conditions/` | same IR as `operators/007-eq-date` |
| 019 | `019-date-notequal/` | same IR as `operators/008-neq-date` |
| 020 | `020-date-greaterthan/` | same IR as `operators/011-gt-date` |
| 021 | `021-date-lessthan/` | same IR as `operators/019-lt-date` |
| 022 | `022-date-blank/` | same IR as `operators/033-blank-date` |
| 023 | `023-date-notblank/` | same IR as `operators/034-notblank-date` |
| 024 | `024-two-columns-filtered-at-once/` | same IR as `filter-tree/001-implicit-and-four-columns` |
| 025 | `025-sort-descending/` | same IR as `sort-paging/004-single-key-string-desc-nulls-first-default` |
| 026 | `026-sort-multi-column/` | same IR as `sort-paging/006-multi-key-two-fields-order-preserved` |
| 027 | `027-page-window/` | same IR as `sort-paging/011-page-range-with-tiebreaker` |

Deliberate merges with existing fixtures: a Community request has no `rowGroupCols` and the other group fields, but compiles to the same IR as the matching `operators/`, `sort-paging/` or `filter-tree/` case, so those cases are the oracle instead of being re-authored; only the single-condition text `equals` is new.
