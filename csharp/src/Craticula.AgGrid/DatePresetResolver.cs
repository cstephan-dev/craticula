using Craticula.Core.Ir;
using FieldType = Craticula.Core.Schema.FieldType;

namespace Craticula.AgGrid;

/// <summary>
/// The half-open <c>[From, To)</c> calendar-date range one AG Grid date preset resolves to, expressed
/// as two LOCAL dates in the request's zone. Both bounds are always a start-of-day: every row in the
/// table AG Grid publishes is <c>[startOfX, startOfY)</c>, so a date pair is a complete, lossless
/// representation — turning those local midnights into absolute instants (only needed for a
/// <c>datetime</c>-typed field) is <see cref="DatePresetResolver.StartOfDayInstant"/>'s separate,
/// zone-aware step.
/// </summary>
public readonly record struct DatePresetRange(DateOnly From, DateOnly To);

/// <summary>
/// AG-2: resolves an AG Grid <em>date filter preset</em> (<c>today</c>, <c>thisQuarter</c>,
/// <c>last90Days</c>, ...) into an absolute date range, entirely inside the adapter — a compiler must
/// never see an unresolved preset.
///
/// <para><b>Why this exists at all.</b> A preset arrives in the very same <c>type</c> slot as
/// <c>equals</c>/<c>lessThan</c>/<c>blank</c> (AG Grid's <c>ISimpleFilterModelType</c> union is one
/// flat vocabulary), with <c>dateFrom</c> and <c>dateTo</c> both <c>undefined</c> — presets are in AG
/// Grid's own <c>zeroInputTypes</c> set, so no bound reaches the wire. AG Grid's SSRM documentation
/// states the obligation explicitly: "Your server (or custom filtering code) must interpret the
/// preset and apply the equivalent date range logic using its own time zone and locale rules." The
/// resolution table below is copied from AG Grid's own <c>@Spec</c> comment in
/// <c>dateFilterHandler.ts</c>, not inferred:</para>
/// <code>
/// today          [startOfToday, startOfTomorrow)          thisYear     [startOfCurrentYear, startOfNextYear)
/// yesterday      [startOfYesterday, startOfToday)         lastYear     [startOfPreviousYear, startOfCurrentYear)
/// tomorrow       [startOfTomorrow, startOfDayAfterTom.)   nextYear     [startOfNextYear, startOfYearAfterNext)
/// thisWeek       [startOfCurrentWeek, startOfNextWeek)    yearToDate   [startOfCurrentYear, startOfTomorrow)
/// lastWeek       [startOfPreviousWeek, startOfCurrentWeek) last7Days   [startOfToday -  7 days,   startOfTomorrow)
/// nextWeek       [startOfNextWeek, startOfWeekAfterNext)  last30Days   [startOfToday - 30 days,   startOfTomorrow)
/// thisMonth      [startOfCurrentMonth, startOfNextMonth)  last90Days   [startOfToday - 90 days,   startOfTomorrow)
/// lastMonth      [startOfPreviousMonth, startOfCurrentMon) last6Months [startOfToday -  6 months, startOfTomorrow)
/// nextMonth      [startOfNextMonth, startOfMonthAfterNext) last12Months[startOfToday - 12 months, startOfTomorrow)
/// thisQuarter    [startOfCurrentQuarter, startOfNextQ.)   last24Months [startOfToday - 24 months, startOfTomorrow)
/// lastQuarter    [startOfPreviousQuarter, startOfCurrentQ.)
/// nextQuarter    [startOfNextQuarter, startOfQuarterAfterNext)
/// </code>
///
/// <para><b>Why emitting an <c>inRange</c> with <c>fromInclusive: true, toInclusive: false</c> does
/// NOT violate SEM-4 / <c>spec/fixtures/README.md</c> — do not "fix" this back into a rejection.</b>
/// Both documents forbid <em>guessing</em> AG Grid's boundary inclusivity: SEM-4 says the exact
/// default AG Grid's simple date filter uses for a native <c>inRange</c> is "verify at implementation
/// time and freeze", and the fixtures README says "do not invent an inclusivity default and assert it
/// as if it were the AG Grid default". That is why #1635 rejects a native <c>inRange</c> outright — a
/// native <c>{type:'inRange', dateFrom, dateTo}</c> genuinely carries no inclusivity signal, so any
/// value the adapter picked would be invention. A PRESET is the opposite case: its inclusivity is
/// published by AG Grid itself, in the spec table above, as half-open <c>[from, to)</c> on every one
/// of the 22 rows. Reading a documented value is not guessing one. The prohibition is on inventing
/// facts, not on using them — and rejecting presets "for consistency" would break AG-2's Definition
/// of Done (a preset would then be unusable through the whole product) to protect against a risk that
/// does not exist here.</para>
///
/// <para><b>Why the resolved bounds are typed via the schema (<see cref="ToTypedBounds"/>), and why
/// that does not weaken #1635's "the adapter never coerces client values" rule.</b> SEM-4 requires a
/// date to be resolved using "both the field's schema-declared type and the request context's zone".
/// #1635's rule is about values that arrive FROM the client: those are carried through verbatim as a
/// <see cref="StringValue"/> so SEM-5's single coercion layer downstream stays the only place a
/// client string is interpreted. A preset carries no client value at all — the adapter <em>generates</em>
/// both bounds from a clock. There is nothing to pass through, so "pass it through untouched" has no
/// meaning here, and the never-coerce rule is untouched for every other path. Emitting a
/// <see cref="StringValue"/> anyway would be actively wrong: a bare <c>"2026-08-13"</c> later coerced
/// toward a <c>datetime</c> field becomes midnight-UTC, which is exactly the silent local-date to
/// instant promotion SEM-4's own "must NOT" list forbids — and it would be wrong by up to a full day
/// for any non-UTC zone.</para>
/// </summary>
public static class DatePresetResolver
{
    /// <summary>
    /// AG Grid's closed preset vocabulary (<c>ISimpleFilterModelPresetType</c>), in the source's own
    /// declaration order. Exposed so a host can, for example, surface the supported set in an error
    /// message without re-deriving it — and so a test can assert the table below covers every member
    /// rather than only the ones someone remembered to write a case for.
    ///
    /// <para>Note on the count: the vocabulary has <b>22</b> members. Some prose (including this
    /// story's own planning notes) says "21" — that is an off-by-one in the prose, not a missing
    /// member; the enumerated list and AG Grid's <c>@Spec</c> table both have 22 rows.</para>
    /// </summary>
    public static IReadOnlyList<string> PresetNames { get; } =
    [
        "today", "yesterday", "tomorrow",
        "thisWeek", "lastWeek", "nextWeek",
        "thisMonth", "lastMonth", "nextMonth",
        "thisQuarter", "lastQuarter", "nextQuarter",
        "thisYear", "lastYear", "nextYear",
        "yearToDate",
        "last7Days", "last30Days", "last90Days",
        "last6Months", "last12Months", "last24Months"
    ];

