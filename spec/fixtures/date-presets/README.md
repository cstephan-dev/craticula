# `date-presets/` — AG-2 date filter presets resolved to absolute ranges

Per `spec/fixtures/README.md`'s category index, this directory proves **AG-2**: an AG Grid *date
preset* (`today`, `thisQuarter`, `last90Days`, ...) arrives on the wire as a bare `type` with no
bounds at all, and the **adapter** — never a compiler — resolves it into an absolute range against an
injected clock and zone. A compiler must never see an unresolved preset. Every case also backs
**SEM-4** (a date is resolved using *both* the field's schema-declared type and the request context's
zone) and **AR-6** (the resolved node is the closed operator set's `inRange`).

## What a preset looks like on the wire, and why the server has to resolve it

AG Grid's `ISimpleFilterModelPresetType` is part of the same flat `type` vocabulary as
`equals`/`lessThan`/`blank`, and presets are members of AG Grid's own `zeroInputTypes` set — so the
entry is exactly `{ "filterType": "date", "type": "<preset>" }`, with `dateFrom`, `dateTo`, `filter`
and `filterTo` all absent. AG Grid's SSRM documentation states the obligation directly: *"Both use the
preset `type` and leave `dateFrom` and `dateTo` as `undefined`. Your server (or custom filtering code)
must interpret the preset and apply the equivalent date range logic using its own time zone and locale
rules."*

The resolution table below is AG Grid's own published `@Spec` comment (`dateFilterHandler.ts`),
reproduced verbatim — **every range is half-open `[from, to)`**:

```
today          [startOfToday, startOfTomorrow)            thisYear     [startOfCurrentYear, startOfNextYear)
yesterday      [startOfYesterday, startOfToday)           lastYear     [startOfPreviousYear, startOfCurrentYear)
tomorrow       [startOfTomorrow, startOfDayAfterTomorrow) nextYear     [startOfNextYear, startOfYearAfterNext)
thisWeek       [startOfCurrentWeek, startOfNextWeek)      yearToDate   [startOfCurrentYear, startOfTomorrow)
lastWeek       [startOfPreviousWeek, startOfCurrentWeek)  last7Days    [startOfToday -  7 days,   startOfTomorrow)
nextWeek       [startOfNextWeek, startOfWeekAfterNext)    last30Days   [startOfToday - 30 days,   startOfTomorrow)
thisMonth      [startOfCurrentMonth, startOfNextMonth)    last90Days   [startOfToday - 90 days,   startOfTomorrow)
lastMonth      [startOfPreviousMonth, startOfCurrentMonth) last6Months [startOfToday -  6 months, startOfTomorrow)
nextMonth      [startOfNextMonth, startOfMonthAfterNext)  last12Months [startOfToday - 12 months, startOfTomorrow)
thisQuarter    [startOfCurrentQuarter, startOfNextQuarter) last24Months[startOfToday - 24 months, startOfTomorrow)
lastQuarter    [startOfPreviousQuarter, startOfCurrentQuarter)
nextQuarter    [startOfNextQuarter, startOfQuarterAfterNext)
```

The vocabulary is closed and has **22** members; cases 001-022 below are one per member, in that
order. (Some prose elsewhere says "21" — an off-by-one in the prose, not a missing preset.)

**Version applicability**: presets were introduced in AG Grid `release-36.0.0` and are absent from
v31-v35. That is inside AG-4's stated v31-v36 support range, so a v31-v35 captured payload will
legitimately contain no preset at all — worth a note when AG-4 (#1637) authors per-major payloads.

## Why `fromInclusive: true, toInclusive: false` here is **not** the forbidden guess

`spec/semantics.md` SEM-4 and this suite's own `../README.md` both forbid *inventing* AG Grid's
boundary inclusivity: "do not invent an inclusivity default and assert it as if it were the AG Grid
default". That rule is why AG Grid's **native** `inRange` has no fixture using the native wire shape
at all (the `operators/039-043` cases go through `customFilters` instead) and why the adapter rejects
a native `inRange` outright — a native `{type:"inRange", dateFrom, dateTo}` carries no inclusivity
signal whatsoever, so any value chosen for it would be fabrication.

A **preset** is the opposite situation: its inclusivity is published by AG Grid itself, on every row of
the table above, as half-open `[from, to)`. Reading a documented value is not guessing one. So every
case here pins `fromInclusive: true, toInclusive: false` on the resolved `inRange` node, and the
compiled artifacts render `>= from AND < to` (`$gte`/`$lt`, `gte`/`lt`) accordingly. Do not "correct"
this to a rejection or to an inclusive upper bound.

## The pinned clock: `requestContext.now`

Preset resolution is clock-dependent, so a fixture is not reproducible without a pinned instant. Each
case's `agGridRequest.json` therefore carries the documented `requestContext` extension with **both**
fields: `{ "zone": "<IANA id>", "now": "<ISO-8601 instant>" }` (see `../README.md`, which documents
the `now` extension alongside `zone`).

`now` is an instruction **to the conformance harness**, not a field an adapter reads off the wire: the
harness sets its injected clock to that instant before parsing. Reference implementations must not
honour a client-supplied `now` — that would let any caller redefine what "last 7 days" means. In the
C# reference adapter, the harness passes it via `AgGridParseOptions.Clock`; `requestContext.zone`, by
contrast, *is* read from the request (it takes precedence over the adapter's configured default zone).

**Cases 001-022 pin `now = 2026-08-12T14:37:00Z`, zone `UTC`.** That instant is deliberately boring on
every axis that could otherwise make a case ambiguous: 2026-08-12 is a **Wednesday** (mid-week, so the
Monday-start week boundary is unmistakably 2026-08-10 and no `thisWeek`/`lastWeek` answer coincides
with another), the **12th** of the month (mid-month, so no `lastMonth`/`nextMonth` boundary is one day
away, and the `lastNMonths` family lands on a day-of-month that exists in every month — end-of-month
clamping is exercised by unit tests instead of being baked into every fixture), **August** is the
middle month of Q3 (so `thisQuarter` = Jul-Sep is distinguishable from `thisMonth` and from
`lastQuarter`), it is far from either year boundary, and 14:37 UTC is mid-afternoon, so the UTC
calendar day is not near a rollover. 2026 is not a leap year, and none of the resolved bounds lands on
a 29 February.

Cases 023-026 each pin their **own** `now`/`zone`, chosen to make one specific axis fail loudly for an
implementation that gets it wrong — see the table.

## Backend coverage

General/structural fixtures per the suite-wide policy (`../README.md`): `postgres` + `mongo` +
`search` on every case. No dialect-specific or cross-backend-parity showcase cases live here.

## Cases

Cases 001-022 all filter `hireDate` (declared `date`) with `now = 2026-08-12T14:37:00Z`, zone `UTC`,
so the resolved local day is 2026-08-12 (Wednesday) throughout and the whole table is directly
comparable case-to-case.

| # | Directory | Preset | Resolved `[from, to)` | Proves |
|---|---|---|---|---|
| 001 | `001-today/` | `today` | `[2026-08-12, 2026-08-13)` | AG-2 the single current day; the baseline every other case varies from. |
| 002 | `002-yesterday/` | `yesterday` | `[2026-08-11, 2026-08-12)` | AG-2 the day before; upper bound is the start of today, not the end of yesterday. |
| 003 | `003-tomorrow/` | `tomorrow` | `[2026-08-13, 2026-08-14)` | AG-2 a forward-looking preset — presets are not all "recent past". |
| 004 | `004-this-week/` | `thisWeek` | `[2026-08-10, 2026-08-17)` | AG-2 week boundaries under the Monday-start policy (AG Grid's `DEFAULT_FIRST_DAY_OF_WEEK`). |
| 005 | `005-last-week/` | `lastWeek` | `[2026-08-03, 2026-08-10)` | AG-2 the previous whole week, abutting `thisWeek`'s lower bound exactly. |
| 006 | `006-next-week/` | `nextWeek` | `[2026-08-17, 2026-08-24)` | AG-2 the next whole week, abutting `thisWeek`'s upper bound exactly. |
| 007 | `007-this-month/` | `thisMonth` | `[2026-08-01, 2026-09-01)` | AG-2 calendar-month boundaries (start of next month, never "the 31st"). |
| 008 | `008-last-month/` | `lastMonth` | `[2026-07-01, 2026-08-01)` | AG-2 the previous whole calendar month. |
| 009 | `009-next-month/` | `nextMonth` | `[2026-09-01, 2026-10-01)` | AG-2 the next whole calendar month. |
| 010 | `010-this-quarter/` | `thisQuarter` | `[2026-07-01, 2026-10-01)` | AG-2 calendar quarters (Q3 = Jul-Sep), distinct from month and from year. |
| 011 | `011-last-quarter/` | `lastQuarter` | `[2026-04-01, 2026-07-01)` | AG-2 the previous quarter (Q2), abutting `thisQuarter`. |
| 012 | `012-next-quarter/` | `nextQuarter` | `[2026-10-01, 2027-01-01)` | AG-2 the next quarter (Q4) — its upper bound crosses the year boundary. |
| 013 | `013-this-year/` | `thisYear` | `[2026-01-01, 2027-01-01)` | AG-2 the whole current calendar year. |
| 014 | `014-last-year/` | `lastYear` | `[2025-01-01, 2026-01-01)` | AG-2 the previous whole calendar year. |
| 015 | `015-next-year/` | `nextYear` | `[2027-01-01, 2028-01-01)` | AG-2 the next whole calendar year. |
| 016 | `016-year-to-date/` | `yearToDate` | `[2026-01-01, 2026-08-13)` | AG-2 the one preset mixing a calendar-year lower bound with a *today*-relative upper bound. |
| 017 | `017-last-7-days/` | `last7Days` | `[2026-08-05, 2026-08-13)` | AG-2 rolling day window; the upper bound is start-of-tomorrow, so today is fully included (the window spans 8 calendar days, not 7 — that is AG Grid's own definition, not an off-by-one). |
| 018 | `018-last-30-days/` | `last30Days` | `[2026-07-13, 2026-08-13)` | AG-2 rolling 30-day window, crossing a month boundary. |
| 019 | `019-last-90-days/` | `last90Days` | `[2026-05-14, 2026-08-13)` | AG-2 rolling 90-day window, crossing three month boundaries — a fixed day count, never "3 months". |
| 020 | `020-last-6-months/` | `last6Months` | `[2026-02-12, 2026-08-13)` | AG-2 rolling **calendar-month** window: 6 months back from 2026-08-12 is 2026-02-12, which is 181 days — proving the `lastNMonths` family is month arithmetic, not `N * 30` days. |
| 021 | `021-last-12-months/` | `last12Months` | `[2025-08-12, 2026-08-13)` | AG-2 rolling 12-month window, crossing a year boundary. |
| 022 | `022-last-24-months/` | `last24Months` | `[2024-08-12, 2026-08-13)` | AG-2 rolling 24-month window, spanning two year boundaries (and over the 2024 leap year). |

### Axis-variation cases (023-026)

Cases 001-022 hold field type and zone constant so the resolution table itself is the only variable.
These four hold the *preset* nearly constant and vary the other two axes SEM-4 names, each chosen so a
plausible wrong implementation produces a visibly different answer rather than a subtly different one.

| # | Directory | Pinned `now` / zone | Resolved `[from, to)` | Proves |
|---|---|---|---|---|
| 023 | `023-today-datetime-field-utc/` | `2026-08-12T14:37:00Z` / `UTC` | `[2026-08-12T00:00:00Z, 2026-08-13T00:00:00Z)` | SEM-4 the field's declared type selects the `typedValue`: `createdAt` is `datetime`, so both bounds are `datetimeValue` instants (Mongo `$date`, not a string), not `dateValue`s. Same preset and clock as 001 — the *only* difference is the schema. |
| 024 | `024-today-datetime-field-paris-zone/` | `2026-08-12T23:30:00Z` / `Europe/Paris` | `[2026-08-12T22:00:00Z, 2026-08-13T22:00:00Z)` | SEM-4 both zone effects at once: in Paris that instant is already 2026-08-13T01:30 local, so "today" is the **13th** (not the 12th a UTC implementation picks), and the day's instants are `22:00Z`-based (not midnight-UTC). |
| 025 | `025-this-week-date-field-paris-zone-day-shift/` | `2026-08-16T23:30:00Z` / `Europe/Paris` | `[2026-08-17, 2026-08-24)` | SEM-4 the zone matters for a **`date`**-typed field too, where no instant is ever built: Sunday in UTC but Monday in Paris, so the week resolves to `[08-17, 08-24)` where a UTC implementation answers `[08-10, 08-17)` — two ranges with zero overlap. |
| 026 | `026-this-week-datetime-field-paris-dst-spring-forward/` | `2026-03-25T12:00:00Z` / `Europe/Paris` | `[2026-03-22T23:00:00Z, 2026-03-29T22:00:00Z)` | SEM-4 DST-correct instants: the week contains Europe's spring-forward (2026-03-29), so the two bounds sit at **different** UTC offsets (+01:00 and +02:00). Reading the zone offset once and applying it to both bounds — the most common way to get this wrong — puts the upper bound an hour out. |

### Zone-rejection cases (027-028)

Both carry `expected/rejection.json` (`requestParsing` / `malformed-request`) instead of backend
artifacts, and both are byte-identical to `001-today` apart from their `requestContext.zone`. They
exist because a bad zone does not fail loudly on its own: every preset resolves its day boundaries in
the request's zone, so getting the zone wrong yields a *plausible range shifted by an offset* — which
for `today` can be a different calendar day entirely (025 shows two such answers with zero overlap).

| # | Directory | `zone` | Proves |
|---|---|---|---|
| 027 | `027-unresolvable-zone-rejected/` | `Mars/Olympus_Mons` | IANA-**shaped** but resolvable by no tzdata release, so a shape check alone accepts it — an implementation only catches this by attempting resolution and failing loudly instead of falling back to its configured default zone. |
| 028 | `028-non-iana-zone-alias-rejected/` | `localtime` | A tzdata **artifact**, not a zone: it resolves successfully AND satisfies the IANA shape, yet means "whatever zone this server is configured for" — so a client could silently relocate every preset boundary into the server's zone, which SEM-4 forbids. `Factory` and `posixrules` behave identically and must be rejected the same way. |

## Notes on artifact construction

- **Every case's IR is one `comparison` node**: `{ op: "inRange", field, from, to, fromInclusive:
  true, toInclusive: false }`. There is no `customFilters` anywhere in this category — unlike
  `operators/039-043`, a preset *does* have a native wire path, and exercising it is the whole point.
- **Postgres**: `WHERE (<col> >= $1 AND <col> < $2)`, positional `$N` placeholders, `ORDER BY id ASC
  NULLS LAST`, `LIMIT $3 OFFSET $4` with `params` `[from, to, 100, 0]`. The `>=`/`<` pair is the
  literal rendering of the node's explicit inclusivity flags — never `BETWEEN`, whose SQL semantics
  are inclusive on both ends and would silently pull in a whole extra day (SEM-4's own third named
  failure mode).
- **MongoDB**: `$match` with `$gte`/`$lt`, then SEM-3's null-bucket emulation for the `id` sort (one
  `__idNullBucket` `$addFields`, a `$sort` on `(bucket, id)`, and the mandatory `$unset` dropping the
  bucket again), then `$skip`/`$limit`. Bare-array pipeline form throughout: no case here filters or
  sorts on a `string` field, so no query-level `collation` is needed. Per SEM-4's pinned
  representation, a `dateValue` bound is a **plain ISO date string** (lexicographic order on
  `YYYY-MM-DD` already matches chronological order, so no BSON `Date` promotion and no "midnight in
  which zone?" question arises), while a `datetimeValue` bound is extended-JSON `{"$date": "...Z"}`.
- **Search (OpenSearch/Elasticsearch, shared per OQ-7)**: a native `range` query with `gte`/`lt`,
  `format: "strict_date"` for `dateValue` bounds and `"strict_date_optional_time"` for
  `datetimeValue` bounds, plus the `sort` array's `missing: "_last"` for SEM-3 and `from`/`size` for
  paging.
- **No zone clause anywhere, in any backend.** By the time a bound reaches a compiler it is already an
  absolute instant (or a zone-less local date). `datetimeValue.zone` is provenance only, so no
  artifact contains `AT TIME ZONE`, `$dateTrunc`-with-timezone, or an ES `time_zone` parameter — the
  whole point of resolving presets in the adapter is that compilers stay date-math-free.
- **The two schema-dependent rejections are unit-tested, not fixtured.** A preset against a field that
  is neither `date` nor `datetime`, and a preset parsed with no schema available, are both clean
  `requestParsing` / `malformed-request` rejections, but they are covered by the reference adapter's
  own tests rather than by fixtures here: their outcome is a property of the *adapter's configuration*
  (which `GridSchema` it was given), not of the wire request, so fixturing them would make the result
  depend on how the harness was set up. **Zone rejections are the opposite** and *are* fixtured (027,
  028): an id that no host can resolve, or that names a tzdata artifact rather than a zone, is a pure
  wire-shape property with a host-independent outcome that every port can replay.
