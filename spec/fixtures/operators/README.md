# `spec/fixtures/operators/` — AR-6's closed 17-operator set

Proves AR-6's closed operator set (`eq, neq, gt, gte, lt, lte, contains, notContains, startsWith,
endsWith, blank, notBlank, in, notIn, inRange, true, false`) across the AR-7 field-type matrix
(`string, int64, decimal, double, boolean, date, datetime`). Every case backs at least AR-6; most
also back one or more SEM rules (SEM-1 case sensitivity, SEM-2 null handling, SEM-4 dates & zones,
SEM-7 `contains` on Search) — see each case's own `description.md` for the exact set.

## Backend coverage

General/structural fixtures per the suite-wide policy (`../README.md`): `postgres` + `mongo` +
`search` on every case. No dialect-specific or cross-backend-parity showcase cases live here (that's
`dialect-parity/`'s job).

## Scope boundaries (deliberate)

- **Paging/sorting is minimal, not the proof point — but SEM-3's null-ordering marker is still
  mandatory.** Every fixture's `agGridRequest.json` sorts by the schema's `stableKey` (`id`)
  ascending and pages `startRow:0/endRow:100` purely so the compiled artifacts are deterministic and
  realistic (a real SSRM `getRows` call always carries a row range); *varying* the sort/paging shape
  itself (multi-key sorts, the stableKey-tiebreaker-append mechanics of SEM-6, page ranges) is
  deliberately **not** exercised here — that is `sort-paging/`'s job. What is NOT optional, per SEM-3's
  own "must NOT omit the null-ordering marker on the theory that the field is probably never null"
  rule: every artifact's `id ASC` sort still carries its adapter-default `nullsPosition: "last"`
  marker — `NULLS LAST` in Postgres, an `__idNullBucket` null-bucket `$addFields`/`$sort` pair in
  Mongo, `missing: "_last"` in Search — unconditionally, on every one of the 47 cases, exactly as
  `filter-tree/` and `sort-paging/` already do for their own bare `id` sort.
- **Mongo case-insensitivity: `collation` for exact-match/set ops, `$regex` for substring ops.**
  Per SEM-1's pinned collation-vs-pattern-match split, `mongo-artifact.schema.json`'s wrapper-object
  shape (`{pipeline, collation}`) is used for the `eq`/`neq`/`in`/`notIn` cases that need
  case-insensitive matching (001, 002, 035, 036) — a query-level `collation: {locale, strength: 2}`
  alongside the pipeline, SEM-1's own preferred exact-match mechanism. `contains`/`startsWith`/
  `endsWith` (025, 027, 028) and their negation (026) have no collation-compatible substring
  operator at all (SEM-1/SEM-7), so those cases keep the bare-array pipeline shape and use `$regex`
  with the `i` option instead (anchored `^...$` for `eq`/`in` would have been the old fallback, no
  longer used now that 001/002/035/036 carry `collation` directly).
- **Postgres case-insensitivity: a `craticula_ci` collation for exact-match/set ops, `ILIKE` for
  substring ops.** Same SEM-1 split as above, applied to the SQL artifacts: 001/002/035/036
  (`eq`/`neq`/`in`/`notIn` on `string`) compile to `col COLLATE craticula_ci = $N` / `<>` / `= ANY(...)`
  / `<> ALL(...)`, never `ILIKE` — reusing `ILIKE` for exact match was the trap SEM-1 explicitly rules
  out (a literal `%`/`_` in the value would silently become a wildcard). `contains`/`notContains`/
  `startsWith`/`endsWith` (025-028) are the only cases that use `ILIKE` (with an explicit `ESCAPE`
  clause), because the operator itself is a pattern match there.
- **Boolean fields have no native `eq`/`neq`/`in`/`notIn` wire path.** Confirmed against the
  `github/ag-grid` clone's `iSetFilter.ts`: `SetFilterModel` is the only wire shape available for any
  column type (AG Grid core has no dedicated boolean filter), and the adapter compiles a boolean
  column's single-value Set Filter selection to the dedicated unary `true`/`false` operators (see
  044/045), never to generic `eq`/`neq`. Cases 005/006 (`eq`/`neq` on `boolean`) therefore use the
  `customFilters` (OQ-11) request extension to inject the IR fragment directly, proving the
  compiler/backend layer's support for those operators against `boolean` independent of adapter/UI
  reachability.
- **`notIn` has no native Set Filter wire path.** Confirmed against the same `iSetFilter.ts`:
  `SetFilterModelValue` (`(string|null)[]`) is an inclusion list only — there is no invert/exclude
  flag anywhere in `ISetFilterParams`/`SetFilterModel`. Cases 036/038 (`notIn` on `string`/`int64`)
  therefore also use `customFilters` for the same reason.
- **Legacy combined-filter form (AG-1).** Cases 001, 004 and 007 use the pre-v29
  `condition1`/`condition2`/`operator` wire shape (instead of the current `conditions[]` array) to
  combine two same-operator conditions, proving AG-1's requirement that both forms parse identically.
  Every other case uses a single, unwrapped condition — AG Grid's wire shape for a lone (non-combined)
  filter condition, unchanged between the legacy and current combining schemes, so "current form" for
  a single condition is simply the bare condition object, not a `conditions[]`-wrapped one.
- **`inRange` has no native inclusivity-flag wire path.** Confirmed against `iNumberFilter.ts`/
  `iDateFilter.ts`: AG Grid's `inRange` wire shape (`{type:'inRange', filter, filterTo}`) carries no
  `fromInclusive`/`toInclusive` signal at all — every inclusivity combination looks identical on the
  wire. Cases 039-043 (all five `inRange` cases) therefore use the `customFilters` (OQ-11) extension
  point to inject a `rangeComparisonNode` with explicit flags directly, per SEM-4's own "must NOT
  present the exact AG Grid boundary-inclusivity default as settled fact" rule — this also fixes a
  previous authoring bug where 039/042 (and, more loosely, 040/043) had nothing in their
  `agGridRequest.json`/`schema.json` to distinguish their opposite inclusivity combinations.
- **Postgres placeholder style is `$1, $2, ...` (positional), matching every other category.** Not
  `:v1, :v2, ...` named placeholders — that was a category-local inconsistency, now corrected; `$N` is
  the correct native PostgreSQL parameter syntax for the declared `"postgres"` dialect.

## Cases

| # | Directory | Proves |
|---|---|---|
| 001 | `001-eq-string/` | AR-6 `eq` on `string`, case-insensitive by default (SEM-1); legacy `condition1`/`condition2` wire form (AG-1). |
| 002 | `002-neq-string/` | AR-6 `neq` on `string`: case-insensitive (SEM-1) + NULL-inclusive negative op (SEM-2). |
| 003 | `003-eq-int64/` | AR-6 `eq` on `int64` (AR-7's typed-value matrix). |
| 004 | `004-neq-int64/` | AR-6 `neq` on `int64`, NULL-inclusive (SEM-2); legacy `condition1`/`condition2` wire form (AG-1). |
| 005 | `005-eq-boolean/` | AR-6 `eq` on `boolean`, via `customFilters` (no native wire path — see scope boundaries). |
| 006 | `006-neq-boolean/` | AR-6 `neq` on `boolean` (SEM-2 NULL-inclusive), via `customFilters`. |
| 007 | `007-eq-date/` | AR-6 `eq` on local `date` (SEM-4); legacy `condition1`/`condition2` wire form (AG-1). |
| 008 | `008-neq-date/` | AR-6 `neq` on local `date`, NULL-inclusive (SEM-2). |
| 009 | `009-gt-int64/` | AR-6 `gt` on `int64`. |
| 010 | `010-gt-double/` | AR-6 `gt` on `double`. |
| 011 | `011-gt-date/` | AR-6 `gt` on local `date` (SEM-4). |
| 012 | `012-gt-datetime/` | AR-6 `gt` on `datetime` instant, zone already resolved by the adapter (SEM-4). |
| 013 | `013-gte-int64/` | AR-6 `gte` on `int64`. |
| 014 | `014-gte-double/` | AR-6 `gte` on `double`. |
| 015 | `015-gte-date/` | AR-6 `gte` on local `date` (SEM-4). |
| 016 | `016-gte-datetime/` | AR-6 `gte` on `datetime` instant (SEM-4). |
| 017 | `017-lt-int64/` | AR-6 `lt` on `int64`. |
| 018 | `018-lt-double/` | AR-6 `lt` on `double`. |
| 019 | `019-lt-date/` | AR-6 `lt` on local `date` (SEM-4). |
| 020 | `020-lt-datetime/` | AR-6 `lt` on `datetime` instant (SEM-4). |
| 021 | `021-lte-int64/` | AR-6 `lte` on `int64`. |
| 022 | `022-lte-double/` | AR-6 `lte` on `double`. |
| 023 | `023-lte-date/` | AR-6 `lte` on local `date` (SEM-4). |
| 024 | `024-lte-datetime/` | AR-6 `lte` on `datetime` instant (SEM-4). |
| 025 | `025-contains-string/` | AR-6 `contains` on `string`: SEM-7 substring `wildcard` on Search (SEM-1 case-insensitive), SEM-7's own "Smart Watch"/"art" example. |
| 026 | `026-notcontains-string/` | AR-6 `notContains` on `string`: SEM-7 substring semantics negated, SEM-2 NULL-inclusive. |
| 027 | `027-startswith-string/` | AR-6 `startsWith` on `string`, case-insensitive anchored substring (SEM-1). |
| 028 | `028-endswith-string/` | AR-6 `endsWith` on `string`, case-insensitive anchored substring (SEM-1). |
| 029 | `029-blank-string/` | AR-6 `blank` on `string`: SEM-2's `IS NULL OR = ''` text-specific form. |
| 030 | `030-notblank-string/` | AR-6 `notBlank` on `string`: exact negation of `blank` (SEM-2). |
| 031 | `031-blank-int64/` | AR-6 `blank` on non-text `int64`: SEM-2 `IS NULL`-only form. |
| 032 | `032-notblank-int64/` | AR-6 `notBlank` on non-text `int64`, negating `blank` (SEM-2). |
| 033 | `033-blank-date/` | AR-6 `blank` on `date`: SEM-2 `IS NULL`-only form. |
| 034 | `034-notblank-date/` | AR-6 `notBlank` on `date`, negating `blank` (SEM-2). |
| 035 | `035-in-string/` | AR-6 `in` on `string` via native Set Filter, case-insensitive membership (SEM-1). |
| 036 | `036-notin-string/` | AR-6 `notIn` on `string`, NULL-inclusive (SEM-2), via `customFilters` (no native exclude wire path). |
| 037 | `037-in-int64/` | AR-6 `in` on `int64` via native Set Filter; wire values are always strings, coerced once by the adapter (SEM-5). |
| 038 | `038-notin-int64/` | AR-6 `notIn` on `int64`, NULL-inclusive (SEM-2), via `customFilters`. |
| 039 | `039-inrange-int64-inclusive-inclusive/` | AR-6 `inRange` on `int64`, both boundaries inclusive (SEM-4 explicit flags), via `customFilters` (no native inclusivity-flag wire path — see scope boundaries). |
| 040 | `040-inrange-date-inclusive-exclusive/` | AR-6 `inRange` on `date`, inclusive `from` / exclusive `to` (SEM-4), via `customFilters`. |
| 041 | `041-inrange-datetime-exclusive-inclusive/` | AR-6 `inRange` on `datetime`, exclusive `from` / inclusive `to`, no zone clause on the resolved instant (SEM-4), via `customFilters`; carries `requestContext.zone` (UTC). |
| 042 | `042-inrange-int64-exclusive-exclusive/` | AR-6 `inRange` on `int64`, both boundaries exclusive (SEM-4), via `customFilters` — same field/value pair as 039, opposite inclusivity, now distinguishable. |
| 043 | `043-inrange-date-inclusive-inclusive/` | AR-6 `inRange` on `date`, both boundaries inclusive (SEM-4), third inclusivity combination, via `customFilters`. |
| 044 | `044-true-boolean/` | AR-6 unary `true` boolean shorthand, via the Set Filter boolean-modeling convention. |
| 045 | `045-false-boolean/` | AR-6 unary `false` boolean shorthand, mirror of case 044. |
| 046 | `046-eq-decimal/` | AR-6 `eq` on `decimal` (AR-7's typed-value matrix) — the numeric-string `decimalValue` representation, previously unexercised in this category. |
| 047 | `047-gt-decimal/` | AR-6 `gt` (ordering) on `decimal`, mirror of 046 for an ordering comparison. |