    private static readonly HashSet<string> PresetNameSet = new(PresetNames, StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="type"/> is one of AG Grid's date presets. Ordinal, case-SENSITIVE:
    /// the wire vocabulary is a TypeScript string-literal union, so <c>"Today"</c> is not a preset AG
    /// Grid can ever send, and accepting it would mean silently repairing a request no real grid
    /// produced (the same fail-closed stance the rest of this adapter's <c>type</c> dispatch takes).
    /// </summary>
    public static bool IsPreset(string type) => PresetNameSet.Contains(type);

    /// <summary>
    /// Resolves <paramref name="preset"/> against the instant <paramref name="now"/>, as observed in
    /// <paramref name="zone"/>, using <paramref name="firstDayOfWeek"/> for the week-based presets.
    /// Pure: same inputs, same output, on any machine and under any <c>TZ</c> environment variable.
    /// Throws <see cref="ArgumentOutOfRangeException"/> for a non-preset name — callers dispatching
    /// on untrusted wire input should use <see cref="TryResolve"/> instead.
    /// </summary>
    public static DatePresetRange Resolve(string preset, DateTimeOffset now, TimeZoneInfo zone, DayOfWeek firstDayOfWeek) =>
        TryResolve(preset, now, zone, firstDayOfWeek, out var range)
            ? range
            : throw new ArgumentOutOfRangeException(
                nameof(preset), preset, $"Not an AG Grid date preset (expected one of: {string.Join(", ", PresetNames)}).");

    /// <summary>
    /// <see cref="Resolve"/>'s non-throwing form — returns <see langword="false"/> (leaving
    /// <paramref name="range"/> at its default) when <paramref name="preset"/> is not a preset name
    /// at all, which is how <see cref="AgGridRequestParser"/> tells "AG-2 preset" apart from
    /// "genuinely unknown condition type" in a single lookup.
    /// </summary>
    public static bool TryResolve(
        string preset, DateTimeOffset now, TimeZoneInfo zone, DayOfWeek firstDayOfWeek, out DatePresetRange range)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(zone);

        if (!IsPreset(preset))
        {
            range = default;
            return false;
        }

        // SEM-4: every boundary is computed from the calendar date "now" falls on IN THE REQUEST'S
        // ZONE, never in UTC and never in the server process's local zone — an instant three hours
        // before UTC midnight is already "tomorrow" in Tokyo and still "yesterday" in Los Angeles, and
        // "today" has to mean what the user's grid shows.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var tomorrow = today.AddDays(1);
        var startOfWeek = StartOfWeek(today, firstDayOfWeek);
        var startOfMonth = new DateOnly(today.Year, today.Month, 1);
        var startOfQuarter = new DateOnly(today.Year, (((today.Month - 1) / 3) * 3) + 1, 1);
        var startOfYear = new DateOnly(today.Year, 1, 1);

        range = preset switch
        {
            "today" => new DatePresetRange(today, tomorrow),
            "yesterday" => new DatePresetRange(today.AddDays(-1), today),
            "tomorrow" => new DatePresetRange(tomorrow, today.AddDays(2)),

            "thisWeek" => new DatePresetRange(startOfWeek, startOfWeek.AddDays(7)),
            "lastWeek" => new DatePresetRange(startOfWeek.AddDays(-7), startOfWeek),
            "nextWeek" => new DatePresetRange(startOfWeek.AddDays(7), startOfWeek.AddDays(14)),

            "thisMonth" => new DatePresetRange(startOfMonth, startOfMonth.AddMonths(1)),
            "lastMonth" => new DatePresetRange(startOfMonth.AddMonths(-1), startOfMonth),
            "nextMonth" => new DatePresetRange(startOfMonth.AddMonths(1), startOfMonth.AddMonths(2)),

            "thisQuarter" => new DatePresetRange(startOfQuarter, startOfQuarter.AddMonths(3)),
            "lastQuarter" => new DatePresetRange(startOfQuarter.AddMonths(-3), startOfQuarter),
            "nextQuarter" => new DatePresetRange(startOfQuarter.AddMonths(3), startOfQuarter.AddMonths(6)),

            // Built from (year +/- n, 1, 1) rather than AddYears, so nothing here can depend on
            // leap-day clamping: a year always starts on the 1st of January.
            "thisYear" => new DatePresetRange(startOfYear, new DateOnly(today.Year + 1, 1, 1)),
            "lastYear" => new DatePresetRange(new DateOnly(today.Year - 1, 1, 1), startOfYear),
            "nextYear" => new DatePresetRange(new DateOnly(today.Year + 1, 1, 1), new DateOnly(today.Year + 2, 1, 1)),
            "yearToDate" => new DatePresetRange(startOfYear, tomorrow),

            // The rolling "last N" family: N days/months back from the start of TODAY, up to (but
            // excluding) the start of TOMORROW — i.e. the window always includes all of today, which
            // is why the upper bound is startOfTomorrow rather than startOfToday.
            "last7Days" => new DatePresetRange(today.AddDays(-7), tomorrow),
            "last30Days" => new DatePresetRange(today.AddDays(-30), tomorrow),
            "last90Days" => new DatePresetRange(today.AddDays(-90), tomorrow),

            // DateOnly.AddMonths clamps to the target month's last day: 2026-08-31 minus 6 months is
            // 2026-02-28. Do not "simplify" this into a day-count subtraction.
            //
            // KNOWN, DELIBERATE DIVERGENCE FROM THE AG GRID CLIENT on month-end days. AG Grid's own
            // implementation (dateFilterHandler.ts) does raw JS Date mutation — `setFullYear(y-1)`
            // then `setMonth(m+6)` — and JS `setMonth` OVERFLOWS rather than clamping, so the same
            // 2026-08-31 yields 2026-03-03 client-side. This code deliberately implements the
            // published `@Spec` table (which reads as calendar arithmetic) rather than reproducing the
            // client's overflow, i.e. the client is buggy against its own documented spec.
            //
            // Reachable only when "today" falls on day 29-31 of a month; measured across 2024-2028,
            // 96 of 5280 (day, preset) combinations differ, spread over last6Months/last12Months/
            // last24Months and — via the same JS overflow in their own start-of-month arithmetic —
            // nextMonth/thisQuarter/nextQuarter/lastQuarter. The largest divergence is a full quarter
            // (now = 2026-03-31: here [Apr 1, Jul 1), client [Jul 1, Oct 1)).
            //
            // Consequence to know when triaging: on a month-end day, a grid's client-side row model
            // and this server-side compilation can legitimately disagree about which rows a preset
            // matches. That is a conscious choice of the spec table as the oracle, not an oversight —
            // do not "fix" it toward the client without re-pinning the oracle first.
            "last6Months" => new DatePresetRange(today.AddMonths(-6), tomorrow),
            "last12Months" => new DatePresetRange(today.AddMonths(-12), tomorrow),
            "last24Months" => new DatePresetRange(today.AddMonths(-24), tomorrow),

            _ => throw new InvalidOperationException($"Preset '{preset}' is in the vocabulary but has no resolution rule.")
        };

        return true;
    }

