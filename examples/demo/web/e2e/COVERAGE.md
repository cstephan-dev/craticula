# What the browser tests cover

These tests replace the click-through checklist of the demo README for everything a script can check. This page lists each
scenario that used to be checked by hand, the spec that now covers it, and the few that stay manual or live somewhere else.
Specs are in this folder; `<p>` means one test per provider the API lists, and a test for a provider the API does not list is
skipped with that reason in the report.

How a test decides it passed: the rows the grid received from `/api/<provider>/rows` must equal what PostgreSQL answers to the
same request (a failure names the provider, the scenario, the row and the column), and a few cells on screen must show those
values. The accepted differences are in `support/divergences.ts`, each with its reason; they are listed at the end.

## Scenarios covered

| Scenario (what you see or do) | Spec | Notes |
|---|---|---|
| Pick a provider in the header with a Customer filter and an Amount sort: the same rows as PostgreSQL | `enterprise-providers.spec.ts` (provider switch) | every provider, including the two EF Core routes on SQLite and SQL Server |
| Open the Country set filter: the values come from the API; untick some and the grid narrows | `enterprise-providers.spec.ts` (set filter) | `<p>` |
| Clear every Country value in the set filter: an empty grid and no error banner | `enterprise-providers.spec.ts` (set filter) | `<p>` |
| Number (Quantity greater than 8), date (Shipped on, Ordered at) and boolean (Priority) filters, combined | `enterprise-providers.spec.ts` (filters by column type) | `<p>`; counts equal PostgreSQL's |
| Sort by clicking a header; shift-click a second and a third column (Id first, Id in the middle, a column twice) | `enterprise-sorting.spec.ts` | `<p>`; includes the Id-then-Amount and Amount, Id, Country orders |
| Scroll down a long list: blocks of 100 load, the grid reaches the end, rows past 10,000 load | `enterprise-sorting.spec.ts` | `<p>`; a search engine has no total, so the list grows block by block and the test allows for it |
| Group by Country then Category, add Amount and Quantity as values, choose sum, avg and count, open a group and then a Category | `enterprise-grouping.spec.ts` | `<p>`; every level equals PostgreSQL's answer to the same request |
| Group, then order the groups by an aggregate: a "Not supported on this provider" banner, the grid stays | `enterprise-grouping.spec.ts` | OpenSearch and Elasticsearch only |
| The Pages button: numbered pages (200 of 100 rows), the total, page 2 and the last page equal PostgreSQL's, then scrolling again | `enterprise-paging.spec.ts` | `<p>`; a search engine has no total and no last page, and the test says so |
| A filter and then a sort bring a later page back to page 1 | `enterprise-paging.spec.ts` | |
| Group, then click Pages: groups, filter and sort stay | `enterprise-paging.spec.ts` | |
| The Columns panel has no Pivot Mode switch and no Pivot drop zone | `enterprise-paging.spec.ts` | |
| In range on Amount and on Ordered at: both bounds exclusive, the count equals two plain conditions joined by AND | `enterprise-in-range.spec.ts` | PostgreSQL, MongoDB and SQLite on the Enterprise page; every provider on the Community page |
| In range joined to another condition by OR: the `unsupported-filter` message | `enterprise-in-range.spec.ts` | |
| Change or clear a range afterwards: the grid keeps answering | `enterprise-in-range.spec.ts` | |
| Customer contains `söhne`, `SÖHNE` and `sohne`: each engine finds what its collation says (accent and case rules per engine) | `enterprise-collation.spec.ts` | `<p>` |
| Export CSV with a filter and a sort: the file equals PostgreSQL's | `export.spec.ts` | `<p>`; the file is read cell by cell |
| Export CSV with no filter: 20,000 rows once each, a cell starting with `=` saved with a leading `'` | `export.spec.ts` | `<p>` |
| Export CSV after a multi-sort | `export.spec.ts` | `<p>` |
| Query panel: SQL with its parameters (PostgreSQL `$1`, SQL Server `@p1` with `COLLATE`, MySQL and SQLite `?`), the MongoDB pipeline, the search engines' JSON; the note on the EF Core routes; Copy query | `query-panel.spec.ts` | `<p>`; the panel can be closed and opened again |
| Community page: the grid loads with no Enterprise package, no watermark, no set filter, no grouping | `community.spec.ts` | |
| Community page: sort, text, number and date filters, scrolling | `community.spec.ts` | |
| Community page: switch provider keeps the filter and the sort and answers like PostgreSQL | `community.spec.ts` | every provider but the baseline |
| Community page: In range on a number and a date column | `community.spec.ts` | `<p>` |
| Community page: Copy request puts the flat request on the clipboard | `community.spec.ts` | |
| The Enterprise page shows the evaluation watermark | `enterprise-providers.spec.ts` | |
| A database that is stopped: the grid stays, a `backend-unavailable` banner (HTTP 503) appears, another provider still works, the provider answers again after the container restarts | `errors.spec.ts` | opt-in (`E2E_DESTRUCTIVE=1`): it stops and restarts containers. Not for the file-based providers, which have no container |
| The page loads against a running API and shows the same first block as PostgreSQL | `smoke.spec.ts` | `<p>` |
| The demo is not running: the run stops at once and prints how to start it | `global-setup.ts` | not a test: the run exits before any spec |

