using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Hand-built coverage of <see cref="AgGridRequestParser"/>'s AG-2 date-preset path (#1636): the wire
/// dispatch, the emitted <c>inRange</c> node shape, the schema-driven choice between
/// <see cref="DateValue"/> and <see cref="DatetimeValue"/> bounds, the <c>requestContext.zone</c>
/// precedence rule, and every clean rejection. Range arithmetic itself lives in
/// <see cref="DatePresetResolverTests"/>; this class is about wiring, not about the table.
/// </summary>
public class AgGridRequestParserDatePresetTests
{
    private const string PinnedNow = "2026-08-12T14:37:00Z";

    private static string Request(string field, string presetType, string? requestContextJson = null) => $$"""
        {
          "startRow": 0, "endRow": 100,
          "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
          "filterModel": { "{{field}}": { "filterType": "date", "type": "{{presetType}}" } },
          "sortModel": []{{(requestContextJson is null ? "" : ",\n  \"requestContext\": " + requestContextJson)}}
        }
        """;

    private static GridSchema SchemaWith(string field, FieldType type) => new()
    {
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = new() { Type = FieldType.Int64 },
            [field] = new() { Type = type }
        }
    };

    private static AgGridParseOptions Options(
        string field = "hireDate",
        FieldType fieldType = FieldType.Date,
        string now = PinnedNow,
        TimeZoneInfo? defaultZone = null,
        DayOfWeek firstDayOfWeek = DayOfWeek.Monday) => new()
    {
        Clock = FixedClock.At(now),
        DefaultZone = defaultZone ?? TimeZoneInfo.Utc,
        FirstDayOfWeek = firstDayOfWeek,
        Schema = SchemaWith(field, fieldType)
    };

    private static ComparisonNode ParsePreset(string presetType, AgGridParseOptions options, string field = "hireDate") =>
        Assert.IsType<ComparisonNode>(AgGridRequestParser.Parse(Request(field, presetType), options).NativeFilter);

    // ---- The emitted node shape -------------------------------------------------------------------

    [Fact]
    public void A_Preset_Becomes_A_Half_Open_InRange_With_Both_Inclusivity_Flags_Explicit()
    {
        var comparison = ParsePreset("today", Options());

        Assert.Equal("hireDate", comparison.Field);
        Assert.Equal(ComparisonOperator.InRange, comparison.Op);

        // [from, to) — read from AG Grid's own published preset spec table, NOT a guessed default for
        // its native inRange (which this adapter still rejects outright, see the test below).
        Assert.True(comparison.FromInclusive);
        Assert.False(comparison.ToInclusive);

        Assert.Equal("2026-08-12", Assert.IsType<DateValue>(comparison.From).Value);
        Assert.Equal("2026-08-13", Assert.IsType<DateValue>(comparison.To).Value);

        // A range node carries nothing in the binary/set slots (ir.schema.json's 4-way oneOf).
        Assert.Null(comparison.Value);
        Assert.Null(comparison.Values);
    }

    [Fact]
    public void A_Native_InRange_Is_Still_Rejected_Even_Though_Presets_Now_Produce_One()
    {
        // The distinction #1636 rests on: a preset's inclusivity is documented upstream, a native
        // inRange's is not. Supporting the former must not quietly open the latter.
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": { "hireDate": { "filterType": "date", "type": "inRange", "dateFrom": "2026-01-01", "dateTo": "2026-12-31" } },
              "sortModel": []
            }
            """;

        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(request, Options()));

        Assert.Equal("unknown-operator", ex.Reason);
    }

    [Theory]
    [InlineData("today", "2026-08-12", "2026-08-13")]
    [InlineData("thisWeek", "2026-08-10", "2026-08-17")]
    [InlineData("thisQuarter", "2026-07-01", "2026-10-01")]
    [InlineData("last6Months", "2026-02-12", "2026-08-13")]
    [InlineData("yearToDate", "2026-01-01", "2026-08-13")]
    public void Representative_Presets_Reach_The_Resolver_Through_The_Wire_Dispatch(
        string presetType, string from, string to)
    {
        var comparison = ParsePreset(presetType, Options());

        Assert.Equal(from, Assert.IsType<DateValue>(comparison.From).Value);
        Assert.Equal(to, Assert.IsType<DateValue>(comparison.To).Value);
    }

    [Fact]
    public void Every_Preset_In_The_Vocabulary_Parses_End_To_End_Through_The_Wire()
    {
        // Parse() runs FilterNodeValidator on its own output, so this also proves every emitted bound
        // satisfies ir.schema.json's dateValue pattern for every preset, not just the sampled ones.
        foreach (var preset in DatePresetResolver.PresetNames)
        {
            var comparison = ParsePreset(preset, Options());

            Assert.Equal(ComparisonOperator.InRange, comparison.Op);
            Assert.IsType<DateValue>(comparison.From);
            Assert.IsType<DateValue>(comparison.To);
        }
    }

    // ---- Schema-driven typing (SEM-4) --------------------------------------------------------------

    [Fact]
    public void A_Datetime_Field_Gets_Instant_Bounds_Carrying_Their_Resolution_Zone()
    {
        var comparison = ParsePreset("today", Options("createdAt", FieldType.Datetime), field: "createdAt");

        var from = Assert.IsType<DatetimeValue>(comparison.From);
        var to = Assert.IsType<DatetimeValue>(comparison.To);

        Assert.Equal("2026-08-12T00:00:00Z", from.Value);
        Assert.Equal("2026-08-13T00:00:00Z", to.Value);
        Assert.Equal("UTC", from.Zone);
        Assert.Equal("UTC", to.Zone);
    }

    [Fact]
    public void The_Field_Type_Alone_Decides_The_TypedValue_Variant_Same_Wire_Same_Clock()
    {
        // Identical wire request and identical clock; only schema.fields[x].type differs. This is what
        // "resolve using BOTH the declared type and the zone" (SEM-4) means in practice.
        var asDate = ParsePreset("today", Options("when", FieldType.Date), field: "when");
        var asDatetime = ParsePreset("today", Options("when", FieldType.Datetime), field: "when");

        Assert.IsType<DateValue>(asDate.From);
        Assert.IsType<DatetimeValue>(asDatetime.From);
    }

    [Fact]
    public void A_Preset_Never_Emits_A_StringValue_Bound()
    {
        // #1635's "the adapter never coerces client values" rule deliberately does NOT apply here: a
        // preset carries no client value at all, the adapter generates both bounds. Emitting a bare
        // StringValue would let SEM-5 later promote "2026-08-12" to midnight-UTC against a datetime
        // column — precisely SEM-4's forbidden silent local-date-to-instant promotion.
        foreach (var fieldType in new[] { FieldType.Date, FieldType.Datetime })
        {
            var comparison = ParsePreset("last7Days", Options("when", fieldType), field: "when");

            Assert.IsNotType<StringValue>(comparison.From);
            Assert.IsNotType<StringValue>(comparison.To);
        }
    }

    // ---- Zone precedence ----------------------------------------------------------------------------

    [Fact]
    public void RequestContext_Zone_Takes_Precedence_Over_The_Configured_Default_Zone()
    {
        var request = Request("createdAt", "today", """{ "zone": "Europe/Paris" }""");
        var options = Options("createdAt", FieldType.Datetime, now: "2026-08-12T23:30:00Z");

        var comparison = Assert.IsType<ComparisonNode>(AgGridRequestParser.Parse(request, options).NativeFilter);
        var from = Assert.IsType<DatetimeValue>(comparison.From);

        // 23:30Z is already 2026-08-13T01:30 in Paris, so "today" is the 13th, starting at 22:00Z.
        Assert.Equal("2026-08-12T22:00:00Z", from.Value);
        Assert.Equal("Europe/Paris", from.Zone);
    }

    [Fact]
    public void The_Configured_Default_Zone_Is_Used_When_The_Request_Declares_None()
    {
        var options = Options("createdAt", FieldType.Datetime, now: "2026-08-12T23:30:00Z",
            defaultZone: TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris"));

        var comparison = ParsePreset("today", options, field: "createdAt");

        Assert.Equal("2026-08-12T22:00:00Z", Assert.IsType<DatetimeValue>(comparison.From).Value);
    }

    [Fact]
    public void The_Zone_Also_Shifts_A_Date_Typed_Field_Where_No_Instant_Is_Involved()
    {
        var request = Request("hireDate", "thisWeek", """{ "zone": "Europe/Paris" }""");
        var options = Options(now: "2026-08-16T23:30:00Z");

        var comparison = Assert.IsType<ComparisonNode>(AgGridRequestParser.Parse(request, options).NativeFilter);

        Assert.Equal("2026-08-17", Assert.IsType<DateValue>(comparison.From).Value);
        Assert.Equal("2026-08-24", Assert.IsType<DateValue>(comparison.To).Value);
    }

    [Fact]
    public void RequestContext_Now_Is_Ignored_The_Injected_Clock_Is_Authoritative()
    {
        // The fixture suite pins requestContext.now so a preset case is reproducible, but that is an
        // instruction to the harness, not a field this parser reads: honouring a client-supplied "now"
        // would let any caller redefine what "today" means.
        var request = Request("hireDate", "today", """{ "zone": "UTC", "now": "2030-01-01T00:00:00Z" }""");

        var comparison = Assert.IsType<ComparisonNode>(AgGridRequestParser.Parse(request, Options()).NativeFilter);

        Assert.Equal("2026-08-12", Assert.IsType<DateValue>(comparison.From).Value);
    }

    [Fact]
    public void The_First_Day_Of_Week_Policy_Is_Configurable_On_The_Options()
    {
        Assert.Equal("2026-08-10",
            Assert.IsType<DateValue>(ParsePreset("thisWeek", Options(firstDayOfWeek: DayOfWeek.Monday)).From).Value);
        Assert.Equal("2026-08-09",
            Assert.IsType<DateValue>(ParsePreset("thisWeek", Options(firstDayOfWeek: DayOfWeek.Sunday)).From).Value);
    }

    // ---- Presets in composite wire forms -----------------------------------------------------------

    [Fact]
    public void A_Preset_Works_Inside_A_Combined_Conditions_Array()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": {
                "hireDate": {
                  "filterType": "date", "operator": "OR",
                  "conditions": [
                    { "filterType": "date", "type": "today" },
                    { "filterType": "date", "type": "lessThan", "dateFrom": "2020-01-01" }
                  ]
                }
              },
              "sortModel": []
            }
            """;

        var orNode = Assert.IsType<OrNode>(AgGridRequestParser.Parse(request, Options()).NativeFilter);

        Assert.Equal(ComparisonOperator.InRange, Assert.IsType<ComparisonNode>(orNode.Nodes[0]).Op);
        Assert.Equal(ComparisonOperator.Lt, Assert.IsType<ComparisonNode>(orNode.Nodes[1]).Op);
    }

    [Fact]
    public void A_Preset_Works_Inside_A_Multi_Filter_Slot_And_A_Legacy_Condition_Pair()
    {
        var multi = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": {
                "hireDate": {
                  "filterType": "multi",
                  "filterModels": [null, { "filterType": "date", "type": "thisMonth" }]
                }
              },
              "sortModel": []
            }
            """;

        Assert.Equal("2026-08-01",
            Assert.IsType<DateValue>(Assert.IsType<ComparisonNode>(AgGridRequestParser.Parse(multi, Options()).NativeFilter).From).Value);

        var legacy = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": {
                "hireDate": {
                  "filterType": "date", "operator": "AND",
                  "condition1": { "filterType": "date", "type": "thisYear" },
                  "condition2": { "filterType": "date", "type": "notBlank" }
                }
              },
              "sortModel": []
            }
            """;

        var andNode = Assert.IsType<AndNode>(AgGridRequestParser.Parse(legacy, Options()).NativeFilter);
        Assert.Equal(ComparisonOperator.InRange, Assert.IsType<ComparisonNode>(andNode.Nodes[0]).Op);
        Assert.Equal(ComparisonOperator.NotBlank, Assert.IsType<ComparisonNode>(andNode.Nodes[1]).Op);
    }

    // ---- Rejections ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(FieldType.String)]
    [InlineData(FieldType.Int64)]
    [InlineData(FieldType.Decimal)]
    [InlineData(FieldType.Double)]
    [InlineData(FieldType.Boolean)]
    public void A_Preset_On_A_Field_That_Is_Neither_Date_Nor_Datetime_Is_A_Clean_Rejection(FieldType fieldType)
    {
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => ParsePreset("today", Options("hireDate", fieldType)));

        Assert.Equal("malformed-request", ex.Reason);
        Assert.Contains("today", ex.Message, StringComparison.Ordinal);
        Assert.Contains(fieldType.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Preset_With_No_Schema_Supplied_Is_A_Clean_Rejection_Not_A_Guessed_Type()
    {
        // Every pre-#1636 call site passes no options at all. Such a request keeps working for every
        // non-preset filter; a preset is the one thing it cannot answer, and it says so.
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => AgGridRequestParser.Parse(Request("hireDate", "today")));

        Assert.Equal("malformed-request", ex.Reason);
        Assert.Contains("GridSchema", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Preset_On_A_Field_Absent_From_The_Schema_Is_A_Clean_Rejection()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => ParsePreset("today", Options(), field: "notDeclared"));

        Assert.Equal("malformed-request", ex.Reason);
        Assert.Contains("notDeclared", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Unresolvable_RequestContext_Zone_Is_Rejected_Rather_Than_Silently_Falling_Back()
    {
        var request = Request("hireDate", "today", """{ "zone": "Mars/Olympus_Mons" }""");

        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(request, Options()));

        Assert.Equal("malformed-request", ex.Reason);
        Assert.Contains("Mars/Olympus_Mons", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Romance Standard Time")] // a Windows id: resolves on .NET/Linux, keeps its spelling in TimeZoneInfo.Id
    [InlineData("localtime")]             // tzdata artifact: resolves to the SERVER's configured zone (SEM-4 forbids)
    [InlineData("Factory")]               // tzdata artifact: the deliberately-unset placeholder
    [InlineData("posixrules")]            // tzdata artifact: a legacy POSIX-TZ implementation detail
    public void A_Resolvable_But_NonIana_Zone_Is_Still_A_Clean_Typed_Rejection(string zoneId)
    {
        // Each of these RESOLVES via TimeZoneInfo.FindSystemTimeZoneById rather than throwing, so the
        // unresolvable-id guard above does not catch them. Without an explicit check they would reach
        // DatetimeValue.Zone: the Windows id then fails FilterNodeValidator's IANA shape (surfacing as
        // a structural-invariant exception with no Reason a host could map to a 400), while the three
        // tzdata artifacts PASS that shape check and would silently resolve preset boundaries in a
        // zone the client never named — 'localtime' being the server's own.
        var request = Request("hireDate", "today", $$"""{ "zone": "{{zoneId}}" }""");

        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(request, Options()));

        Assert.Equal("malformed-request", ex.Reason);
        Assert.Contains(zoneId, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_NonIana_Zone_Is_Rejected_On_A_Date_Field_Too_Where_No_Instant_Is_Ever_Built()
    {
        // A `date`-typed field never emits a Zone, so the downstream FilterNodeValidator check can't
        // catch a bad zone here at all — yet the zone still selects which calendar day "today" is.
        // Rejecting at resolution is what makes this consistent across both field types.
        var request = Request("hireDate", "today", """{ "zone": "localtime" }""");

        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(request, Options()));

        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void An_Unresolvable_Zone_Is_Rejected_Even_When_The_Request_Carries_No_Preset()
    {
        // Zone resolution is eager, for every request: a request declaring a zone the server cannot
        // resolve is malformed whether or not this particular filter shape happens to depend on it,
        // and failing at the same point every time is far easier to diagnose.
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": { "status": { "filterType": "text", "type": "equals", "filter": "active" } },
              "sortModel": [],
              "requestContext": { "zone": "Nowhere/AtAll" }
            }
            """;

        Assert.Equal("malformed-request",
            Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(request)).Reason);
    }

    [Theory]
    [InlineData("""{ "zone": 42 }""")]
    [InlineData("""{ "zone": ["UTC"] }""")]
    public void A_Malformed_RequestContext_Zone_Is_Rejected(string requestContextJson)
    {
        var request = Request("hireDate", "today", requestContextJson);

        Assert.Equal("malformed-request",
            Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(request, Options())).Reason);
    }

    [Fact]
    public void A_Non_Object_RequestContext_Is_Rejected()
    {
        var request = Request("hireDate", "today", "\"UTC\"");

        Assert.Equal("malformed-request",
            Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(request, Options())).Reason);
    }

    [Fact]
    public void An_Absent_Or_Null_RequestContext_Zone_Falls_Back_To_The_Configured_Default()
    {
        foreach (var contextJson in new[] { null, """{ "now": "2026-08-12T14:37:00Z" }""", """{ "zone": null }""" })
        {
            var comparison = Assert.IsType<ComparisonNode>(
                AgGridRequestParser.Parse(Request("hireDate", "today", contextJson), Options()).NativeFilter);

            Assert.Equal("2026-08-12", Assert.IsType<DateValue>(comparison.From).Value);
        }
    }

    [Theory]
    [InlineData("Today")]
    [InlineData("last8Days")]
    [InlineData("someFutureOperator")]
    public void A_Date_Type_That_Is_Neither_An_Operator_Nor_A_Preset_Is_Still_UnknownOperator(string type)
    {
        // #1635's behaviour, unchanged: the preset check runs BEFORE the operator switch precisely so
        // the switch's default arm keeps meaning "genuinely unknown condition type".
        var ex = Assert.Throws<AgGridRequestParseException>(() => ParsePreset(type, Options()));

        Assert.Equal("unknown-operator", ex.Reason);
    }
}