    /// <summary>
    /// Converts a resolved <paramref name="range"/> into the pair of <see cref="TypedValue"/> bounds a
    /// <see cref="ComparisonNode"/> carries, per the field's schema-declared
    /// <paramref name="fieldType"/> (SEM-4 — see this class's own doc for why the schema, not the
    /// wire, decides this):
    /// <list type="bullet">
    /// <item><see cref="FieldType.Date"/> -&gt; two <see cref="DateValue"/>s, the local calendar dates
    /// verbatim. A local date has no zone by definition, so no instant is ever manufactured.</item>
    /// <item><see cref="FieldType.Datetime"/> -&gt; two <see cref="DatetimeValue"/>s, each the absolute
    /// instant at which that local date BEGINS in <paramref name="zone"/> (see
    /// <see cref="StartOfDayInstant"/>), normalized to UTC (<c>...Z</c>) since SEM-4 pins
    /// <c>datetimeValue.value</c> as an absolute instant and <c>zone</c> as provenance only.</item>
    /// </list>
    /// Any other <paramref name="fieldType"/> throws <see cref="ArgumentOutOfRangeException"/> — a
    /// date preset on an <c>int64</c>/<c>string</c>/... column is a request the adapter must reject,
    /// not one it should coerce into some plausible-looking comparison.
    /// </summary>
    public static (TypedValue From, TypedValue To) ToTypedBounds(DatePresetRange range, FieldType fieldType, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        return fieldType switch
        {
            FieldType.Date => (Date(range.From), Date(range.To)),
            FieldType.Datetime => (Datetime(range.From), Datetime(range.To)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(fieldType), fieldType, "A date preset can only be resolved against a 'date' or 'datetime' field.")
        };

        static DateValue Date(DateOnly date) => new() { Value = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) };

