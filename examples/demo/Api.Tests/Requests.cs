namespace Demo.Api.Tests;

/// <summary>
/// AG Grid SSRM request bodies against orders.schema.json. The shapes are copied from the spec fixtures
/// (spec/fixtures/operators/009, 025, 040, 041 and 005 for the filters, group-aggregation/001, 010, 016 and 022 for
/// grouping and set values, sort-paging/* for the window, page-export/004 for the export extension); only the column
/// ids and values were changed to this schema. Where AG Grid's native filterModel has no wire form (inRange on a date,
/// a datetime window) the fixtures use the <c>customFilters</c> IR fragment, and so do these.
/// </summary>
internal static class Requests
{
    private const string NoGrouping = "\"rowGroupCols\": [], \"valueCols\": [], \"pivotCols\": [], \"pivotMode\": false, \"groupKeys\": [],";

    /// <summary>Text contains + number greaterThan + sort (operators/025, 009), a 50-row page.</summary>
    public static string TextNumberSort => $$"""
        {
          "startRow": 0, "endRow": 50, {{NoGrouping}}
          "filterModel": {
            "customer": { "filterType": "text", "type": "contains", "filter": "GmbH" },
            "quantity": { "filterType": "number", "type": "greaterThan", "filter": 3 }
          },
          "sortModel": [ { "colId": "amount", "sort": "desc" } ]
        }
        """;

    /// <summary>operators/040: inRange on a date, from inclusive, to exclusive.</summary>
    public static string DateRange => $$"""
        {
          "startRow": 0, "endRow": 200, {{NoGrouping}}
          "filterModel": null,
          "sortModel": [ { "colId": "id", "sort": "asc" } ],
          "customFilters": {
            "type": "comparison", "field": "shippedOn", "op": "inRange",
            "from": { "type": "date", "value": "2025-03-01" }, "to": { "type": "date", "value": "2025-03-15" },
            "fromInclusive": true, "toInclusive": false
          }
        }
        """;

    /// <summary>operators/041: inRange on a datetime with an explicit zone, from exclusive, to inclusive.</summary>
    public static string DatetimeRange => $$"""
        {
          "startRow": 0, "endRow": 200, {{NoGrouping}}
          "filterModel": null,
          "sortModel": [ { "colId": "id", "sort": "asc" } ],
          "requestContext": { "zone": "UTC" },
          "customFilters": {
            "type": "comparison", "field": "orderedAt", "op": "inRange",
            "from": { "type": "datetime", "value": "2025-06-01T00:00:00Z", "zone": "UTC" },
            "to": { "type": "datetime", "value": "2025-06-03T00:00:00Z", "zone": "UTC" },
            "fromInclusive": false, "toInclusive": true
          }
        }
        """;

    /// <summary>operators/005: boolean eq true.</summary>
    public static string Boolean => $$"""
        {
          "startRow": 0, "endRow": 100, {{NoGrouping}}
          "filterModel": null,
          "sortModel": [ { "colId": "id", "sort": "asc" } ],
          "customFilters": { "type": "comparison", "field": "isPriority", "op": "eq", "value": { "type": "boolean", "value": true } }
        }
        """;

    /// <summary>group-aggregation/001 + 010: group by one column with sum, avg and count.</summary>
    public static string GroupByCountry => """
        {
          "startRow": 0, "endRow": 100,
          "rowGroupCols": [ { "id": "country", "displayName": "Country", "field": "country" } ],
          "valueCols": [
            { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "sum" },
            { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "avg" },
            { "id": "quantity", "displayName": "Quantity", "field": "quantity", "aggFunc": "count" }
          ],
          "pivotCols": [], "pivotMode": false, "groupKeys": [],
          "filterModel": null,
          "sortModel": [ { "colId": "country", "sort": "asc" } ]
        }
        """;

    /// <summary>group-aggregation drill-down: groupKeys [{"type":"string","value":"Germany"}] under a country grouping returns the leaf rows.</summary>
    public const string DrillDownGermany = """
        {
          "startRow": 0, "endRow": 100,
          "rowGroupCols": [ { "id": "country", "displayName": "Country", "field": "country" } ],
          "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [ { "type": "string", "value": "Germany" } ],
          "filterModel": null,
          "sortModel": [ { "colId": "id", "sort": "asc" } ]
        }
        """;

    /// <summary>A contains filter in capitals with a non-ASCII letter; the seed has "Müller, Söhne &amp; Co".</summary>
    public static string NonAsciiContains => FirstPage(
        """{ "customer": { "filterType": "text", "type": "contains", "filter": "SÖHNE" } }""");

    /// <summary>group-aggregation/022 shape: the set-filter values request.</summary>
    public static string CountryValues => """{ "setValuesRequest": { "colId": "country", "filterModel": {} } }""";

    /// <summary>A window far beyond the data (sort-paging/002 shape), filtered so every provider shares one total.</summary>
    public static string PastTheEnd => $$"""
        {
          "startRow": 30000, "endRow": 30100, {{NoGrouping}}
          "filterModel": { "customer": { "filterType": "text", "type": "contains", "filter": "GmbH" } },
          "sortModel": [ { "colId": "id", "sort": "asc" } ]
        }
        """;

    public static string FirstPage(string filterModelJson) => $$"""
        {
          "startRow": 0, "endRow": 100, {{NoGrouping}}
          "filterModel": {{filterModelJson}},
          "sortModel": [ { "colId": "id", "sort": "asc" } ]
        }
        """;

    /// <summary>page-export/004: the same request with exportFormat, here on a status filter.</summary>
    public static string StatusExport => $$"""
        {
          "startRow": null, "endRow": null, {{NoGrouping}}
          "filterModel": { "status": { "filterType": "text", "type": "equals", "filter": "shipped" } },
          "sortModel": [ { "colId": "id", "sort": "desc" } ],
          "exportFormat": "csv", "exportScope": "all"
        }
        """;

    public static string FormulaCustomer => $$"""
        {
          "startRow": null, "endRow": null, {{NoGrouping}}
          "filterModel": { "customer": { "filterType": "text", "type": "equals", "filter": "=SUM(A1)" } },
          "sortModel": [],
          "exportFormat": "csv"
        }
        """;

    public const string UnlikelyValue = "Zx9-needle-77";

    public static string NeedleFilter => $$"""
        {
          "startRow": 0, "endRow": 25, {{NoGrouping}}
          "filterModel": { "customer": { "filterType": "text", "type": "contains", "filter": "{{UnlikelyValue}}" } },
          "sortModel": [ { "colId": "id", "sort": "asc" } ]
        }
        """;

    public static string UnknownColumn => $$"""
        {
          "startRow": 0, "endRow": 50, {{NoGrouping}}
          "filterModel": { "passwordHash": { "filterType": "text", "type": "equals", "filter": "x" } },
          "sortModel": []
        }
        """;

    public static string PageSize(int rows) => $$"""
        { "startRow": 0, "endRow": {{rows}}, {{NoGrouping}} "filterModel": null, "sortModel": [] }
        """;
}
