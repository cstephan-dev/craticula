using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Bug #2319 (SEM-4 / SEM-5): a set filter's <c>values</c> on a <c>date</c> or <c>datetime</c> field follow the
/// comparison-filter rule per type — the zone-less AG Grid spellings are resolved by the adapter with the
/// request's zone; an ISO instant on a <c>datetime</c> field passes unchanged; anything else is a
/// <c>malformed-request</c> raised here, not a later coercion error.
/// </summary>
public class AgGridRequestParserSetDateValuesTests
{
    private const string SchemaJson = """
        {
          "mode": "strict", "source": "items", "stableKey": "id",
          "fields": {
            "id": { "type": "int64", "filterable": false, "sortable": true,
                    "physical": { "sql": { "column": "id" }, "mongo": { "path": "id" }, "search": { "field": "id" } } },
            "createdAt": { "type": "datetime", "filterable": true, "sortable": true,
                    "physical": { "sql": { "column": "created_at" }, "mongo": { "path": "createdAt" }, "search": { "field": "createdAt" } } },
            "birthDate": { "type": "date", "filterable": true, "sortable": true,
                    "physical": { "sql": { "column": "birth_date" }, "mongo": { "path": "birthDate" }, "search": { "field": "birthDate" } } }
          }
        }
        """;

    private static ComparisonNode Parse(string field, string valuesJson, string? zone = null, bool withSchema = true)
    {
        var context = zone is null ? "" : $$""", "requestContext": { "zone": "{{zone}}" }""";
        var request = $$"""
            { "startRow": 0, "endRow": 100, "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false,
              "groupKeys": [], "filterModel": { "{{field}}": { "filterType": "set", "values": {{valuesJson}} } },
              "sortModel": []{{context}} }
            """;
        var options = withSchema ? new AgGridParseOptions { Schema = GridSchema.Parse(SchemaJson) } : new AgGridParseOptions();
        return Assert.IsType<ComparisonNode>(AgGridRequestParser.Parse(request, options).NativeFilter);
    }

    [Fact]
    public void Datetime_ZoneLess_Values_Resolve_In_Utc_By_Default()
    {
        var comparison = Parse("createdAt", """["2026-01-01 00:00:00", "2026-06-15 12:30:00", "2026-03-02"]""");

        Assert.Equal(ComparisonOperator.In, comparison.Op);
        var values = comparison.Values!.Cast<DatetimeValue>().ToList();
        Assert.Equal(["2026-01-01T00:00:00Z", "2026-06-15T12:30:00Z", "2026-03-02T00:00:00Z"], values.Select(v => v.Value));
        Assert.All(values, v => Assert.Equal("UTC", v.Zone));
    }

    [Fact]
    public void Datetime_ZoneLess_Value_Uses_The_Request_Zone()
    {
        var comparison = Parse("createdAt", """["2026-01-15 09:30:00"]""", zone: "America/New_York");

        var value = Assert.IsType<DatetimeValue>(Assert.Single(comparison.Values!));
        Assert.Equal("2026-01-15T14:30:00Z", value.Value);
        Assert.Equal("America/New_York", value.Zone);
    }

    [Theory]
    [InlineData("2026-01-01T00:00:00Z")]
    [InlineData("2026-06-15T12:30:00.250Z")]
    [InlineData("2026-06-15T12:30:00+02:00")]
    public void Datetime_Iso_Instant_Passes_Unchanged(string iso)
    {
        var comparison = Parse("createdAt", $"[\"{iso}\"]", zone: "America/New_York");

        Assert.Equal(iso, Assert.IsType<StringValue>(Assert.Single(comparison.Values!)).Value);
    }

    [Fact]
    public void Date_ZoneLess_Midnight_And_DateOnly_Resolve_To_DateValue()
    {
        var comparison = Parse("birthDate", """["1990-05-12 00:00:00", "1991-07-04"]""", zone: "America/New_York");

        Assert.Equal(["1990-05-12", "1991-07-04"], comparison.Values!.Cast<DateValue>().Select(v => v.Value));
    }

    [Fact]
    public void Date_NonMidnight_Time_Is_MalformedRequest()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse("birthDate", """["1990-05-12 13:45:00"]"""));

        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void Date_Iso_Instant_Is_MalformedRequest()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse("birthDate", """["1990-05-12T00:00:00Z"]"""));

        Assert.Equal("malformed-request", ex.Reason);
    }

    [Theory]
    [InlineData("createdAt", "not a date")]
    [InlineData("createdAt", "2026-13-45 00:00:00")]
    [InlineData("createdAt", "2026-13-45T00:00:00Z")]
    [InlineData("birthDate", "12/05/1990")]
    [InlineData("birthDate", "1990-02-30")]
    public void Garbage_Is_MalformedRequest(string field, string raw)
    {
        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse(field, $"[\"{raw}\"]"));

        Assert.Equal("malformed-request", ex.Reason);
    }

    [Theory]
    [InlineData("0001-01-01 00:00:00", "Asia/Tokyo")]
    [InlineData("9999-12-31 23:59:59", "America/New_York")]
    public void A_Wall_Clock_Outside_The_Representable_Instant_Range_Is_MalformedRequest(string raw, string zone)
    {
        // SEC-6: one typed, reason-coded rejection — never an untyped ArgumentOutOfRangeException out of Parse.
        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse("createdAt", $"[\"{raw}\"]", zone: zone));

        Assert.Equal("malformed-request", ex.Reason);
    }

    [Theory]
    [InlineData("0001-01-01 00:00:00", "Asia/Tokyo")]
    [InlineData("9999-12-31 23:59:59", "America/New_York")]
    public void A_Comparison_Filter_Wall_Clock_Outside_The_Representable_Instant_Range_Is_MalformedRequest(string raw, string zone)
    {
        var request = $$"""
            { "startRow": 0, "endRow": 100, "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false,
              "groupKeys": [], "filterModel": { "createdAt": { "filterType": "date", "type": "equals", "dateFrom": "{{raw}}", "dateTo": null } },
              "sortModel": [], "requestContext": { "zone": "{{zone}}" } }
            """;
        var options = new AgGridParseOptions { Schema = GridSchema.Parse(SchemaJson) };

        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(request, options));

        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void Without_A_Schema_The_Verbatim_Value_Is_Kept()
    {
        var comparison = Parse("createdAt", """["2026-01-01 00:00:00", "garbage"]""", withSchema: false);

        Assert.Equal(["2026-01-01 00:00:00", "garbage"], comparison.Values!.Cast<StringValue>().Select(v => v.Value));
    }
}