        DatetimeValue Datetime(DateOnly date) => new()
        {
            Value = StartOfDayInstant(date, zone).UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            Zone = zone.Id
        };
    }

    /// <summary>
    /// The absolute instant at which the local calendar day <paramref name="localDate"/> begins in
    /// <paramref name="zone"/> — i.e. the earliest instant whose local time falls on that date.
    ///
    /// <para><b>Both DST edge cases are handled deliberately, because "local midnight" is not always a
    /// real, unique instant:</b></para>
    /// <list type="bullet">
    /// <item><b>Spring-forward gap</b> — in zones that transition AT midnight (America/Santiago,
    /// America/Sao_Paulo historically, Asia/Beirut, ...), local 00:00 simply never happens on the
    /// transition day: the clock jumps 23:59:59 -&gt; 01:00:00. The day's true start is the transition
    /// instant itself. <see cref="TimeZoneInfo.GetUtcOffset(System.DateTime)"/> returns the zone's
    /// STANDARD (pre-transition) offset for an invalid local time, so <c>local - offset</c> lands
    /// exactly on that transition instant — which is also what a browser's
    /// <c>new Date(y, m, d, 0, 0, 0)</c> produces, so the server agrees with the grid.</item>
    /// <item><b>Fall-back overlap</b> — in zones whose clocks go back THROUGH midnight
    /// (America/Havana, Atlantic/Azores, Asia/Gaza, ...), local 00:00 happens twice. The day starts at
    /// the FIRST occurrence, which is the one with the LARGER UTC offset (still on daylight time);
    /// <see cref="TimeZoneInfo.GetUtcOffset(System.DateTime)"/> would pick the standard-time offset —
    /// the second occurrence — and silently drop the first hour of the day out of every
    /// <c>[startOfDay, ...)</c> range, so the ambiguous case is resolved explicitly here.</item>
    /// </list>
    /// Getting either wrong costs exactly one hour of rows, once or twice a year, in a subset of
    /// zones — the kind of bug that is never reproduced from a bug report, hence the explicit handling
    /// rather than a bare <see cref="TimeZoneInfo.ConvertTimeToUtc(System.DateTime, TimeZoneInfo)"/>
    /// (which additionally THROWS on the gap case rather than resolving it).
    /// </summary>
    public static DateTimeOffset StartOfDayInstant(DateOnly localDate, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var localMidnight = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        if (zone.IsAmbiguousTime(localMidnight))
        {
            return new DateTimeOffset(localMidnight, zone.GetAmbiguousTimeOffsets(localMidnight).Max());
        }

        return new DateTimeOffset(localMidnight, zone.GetUtcOffset(localMidnight));
    }

    /// <summary>
    /// The start of the week <paramref name="date"/> falls in, per
    /// <paramref name="firstDayOfWeek"/> — always at most 6 days back, never forward.
    /// </summary>
    private static DateOnly StartOfWeek(DateOnly date, DayOfWeek firstDayOfWeek) =>
        date.AddDays(-(((int)date.DayOfWeek - (int)firstDayOfWeek + 7) % 7));
}