## Not automatable, or covered somewhere else

| Scenario | Where it is covered, or why it is not |
|---|---|
| Post an unknown column (or any bad request) straight to `/api/<provider>/rows`: a typed 400 | not a browser scenario: `smoke.sh` (several refusals, every provider) and the demo's API tests |
| A native In range sent to the API without the page's translation: 400 `unknown-operator` | not a browser scenario (the page never sends it): `smoke.sh` and the API tests |
| In range with one bound only | AG Grid sends a range only when both bounds are filled, so the page cannot produce it; the translation is covered by the page's unit tests (`src/inRange.test.ts`) and by `smoke.sh` |
| Open the group whose key is empty (the null group) | the grid sends nothing when that blank group row is clicked (measured on PostgreSQL and OpenSearch), so the click cannot be scripted; the API answer is covered by the API tests and `smoke.sh` on the engines that support it |
| Read the README sections (the EF Core walkthrough "from the code only", what to expect, the per-engine notes) | reading, not behaviour |
| Package approvals, licence acceptance for the SQL Server image | a decision of a person, not of the page |
| Library behaviour (compiled SQL for a sort that repeats a key, fixture conformance, execution equality on live engines) | the library's own test projects and the conformance runner, not the demo page |
| Timing on a cold engine (first request to SQL Server or OpenSearch) | the tests allow generous first-request timeouts; they do not measure speed |
| The exact look of the page (screenshots) | not asserted; a failed test keeps a screenshot and a trace for you to look at |

## Differences between providers the tests accept

Each is measured and documented in the demo README; a test only excuses a difference listed here, and only for the provider
and the key named. Anything else that differs fails the run.

| Provider | Difference | Where in the code |
|---|---|---|
| SQLite | a grouped `amount` sum can differ in its last digits (floating point), relative 1e-9 | `NUMBER_RULES` |
| SQL Server | the average of a whole-number column is an integer: PostgreSQL's value cut toward zero | `NUMBER_RULES` |
| MySQL | the average of a whole-number column has four decimals: PostgreSQL's value rounded half up | `NUMBER_RULES` |
| every provider | an average can differ in the 7th significant digit (relative 1e-6) | `NUMBER_RULES` |
| OpenSearch, Elasticsearch | the row count above 10,000 is unknown, so `lastRow` is null where PostgreSQL's is larger | `LAST_ROW_UNKNOWN` |
| OpenSearch, Elasticsearch | the group with no key sits at the other end of a grouped level | `NULL_GROUP_AT_OTHER_END` |
| OpenSearch, Elasticsearch | ordering groups by an aggregate is refused (`unsupported-shape`), tested as a banner | `enterprise-grouping.spec.ts` |
| each engine | which spellings of "Söhne" a contains filter finds depends on its collation | `enterprise-collation.spec.ts` |
