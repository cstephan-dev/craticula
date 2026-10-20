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

    /// <summary>A datetime range whose bounds are NOT at midnight: on the bound's own date, a text comparison in a different form (a space instead of <c>T</c>) counts every row as later.</summary>
    public static string DatetimeRangeAtNoon => $$"""
        {
          "startRow": 0, "endRow": 200, {{NoGrouping}}
          "filterModel": null,
          "sortModel": [ { "colId": "id", "sort": "asc" } ],
          "requestContext": { "zone": "UTC" },
          "customFilters": {
            "type": "comparison", "field": "orderedAt", "op": "inRange",
            "from": { "type": "datetime", "value": "2025-06-01T12:00:00Z", "zone": "UTC" },
            "to": { "type": "datetime", "value": "2025-06-03T12:00:00Z", "zone": "UTC" },
            "fromInclusive": false, "toInclusive": true
          }
        }
        """;

    /// <summary>A one-sided datetime comparison at noon, so the total (<c>lastRow</c>) of every provider must agree.</summary>
    public static string DatetimeGreaterThanNoon => $$"""
        {
          "startRow": 0, "endRow": 200, {{NoGrouping}}
          "filterModel": null,
          "sortModel": [ { "colId": "id", "sort": "asc" } ],
          "requestContext": { "zone": "UTC" },
          "customFilters": {
            "type": "comparison", "field": "orderedAt", "op": "gt",
            "value": { "type": "datetime", "value": "2025-06-01T12:00:00Z", "zone": "UTC" }
          }
        }
        """;

    /// <summary>The whole table as CSV ordered by a datetime: the keyset walk of the export compares the datetime of each chunk's last row.</summary>
    public static string FullExportByOrderedAt(string direction) => $$"""
        {
          "startRow": null, "endRow": null, {{NoGrouping}}
          "filterModel": null,
          "sortModel": [ { "colId": "orderedAt", "sort": "{{direction}}" } ],
          "exportFormat": "csv", "exportScope": "all"
        }
        """;

    /// <summary>A text filter on a column; the ones that matter here are the binary-collated grouping columns (country, category, status).</summary>
    public static string TextFilter(string column, string type, string value) => FirstPage(
        $$"""{ "{{column}}": { "filterType": "text", "type": "{{type}}", "filter": "{{value}}" } }""");

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

    /// <summary>A grouping with the average of a whole-number column (quantity, BIGINT): SQL Server returns an integer for it.</summary>
    public static string GroupByWithQuantityAvg(string column) => $$"""
        {
          "startRow": 0, "endRow": 100,
          "rowGroupCols": [ { "id": "{{column}}", "displayName": "{{column}}", "field": "{{column}}" } ],
          "valueCols": [
            { "id": "quantity", "displayName": "Quantity", "field": "quantity", "aggFunc": "avg" },
            { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "avg" },
            { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "sum" },
            { "id": "quantity", "displayName": "Quantity", "field": "quantity", "aggFunc": "count" }
          ],
          "pivotCols": [], "pivotMode": false, "groupKeys": [],
          "filterModel": null,
          "sortModel": [ { "colId": "{{column}}", "sort": "asc" } ]
        }
        """;

    /// <summary>The set-filter values request with a native date filter and a customFilters date range (inside the envelope): the
    /// compiled statement binds date and datetime values as strings.</summary>
    public const string DateFilteredCountryValues = """
        { "setValuesRequest": { "colId": "country",
          "filterModel": { "orderedAt": { "filterType": "date", "type": "greaterThan", "dateFrom": "2026-05-01 00:00:00", "dateTo": null } },
          "customFilters": { "type": "comparison", "field": "shippedOn", "op": "inRange",
            "from": { "type": "date", "value": "2026-05-10" }, "to": { "type": "date", "value": "2026-05-12" },
            "fromInclusive": true, "toInclusive": false } } }
        """;

    /// <summary>The same with a datetime range of 90 minutes (zone UTC): few rows, so few countries.</summary>
    public const string DatetimeFilteredCountryValues = """
        { "setValuesRequest": { "colId": "country", "filterModel": {},
          "customFilters": { "type": "comparison", "field": "orderedAt", "op": "inRange",
            "from": { "type": "datetime", "value": "2025-06-01T00:00:00Z", "zone": "UTC" },
            "to": { "type": "datetime", "value": "2025-06-01T01:30:00Z", "zone": "UTC" },
            "fromInclusive": false, "toInclusive": true },
          "requestContext": { "zone": "UTC" } } }
        """;

    /// <summary>The same grouping on another column (country or category), ordered by that column.</summary>
    public static string GroupBy(string column) => GroupByCountry.Replace("country", column);

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

    /// <summary>The set-filter values request with a filter on another column: the compiled statement carries a parameter.</summary>
    public const string FilteredCountryValues =
        """{ "setValuesRequest": { "colId": "country", "filterModel": { "status": { "filterType": "text", "type": "equals", "filter": "shipped" } } } }""";

    /// <summary>The same contains filter in lower case: the only non-ASCII letter keeps its case, so SQLite's ASCII-only NOCASE matches.</summary>
    public static string NonAsciiContainsLower => FirstPage(
        """{ "customer": { "filterType": "text", "type": "contains", "filter": "söhne" } }""");

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

    /// <summary>The whole table as CSV, ordered by id: more than one chunk of the export walk.</summary>
    public static string FullExport => $$"""
        {
          "startRow": null, "endRow": null, {{NoGrouping}}
          "filterModel": null,
          "sortModel": [ { "colId": "id", "sort": "asc" } ],
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

    /// <summary>A page with a multi-column sort, in the order given (a column may repeat: the library drops the later occurrence, SEM-6).</summary>
    public static string MultiSort(int rows, params (string ColId, string Direction)[] sort) => $$"""
        { "startRow": 0, "endRow": {{rows}}, {{NoGrouping}} "filterModel": null, "sortModel": [ {{string.Join(", ", sort.Select(key => $"{{ \"colId\": \"{key.ColId}\", \"sort\": \"{key.Direction}\" }}"))}} ] }
        """;

    /// <summary>The whole table as CSV with a multi-column sort, in the order given.</summary>
    public static string MultiSortExport(params (string ColId, string Direction)[] sort) => $$"""
        { "startRow": null, "endRow": null, {{NoGrouping}} "filterModel": null, "sortModel": [ {{string.Join(", ", sort.Select(key => $"{{ \"colId\": \"{key.ColId}\", \"sort\": \"{key.Direction}\" }}"))}} ], "exportFormat": "csv", "exportScope": "all" }
        """;

    public static string PageSize(int rows) => $$"""
        { "startRow": 0, "endRow": {{rows}}, {{NoGrouping}} "filterModel": null, "sortModel": [] }
        """;
}
